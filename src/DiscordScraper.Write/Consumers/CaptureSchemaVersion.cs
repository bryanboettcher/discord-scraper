namespace DiscordScraper.Write.Consumers;

/// <summary>
/// Schema version for JSONL capture files. Increment when any captured fact-record shape
/// changes in a way that breaks deserialization. Additive changes (new optional fields) do
/// not require a bump — the replay loader uses lenient deserialization.
/// </summary>
internal static class CaptureSchemaVersion
{
    /// <summary>Current schema version embedded in every capture file header.</summary>
    public const int Current = 1;
}
