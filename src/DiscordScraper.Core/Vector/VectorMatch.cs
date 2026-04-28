namespace DiscordScraper.Core.Vector;

/// <summary>
/// Equality note: IReadOnlyList&lt;T&gt; doesn't participate in record structural equality.
/// IEquatable&lt;VectorMatch&gt; gives structural equality for tests.
/// </summary>
public sealed record VectorMatch(
    long MessageId,
    float Score,
    long ChannelId,
    long GuildId,
    long AuthorId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Tags) : IEquatable<VectorMatch>
{
    public bool Equals(VectorMatch? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return MessageId == other.MessageId
            && Score == other.Score
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
        hash.Add(Score);
        hash.Add(ChannelId);
        hash.Add(GuildId);
        hash.Add(AuthorId);
        hash.Add(CreatedAt);
        foreach (var tag in Tags) hash.Add(tag);
        return hash.ToHashCode();
    }
}
