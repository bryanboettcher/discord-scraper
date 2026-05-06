namespace DiscordScraper.Contracts.Configuration;

/// <summary>
/// Controls the MessageCaptureConsumer.
/// When Enabled == false the consumer body no-ops; the full MT consume pipeline still runs
/// (deserialization, scope creation, middleware) as a deliberate passive load generator.
/// Binds to "Capture".
/// </summary>
public sealed class CaptureOptions
{
    public const string SectionName = "Capture";

    /// <summary>When false the consumer no-ops without writing to OutputPath.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Directory path for JSONL capture files. One file per capture session, named
    /// {guildId}_{yyyyMMddTHHmmss}.jsonl. Ignored when Enabled == false.
    /// </summary>
    public string OutputPath { get; init; } = "capture";
}
