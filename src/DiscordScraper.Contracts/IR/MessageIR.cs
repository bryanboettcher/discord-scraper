namespace DiscordScraper.Contracts.IR;

/// <summary>
/// Typed AST of a Discord message. Immutable; produced once at projection time and stored
/// as a BSON sub-document (saga) / jsonb column (read side).
///
/// Equality note: record auto-equality on IReadOnlyList properties uses reference comparison,
/// not sequence comparison. We override Equals/GetHashCode here to provide structural equality
/// so test assertions and deduplication logic work correctly. The trade-off is O(n) equality
/// cost — acceptable since MessageIR equality is only needed in tests and idempotency checks,
/// never in hot paths.
/// </summary>
public sealed record MessageIR(
    IReadOnlyList<MessageNode> Body,
    IReadOnlyList<AttachmentIR> Attachments,
    IReadOnlyList<EmbedIR> Embeds,
    ReplyContext? ReplyTo,
    DateTimeOffset CapturedAt) : IEquatable<MessageIR>
{
    public bool Equals(MessageIR? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return CapturedAt == other.CapturedAt
            && ReplyTo == other.ReplyTo
            && Body.SequenceEqual(other.Body)
            && Attachments.SequenceEqual(other.Attachments)
            && Embeds.SequenceEqual(other.Embeds);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CapturedAt);
        hash.Add(ReplyTo);
        foreach (var node in Body) hash.Add(node);
        foreach (var att in Attachments) hash.Add(att);
        foreach (var emb in Embeds) hash.Add(emb);
        return hash.ToHashCode();
    }
}
