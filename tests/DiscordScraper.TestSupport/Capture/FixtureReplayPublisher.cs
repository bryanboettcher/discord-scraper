using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Contracts.Events.Message;
using MassTransit;

namespace DiscordScraper.TestSupport.Capture;

/// <summary>
/// Reads an embedded JSONL fixture and republishes each captured body through <see cref="IBus"/>.
/// MT regenerates fresh correlation/conversation/header state per run; the body's own fields
/// (snowflakes, Discord timestamps) are sufficient for downstream saga and enrichment behavior.
///
/// The replay bypasses the Discord scraper layer entirely. Parser regression is a separate
/// concern covered by per-consumer unit tests; see ADR-003 for the accepted trade-off.
/// </summary>
public sealed class FixtureReplayPublisher(IBus bus)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ---------------------------------------------------------------------------
    // Factory helpers for loading from embedded resources
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Loads a fixture from an embedded resource in <paramref name="hostAssembly"/>.
    /// The resource name is matched by suffix (e.g., "my_fixture.jsonl") — no path prefix needed.
    /// Transparently decompresses .jsonl.gz resources.
    /// </summary>
    public static IEnumerable<CaptureEnvelope> LoadEnvelopes(Assembly hostAssembly, string resourceSuffix)
    {
        var name = hostAssembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Embedded resource ending with '{resourceSuffix}' not found in {hostAssembly.GetName().Name}. " +
                $"Available: {string.Join(", ", hostAssembly.GetManifestResourceNames())}");

        using var raw = hostAssembly.GetManifestResourceStream(name)!;
        Stream stream = name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(raw, CompressionMode.Decompress)
            : raw;

        return ReadEnvelopes(stream).ToList(); // materialize while stream is open
    }

    private static IEnumerable<CaptureEnvelope> ReadEnvelopes(Stream stream)
    {
        using var reader = new StreamReader(stream);
        string? line;
        var lineNumber = 0;

        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // First line is always the header — validate and skip.
            if (lineNumber == 1)
            {
                var header = JsonSerializer.Deserialize<CaptureHeader>(line, JsonOpts)
                             ?? throw new InvalidOperationException($"JSONL line 1 is not a valid header: {line}");

                if (header.SchemaVersion > CaptureHeader.SupportedVersion)
                    throw new InvalidOperationException(
                        $"Fixture schema version {header.SchemaVersion} is not supported by this loader " +
                        $"(max: {CaptureHeader.SupportedVersion}). Regenerate the fixture.");
                continue;
            }

            var envelope = JsonSerializer.Deserialize<CaptureEnvelope>(line, JsonOpts)
                           ?? throw new InvalidOperationException($"Failed to deserialize JSONL line {lineNumber}: {line}");

            yield return envelope;
        }
    }

    // ---------------------------------------------------------------------------
    // Replay
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Burst mode: republish every envelope as fast as the bus accepts.
    /// </summary>
    public async Task ReplayBurstAsync(IEnumerable<CaptureEnvelope> envelopes, CancellationToken ct = default)
    {
        foreach (var envelope in envelopes)
        {
            ct.ThrowIfCancellationRequested();
            await PublishEnvelopeAsync(envelope, ct);
        }
    }

    /// <summary>
    /// Paced mode: replay messages with delays derived from the body's own timestamp field,
    /// preserving the original inter-message cadence relative to the first message.
    /// </summary>
    public async Task ReplayPacedAsync(IEnumerable<CaptureEnvelope> envelopes, CancellationToken ct = default)
    {
        DateTimeOffset? firstTs = null;
        var wallStart = DateTimeOffset.UtcNow;

        foreach (var envelope in envelopes)
        {
            ct.ThrowIfCancellationRequested();

            var bodyTs = ExtractTimestamp(envelope);
            if (bodyTs is not null)
            {
                firstTs ??= bodyTs;
                var targetElapsed = bodyTs.Value - firstTs.Value;
                var actualElapsed = DateTimeOffset.UtcNow - wallStart;
                var delay = targetElapsed - actualElapsed;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct);
            }

            await PublishEnvelopeAsync(envelope, ct);
        }
    }

    // ---------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------

    private async Task PublishEnvelopeAsync(CaptureEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Kind)
        {
            case "messageCaptured":
                var mc = Deserialize<MessageCapturedBody>(envelope);
                await bus.Publish<MessageCaptured>(mc, ct);
                break;

            case "messageEditObserved":
                var me = Deserialize<MessageEditObservedBody>(envelope);
                await bus.Publish<MessageEditObserved>(me, ct);
                break;

            case "channelChanged":
                var cc = Deserialize<ChannelChangedBody>(envelope);
                await bus.Publish<ChannelChanged>(cc, ct);
                break;

            case "guildChanged":
                var gc = Deserialize<GuildChangedBody>(envelope);
                await bus.Publish<GuildChanged>(gc, ct);
                break;

            default:
                // Unknown kinds are silently skipped for forward-compatibility with future additions.
                break;
        }
    }

    private static T Deserialize<T>(CaptureEnvelope envelope)
        => envelope.Data.Deserialize<T>(JsonOpts)
           ?? throw new InvalidOperationException(
               $"Failed to deserialize '{envelope.Kind}' data as {typeof(T).Name}");

    private static DateTimeOffset? ExtractTimestamp(CaptureEnvelope envelope)
    {
        if (envelope.Data.TryGetProperty("updatedOn", out var prop) ||
            envelope.Data.TryGetProperty("UpdatedOn", out prop))
        {
            if (prop.TryGetDateTimeOffset(out var ts))
                return ts;
        }
        return null;
    }
}
