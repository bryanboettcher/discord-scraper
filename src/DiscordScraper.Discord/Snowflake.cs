namespace DiscordScraper.Discord;

/// <summary>
/// Helpers for decoding Discord snowflake IDs. A snowflake encodes a UTC
/// millisecond timestamp (relative to the Discord epoch) in its upper 42 bits
/// plus 22 bits of worker/process/increment metadata.
/// </summary>
public static class Snowflake
{
    /// <summary>Discord epoch: 2015-01-01T00:00:00Z in Unix milliseconds.</summary>
    public const long DiscordEpochMs = 1420070400000L;

    public static DateTimeOffset ToTimestamp(long snowflake)
    {
        var unixMs = (snowflake >> 22) + DiscordEpochMs;
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }
}
