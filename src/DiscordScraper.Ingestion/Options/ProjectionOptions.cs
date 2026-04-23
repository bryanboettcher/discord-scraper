namespace DiscordScraper.Ingestion.Options;

public sealed class ProjectionOptions
{
    public const string SectionName = "Projection";

    /// <summary>How many raw messages the projection worker pulls + upserts
    /// per round-trip. Larger = fewer round-trips, more memory per pass.</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>How long the worker sleeps after a pass before re-checking for
    /// new unprojected raw_messages. Short intervals are fine — the un-project
    /// query is cheap when the backlog is empty.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
}
