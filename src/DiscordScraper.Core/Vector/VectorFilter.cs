namespace DiscordScraper.Core.Vector;

/// <summary>
/// Equality note: IReadOnlyList&lt;T&gt; doesn't participate in record structural equality.
/// IEquatable&lt;VectorFilter&gt; gives structural equality for tests.
/// </summary>
public sealed record VectorFilter(
    long? GuildId = null,
    long? ChannelId = null,
    DateTimeOffset? After = null,
    DateTimeOffset? Before = null,
    IReadOnlyList<string>? AnyTags = null) : IEquatable<VectorFilter>
{
    public bool Equals(VectorFilter? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return GuildId == other.GuildId
            && ChannelId == other.ChannelId
            && After == other.After
            && Before == other.Before
            && NullableTagsEqual(AnyTags, other.AnyTags);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GuildId);
        hash.Add(ChannelId);
        hash.Add(After);
        hash.Add(Before);
        if (AnyTags is not null)
            foreach (var tag in AnyTags) hash.Add(tag);
        return hash.ToHashCode();
    }

    private static bool NullableTagsEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.SequenceEqual(b);
    }
}
