namespace DiscordScraper.Storage.Entities;

/// <summary>
/// Tier 2 projection layered on top of <see cref="MessageEntity"/>. Holds
/// LLM-derived metadata and the pointer into Qdrant. Also disposable:
/// bumping <c>Enrichment:Version</c> in config causes the worker to
/// re-process every row whose <see cref="EnrichmentVersion"/> is stale.
/// </summary>
public sealed class MessageEnrichmentEntity
{
    public long MessageId { get; set; }
    public required string EmbeddingModel { get; set; }
    public required string QdrantPointId { get; set; }
    public required string[] TopicTags { get; set; }
    public bool IsSubstantive { get; set; }
    public DateTimeOffset EnrichedAt { get; set; }
    public int EnrichmentVersion { get; set; }

    public MessageEntity? Message { get; set; }
}
