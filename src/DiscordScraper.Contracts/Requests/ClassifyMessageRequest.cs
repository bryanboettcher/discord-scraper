namespace DiscordScraper.Contracts.Requests;

public sealed record ClassifyMessageRequest : IMeasured
{
    public long MessageSnowflake { get; init; }
    public long GuildId { get; init; }
    public long ChannelId { get; init; }
    public long AuthorId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string PlainText { get; init; } = string.Empty;
    /// <summary>Embedding vector computed by the preceding Tag phase. Forwarded to the vector store.</summary>
    public IReadOnlyList<float> Embedding { get; init; } = [];

    /// <inheritdoc cref="IStampable.Timestamp"/>
    public DateTimeOffset Timestamp { get; set; }

    /// <inheritdoc cref="IMeasured.ReceivedOn"/>
    public DateTimeOffset ReceivedOn { get; set; }
}
