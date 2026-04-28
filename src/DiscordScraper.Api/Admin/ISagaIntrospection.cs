namespace DiscordScraper.Api.Admin;

public interface ISagaIntrospection
{
    Task<SagaCountsByState> GetCountsAsync(CancellationToken ct);
    Task<IReadOnlyList<GuildSagaSnapshot>> ListGuildSagasAsync(CancellationToken ct);
    Task<IReadOnlyList<ChannelSagaSnapshot>> ListChannelSagasAsync(long? guildId, CancellationToken ct);
    Task<MessageSagaSnapshot?> GetMessageSagaAsync(long messageSnowflake, CancellationToken ct);
}
