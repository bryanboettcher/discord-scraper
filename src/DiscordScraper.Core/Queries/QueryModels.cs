namespace DiscordScraper.Core.Queries;

public sealed record MessageSearchQuery(
    string? TextQuery,
    ReadOnlyMemory<float>? Embedding,
    long? GuildId,
    long? ChannelId,
    long? AuthorId,
    DateTimeOffset? After,
    DateTimeOffset? Before,
    IReadOnlyList<string>? AnyTags,
    int TopK,
    RenderFormat OutputFormat);

public sealed record ScoredMessage(
    RenderedMessage Message,
    float SemanticScore,
    float LexicalScore,
    float CombinedScore,
    string? Snippet);

public sealed record RenderedMessage(
    long MessageId,
    long ChannelId,
    long GuildId,
    long AuthorId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    string Body,
    IReadOnlyList<string> Tags,
    string AuthorName,
    string ChannelName);

/// <summary>
/// Center is null when the requested message was not found in read_messages
/// (matches ConversationCluster.Center nullability).
/// </summary>
public sealed record RenderedConversation(
    RenderedMessage? Center,
    IReadOnlyList<RenderedMessage> Ancestors,
    IReadOnlyList<RenderedMessage> Descendants);

public sealed record ChannelSummary(
    long ChannelId,
    string Name,
    int MessageCountInWindow,
    DateTimeOffset MostRecentActivity);
