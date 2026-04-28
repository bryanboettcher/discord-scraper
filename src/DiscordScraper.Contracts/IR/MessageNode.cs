using System.Text.Json.Serialization;

namespace DiscordScraper.Contracts.IR;

/// <summary>
/// Polymorphic base for IR AST nodes. JsonDerivedType attributes configure STJ discriminator-based
/// deserialization so the hierarchy survives JSON round-trips in both the read-side jsonb column
/// and any API payloads. The BSON saga side uses the MongoDB driver's own discriminator mechanism;
/// keep these type names short to avoid BSON bloat.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(TextNode), "text")]
[JsonDerivedType(typeof(MentionNode), "mention")]
[JsonDerivedType(typeof(ChannelRefNode), "chan")]
[JsonDerivedType(typeof(EmojiNode), "emoji")]
[JsonDerivedType(typeof(TimestampNode), "ts")]
[JsonDerivedType(typeof(FormattingNode), "fmt")]
[JsonDerivedType(typeof(CodeBlockNode), "code")]
[JsonDerivedType(typeof(InlineCodeNode), "icode")]
[JsonDerivedType(typeof(QuoteNode), "quote")]
[JsonDerivedType(typeof(LinkNode), "link")]
public abstract record MessageNode;

public sealed record TextNode(string Text) : MessageNode;

/// <param name="Fallback">
/// Display name captured at projection time. Populated from payload's mentions[] for user mentions;
/// null for role/channel refs (resolved later by ProjectMessageConsumer via repo lookup).
/// </param>
public sealed record MentionNode(MentionKind Kind, long? Id, string? Fallback) : MessageNode;

/// <param name="Fallback">
/// Channel display name captured at projection time. Populated when the ref targets the home channel
/// (ChannelSyncConsumer stamps it onto MessageCaptured); null for cross-channel refs (3C resolves).
/// </param>
public sealed record ChannelRefNode(long ChannelId, string? Fallback) : MessageNode;

public sealed record EmojiNode(EmojiKind Kind, long? Id, string NameOrGlyph, bool Animated) : MessageNode;

public sealed record TimestampNode(DateTimeOffset Value, TimestampStyle Style) : MessageNode;

/// <summary>Children list is IReadOnlyList; override equality to compare by sequence, not reference.</summary>
public sealed record FormattingNode(FormattingKind Kind, IReadOnlyList<MessageNode> Children) : MessageNode
{
    public bool Equals(FormattingNode? other) =>
        other is not null && Kind == other.Kind && Children.SequenceEqual(other.Children);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Kind);
        foreach (var n in Children) h.Add(n);
        return h.ToHashCode();
    }
}

public sealed record CodeBlockNode(string? Language, string Content) : MessageNode;

public sealed record InlineCodeNode(string Content) : MessageNode;

/// <summary>Children list is IReadOnlyList; override equality to compare by sequence, not reference.</summary>
public sealed record QuoteNode(IReadOnlyList<MessageNode> Children) : MessageNode
{
    public bool Equals(QuoteNode? other) =>
        other is not null && Children.SequenceEqual(other.Children);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var n in Children) h.Add(n);
        return h.ToHashCode();
    }
}

/// <summary>DisplayChildren list is IReadOnlyList; override equality to compare by sequence, not reference.</summary>
public sealed record LinkNode(string Url, IReadOnlyList<MessageNode> DisplayChildren) : MessageNode
{
    public bool Equals(LinkNode? other) =>
        other is not null && Url == other.Url && DisplayChildren.SequenceEqual(other.DisplayChildren);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Url);
        foreach (var n in DisplayChildren) h.Add(n);
        return h.ToHashCode();
    }
}
