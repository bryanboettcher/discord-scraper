namespace DiscordScraper.Contracts.IR;

public sealed record ReplyContext(
    long? ReplyToMessageId,
    long? ReplyToChannelId,
    long? ReplyToAuthorId);
