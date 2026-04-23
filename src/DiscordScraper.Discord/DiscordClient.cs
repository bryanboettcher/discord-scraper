using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DiscordScraper.Discord.Models;
using DiscordScraper.Discord.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Discord;

internal sealed class DiscordClient(
    HttpClient http,
    IOptions<DiscordOptions> options,
    ILogger<DiscordClient> logger) : IDiscordClient
{
    private readonly DiscordOptions _options = options.Value;

    // Discord returns a 100-message cap on the messages endpoint; the thread
    // archive endpoint is capped at 100 too per API docs.
    private const int ArchivedThreadPageSize = 100;

    public async Task<IReadOnlyList<DiscordGuildRaw>> GetCurrentUserGuildsAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "users/@me/guilds", ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var result = new List<DiscordGuildRaw>(doc.RootElement.GetArrayLength());
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            result.Add(new DiscordGuildRaw(
                GuildId: ParseSnowflake(element, "id"),
                Name: element.GetProperty("name").GetString() ?? "",
                Payload: element.GetRawText()));
        }
        return result;
    }

    public async Task<DiscordGuildRaw> GetGuildAsync(string guildId, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"guilds/{guildId}?with_counts=true", ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        return new DiscordGuildRaw(
            GuildId: ParseSnowflake(doc.RootElement, "id"),
            Name: doc.RootElement.GetProperty("name").GetString() ?? "",
            Payload: json);
    }

    public async Task<IReadOnlyList<DiscordChannelRaw>> GetGuildChannelsAsync(string guildId, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"guilds/{guildId}/channels", ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var result = new List<DiscordChannelRaw>(doc.RootElement.GetArrayLength());
        foreach (var element in doc.RootElement.EnumerateArray())
            result.Add(ParseChannel(element, fallbackGuildId: ParseSnowflakeOrZero(guildId)));
        return result;
    }

    public async Task<IReadOnlyList<DiscordChannelRaw>> GetGuildActiveThreadsAsync(string guildId, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"guilds/{guildId}/threads/active", ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("threads", out var threads))
            return [];

        var result = new List<DiscordChannelRaw>(threads.GetArrayLength());
        foreach (var element in threads.EnumerateArray())
            result.Add(ParseChannel(element, fallbackGuildId: ParseSnowflakeOrZero(guildId)));
        return result;
    }

    public async IAsyncEnumerable<DiscordChannelRaw> EnumerateArchivedThreadsAsync(
        string channelId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Pagination cursor for this endpoint is the oldest archive_timestamp
        // we've seen so far (ISO 8601). First request omits `before`.
        string? beforeTimestamp = null;

        while (!ct.IsCancellationRequested)
        {
            var path = beforeTimestamp is null
                ? $"channels/{channelId}/threads/archived/public?limit={ArchivedThreadPageSize}"
                : $"channels/{channelId}/threads/archived/public?limit={ArchivedThreadPageSize}&before={Uri.EscapeDataString(beforeTimestamp)}";

            using var response = await SendAsync(HttpMethod.Get, path, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("threads", out var threads) || threads.GetArrayLength() == 0)
                yield break;

            string? oldestArchive = null;
            foreach (var element in threads.EnumerateArray())
            {
                var parsed = ParseChannel(element, fallbackGuildId: 0);
                yield return parsed;

                // Track the oldest archive timestamp in this page for the next
                // `before` cursor. Discord returns newest-first so the last
                // element is the oldest.
                if (element.TryGetProperty("thread_metadata", out var meta) &&
                    meta.TryGetProperty("archive_timestamp", out var archive) &&
                    archive.ValueKind == JsonValueKind.String)
                {
                    oldestArchive = archive.GetString();
                }
            }

            var hasMore = doc.RootElement.TryGetProperty("has_more", out var hm) && hm.GetBoolean();
            if (!hasMore || oldestArchive is null) yield break;
            beforeTimestamp = oldestArchive;
        }
    }

    public async IAsyncEnumerable<DiscordMessageRaw> EnumerateChannelMessagesAsync(
        string channelId,
        long guildId,
        long afterSnowflake,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var cursor = afterSnowflake;
        var pageSize = _options.MaxPageSize;

        while (!ct.IsCancellationRequested)
        {
            // `after=0` means "everything": Discord treats the cursor as an
            // exclusive lower bound on the snowflake.
            var path = $"channels/{channelId}/messages?limit={pageSize}&after={cursor}";

            using var response = await SendAsync(HttpMethod.Get, path, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var count = doc.RootElement.GetArrayLength();
            if (count == 0) yield break;

            long maxId = cursor;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var messageId = ParseSnowflake(element, "id");
                var channelIdLong = ParseSnowflake(element, "channel_id");

                yield return new DiscordMessageRaw(
                    MessageId: messageId,
                    ChannelId: channelIdLong,
                    GuildId: guildId,
                    CreatedAt: Snowflake.ToTimestamp(messageId),
                    Payload: element.GetRawText());

                if (messageId > maxId) maxId = messageId;
            }

            cursor = maxId;
            if (count < pageSize) yield break;
        }
    }

    // -------------------------------------------------------------------------
    // Transport
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a request honoring Discord's per-route rate limits: retries on
    /// 429 using <c>Retry-After</c>, and delays after each response when the
    /// bucket drains (remaining == 0) so the next call doesn't immediately 429.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        const int maxRetries = 3;

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt <= maxRetries)
            {
                var retryAfter = GetRetryAfter(response) ?? TimeSpan.FromSeconds(1);
                var global = response.Headers.TryGetValues("X-RateLimit-Global", out _);
                logger.LogWarning(
                    "Discord 429 on {Method} {Path} (attempt {Attempt}/{Max}, global={Global}); retrying in {Delay}",
                    method, path, attempt, maxRetries, global, retryAfter);
                response.Dispose();
                await Task.Delay(retryAfter, ct);
                continue;
            }

            response.EnsureSuccessStatusCode();

            // Proactive pacing: a drained bucket means the very next call on
            // this route will 429. Sleeping for Reset-After avoids the retry
            // round-trip entirely.
            if (TryGetDrainedBucketDelay(response, out var pacingDelay))
            {
                logger.LogDebug(
                    "Rate-limit bucket drained for {Path}; pacing {Delay} before next call",
                    path, pacingDelay);
                await Task.Delay(pacingDelay, ct);
            }

            return response;
        }
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var raw = values.FirstOrDefault();
            if (raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                return TimeSpan.FromSeconds(Math.Max(0.1, seconds));
        }

        // Discord also echoes retry_after inside the JSON body, but reading it
        // consumes the stream. Headers are authoritative for bot-token routes.
        return null;
    }

    private static bool TryGetDrainedBucketDelay(HttpResponseMessage response, out TimeSpan delay)
    {
        delay = default;

        if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining))
            return false;
        if (!int.TryParse(remaining.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) || r > 0)
            return false;

        if (!response.Headers.TryGetValues("X-RateLimit-Reset-After", out var reset))
            return false;
        if (!double.TryParse(reset.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            return false;

        delay = TimeSpan.FromSeconds(seconds);
        return true;
    }

    // -------------------------------------------------------------------------
    // JSON helpers
    // -------------------------------------------------------------------------

    private static DiscordChannelRaw ParseChannel(JsonElement element, long fallbackGuildId)
    {
        var channelId = ParseSnowflake(element, "id");
        var guildId = element.TryGetProperty("guild_id", out var g) && g.ValueKind == JsonValueKind.String
            ? ParseSnowflakeOrZero(g.GetString()!)
            : fallbackGuildId;

        var type = element.TryGetProperty("type", out var t) ? t.GetInt32() : 0;
        var parentId = element.TryGetProperty("parent_id", out var p) && p.ValueKind == JsonValueKind.String
            ? (long?)ParseSnowflakeOrZero(p.GetString()!)
            : null;
        var name = element.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? ""
            : "";

        return new DiscordChannelRaw(
            ChannelId: channelId,
            GuildId: guildId,
            Type: type,
            ParentId: parentId,
            Name: name,
            Payload: element.GetRawText());
    }

    private static long ParseSnowflake(JsonElement element, string propertyName) =>
        long.Parse(element.GetProperty(propertyName).GetString()!, CultureInfo.InvariantCulture);

    private static long ParseSnowflakeOrZero(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0L;
}
