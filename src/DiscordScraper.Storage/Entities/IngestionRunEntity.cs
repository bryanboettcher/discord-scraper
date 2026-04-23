namespace DiscordScraper.Storage.Entities;

/// <summary>
/// Observability log of individual worker runs. Populated by the sync,
/// projection, and enrichment workers on each wake-up so the admin API can
/// render a timeline.
/// </summary>
public sealed class IngestionRunEntity
{
    public long Id { get; set; }
    public required string Worker { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required string Status { get; set; }
    public int ItemsProcessed { get; set; }
    public string? Error { get; set; }
}
