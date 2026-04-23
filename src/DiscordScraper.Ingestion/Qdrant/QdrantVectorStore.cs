using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using DiscordScraper.Ingestion.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Ingestion.Qdrant;

internal sealed class QdrantVectorStore(
    HttpClient http,
    IOptions<QdrantOptions> options,
    ILogger<QdrantVectorStore> logger) : IQdrantVectorStore
{
    private readonly QdrantOptions _options = options.Value;

    public async Task EnsureCollectionAsync(int vectorSize, CancellationToken ct = default)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/collections/{_options.CollectionName}";

        using var probe = await http.GetAsync(url, ct);
        if (probe.IsSuccessStatusCode)
        {
            logger.LogDebug("Qdrant collection {Collection} exists", _options.CollectionName);
            return;
        }

        if (probe.StatusCode != HttpStatusCode.NotFound)
            probe.EnsureSuccessStatusCode();

        logger.LogInformation(
            "Creating Qdrant collection {Collection} ({Size}d cosine)",
            _options.CollectionName, vectorSize);

        var body = new
        {
            vectors = new
            {
                size = vectorSize,
                distance = "Cosine",
            },
        };

        using var create = await http.PutAsJsonAsync(url, body, ct);
        create.EnsureSuccessStatusCode();
    }

    public async Task UpsertAsync(MessageVectorRecord record, CancellationToken ct = default)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/collections/{_options.CollectionName}/points?wait=true";

        var payload = new Dictionary<string, object?>
        {
            ["message_id"] = record.MessageId,
            ["channel_id"] = record.ChannelId,
            ["guild_id"] = record.GuildId,
            ["root_channel_id"] = record.RootChannelId,
            ["thread_id"] = record.ThreadId,
            ["author_name"] = record.AuthorName,
            ["created_at"] = record.CreatedAt.UtcDateTime.ToString("o"),
            ["topic_tags"] = record.TopicTags,
            ["content"] = record.Content,
        };

        var body = new
        {
            points = new[]
            {
                new
                {
                    id = record.PointId,
                    vector = record.Vector.ToArray(),
                    payload,
                },
            },
        };

        using var response = await http.PutAsJsonAsync(url, body, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Deterministic UUID derived from <c>sha256(message_id:model)[0..16]</c>.
    /// Qdrant requires point ids to be either integers or UUIDs; this shape
    /// survives model changes because bumping the model produces new UUIDs
    /// and the stale ones are orphaned (but safely overwritten on re-enrich).
    /// </summary>
    public static string GeneratePointId(long messageId, string embeddingModel)
    {
        var input = $"{messageId}:{embeddingModel}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }
}
