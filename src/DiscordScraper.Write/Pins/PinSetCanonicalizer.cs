using System.Security.Cryptography;
using System.Text;

namespace DiscordScraper.Write.Pins;

/// <summary>
/// Snapshot of a single pinned message used for canonical comparison.
/// EditedAt comes from edited_timestamp on the Discord message object;
/// null means the message has never been edited.
/// </summary>
public sealed record PinSnapshot(long MessageId, DateTimeOffset? EditedAt);

/// <summary>
/// Pure helpers for producing a stable canonical representation of a pin set.
/// The canonical is the hex-encoded SHA-256 of a deterministic encoding of the set —
/// two pin sets with the same messages and the same edit timestamps always produce the
/// same hash regardless of the order Discord returns them.
/// </summary>
public static class PinSetCanonicalizer
{
    /// <summary>
    /// Canonicalizes a pin set for stable comparison.
    ///
    /// Encoding: "messageId:editedAtIso|messageId:editedAtIso|..." sorted by messageId
    /// ascending, where editedAt is formatted as ISO 8601 UTC (round-trip "O" specifier
    /// normalized to UTC) and missing editedAt is encoded as the empty string.
    ///
    /// Timezone normalization: DateTimeOffset values are converted to UTC before formatting
    /// so (+00:00) and (Z) compare equal and offsets from non-UTC timezones do not produce
    /// different hashes for the same instant.
    /// </summary>
    public static string Canonicalize(IEnumerable<PinSnapshot> pins)
    {
        var sorted = pins
            .OrderBy(p => p.MessageId)
            .Select(p =>
            {
                // Normalize to UTC and round-trip format.  EditedAt=null → empty string.
                var editedPart = p.EditedAt.HasValue
                    ? p.EditedAt.Value.ToUniversalTime().ToString("O")
                    : string.Empty;
                return $"{p.MessageId}:{editedPart}";
            });

        var canonical = string.Join('|', sorted);

        // Hash the UTF-8 encoding.  Empty pin set produces hash of the empty string —
        // stable and distinct from any non-empty set.
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
