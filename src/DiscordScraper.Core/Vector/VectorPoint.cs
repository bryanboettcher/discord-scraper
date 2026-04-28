namespace DiscordScraper.Core.Vector;

/// <summary>
/// Equality note: ReadOnlyMemory&lt;T&gt; and IReadOnlyList&lt;T&gt; don't participate in record
/// structural equality. We implement IEquatable&lt;VectorPoint&gt; to give value semantics so
/// test assertions work correctly. Cost is O(n) — acceptable since equality is never on a hot path.
/// </summary>
public sealed record VectorPoint(
    long MessageId,
    ReadOnlyMemory<float> Embedding,
    long ChannelId,
    long GuildId,
    long AuthorId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Tags) : IEquatable<VectorPoint>
{
    public bool Equals(VectorPoint? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return MessageId == other.MessageId
            && Embedding.Span.SequenceEqual(other.Embedding.Span)
            && ChannelId == other.ChannelId
            && GuildId == other.GuildId
            && AuthorId == other.AuthorId
            && CreatedAt == other.CreatedAt
            && Tags.SequenceEqual(other.Tags);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(MessageId);
        hash.Add(ChannelId);
        hash.Add(GuildId);
        hash.Add(AuthorId);
        hash.Add(CreatedAt);
        foreach (var tag in Tags) hash.Add(tag);
        // Embedding excluded from hash for performance; equality check covers it.
        return hash.ToHashCode();
    }
}
