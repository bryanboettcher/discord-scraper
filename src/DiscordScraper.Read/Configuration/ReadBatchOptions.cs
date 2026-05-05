namespace DiscordScraper.Read.Configuration;

/// <summary>
/// Common shape for read-side batch consumer settings — exposes a timeout and a max-message
/// count. Implemented by per-event options classes so the base
/// <c>ReadModelBatchConsumerDefinition</c> can apply settings without knowing the concrete type.
/// </summary>
public interface IReadBatchOptions
{
    TimeSpan BatchTimeout { get; }
    int BatchMessageLimit { get; }
}

/// <summary>
/// Batch consumer settings for the high-volume message read projection.
/// Binds to <c>ReadBatch:Messages</c>.
/// </summary>
public sealed class MessageReadBatchOptions : IReadBatchOptions
{
    public const string SectionName = "ReadBatch:Messages";

    public TimeSpan BatchTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int BatchMessageLimit { get; init; } = 2000;
}

/// <summary>
/// Batch consumer settings for the channel read projection.
/// Binds to <c>ReadBatch:Channels</c>.
/// </summary>
public sealed class ChannelReadBatchOptions : IReadBatchOptions
{
    public const string SectionName = "ReadBatch:Channels";

    public TimeSpan BatchTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public int BatchMessageLimit { get; init; } = 50;
}

/// <summary>
/// Batch consumer settings for the guild read projection.
/// Binds to <c>ReadBatch:Guilds</c>.
/// </summary>
public sealed class GuildReadBatchOptions : IReadBatchOptions
{
    public const string SectionName = "ReadBatch:Guilds";

    public TimeSpan BatchTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public int BatchMessageLimit { get; init; } = 10;
}
