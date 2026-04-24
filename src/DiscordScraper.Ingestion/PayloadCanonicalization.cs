using System.Text;
using System.Text.Json;

namespace DiscordScraper.Ingestion;

/// <summary>
/// Produces canonical strings from Discord guild/channel JSON payloads for
/// snapshot-on-change comparisons. Volatile fields (member counts, last message
/// id, rate limit bucket data, etc.) are intentionally excluded so the raw_*
/// tables don't accrete a new row on every sync pass.
/// </summary>
internal static class PayloadCanonicalization
{
    public static string GuildCanonical(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var sb = new StringBuilder();
        AppendString(sb, doc.RootElement, "name");
        AppendString(sb, doc.RootElement, "description");
        AppendString(sb, doc.RootElement, "icon");
        AppendInt(sb, doc.RootElement, "verification_level");
        AppendSortedStringArray(sb, doc.RootElement, "features");
        return sb.ToString();
    }

    /// <summary>
    /// Stable identity of a channel's pin set: the sorted list of pinned
    /// message IDs. Order of the upstream array isn't authoritative — Discord
    /// returns pins newest-first by pin time, and the set of pinned IDs is the
    /// only thing we care about for snapshot-on-change.
    /// </summary>
    public static string PinSetCanonical(string pinsArrayJson)
    {
        using var doc = JsonDocument.Parse(pinsArrayJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return "";

        var ids = new List<string>(doc.RootElement.GetArrayLength());
        foreach (var message in doc.RootElement.EnumerateArray())
        {
            if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                ids.Add(id.GetString() ?? "");
        }
        ids.Sort(StringComparer.Ordinal);
        return string.Join(",", ids);
    }

    public static string ChannelCanonical(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        var sb = new StringBuilder();
        AppendString(sb, root, "name");
        AppendString(sb, root, "topic");
        AppendString(sb, root, "parent_id");
        AppendBool(sb, root, "nsfw");
        AppendInt(sb, root, "type");
        AppendInt(sb, root, "position");
        AppendInt(sb, root, "rate_limit_per_user");

        if (root.TryGetProperty("thread_metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            AppendBool(sb, meta, "archived");
            AppendBool(sb, meta, "locked");
        }

        return sb.ToString();
    }

    private static void AppendString(StringBuilder sb, JsonElement root, string property)
    {
        sb.Append(property).Append('=');
        if (root.TryGetProperty(property, out var e) && e.ValueKind == JsonValueKind.String)
            sb.Append(e.GetString());
        sb.Append(';');
    }

    private static void AppendBool(StringBuilder sb, JsonElement root, string property)
    {
        sb.Append(property).Append('=');
        if (root.TryGetProperty(property, out var e) && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False))
            sb.Append(e.GetBoolean() ? '1' : '0');
        sb.Append(';');
    }

    private static void AppendInt(StringBuilder sb, JsonElement root, string property)
    {
        sb.Append(property).Append('=');
        if (root.TryGetProperty(property, out var e) && e.ValueKind == JsonValueKind.Number)
            sb.Append(e.GetInt64());
        sb.Append(';');
    }

    private static void AppendSortedStringArray(StringBuilder sb, JsonElement root, string property)
    {
        sb.Append(property).Append('=');
        if (root.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var items = new List<string>(arr.GetArrayLength());
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    items.Add(item.GetString() ?? "");
            items.Sort(StringComparer.Ordinal);
            sb.Append(string.Join(',', items));
        }
        sb.Append(';');
    }
}
