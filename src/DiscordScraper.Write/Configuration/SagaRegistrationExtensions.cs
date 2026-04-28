using DiscordScraper.Write.Sagas;
using MassTransit;

namespace DiscordScraper.Write.Configuration;

/// <summary>
/// Registers Write-assembly sagas (Guild, Channel, Message) plus all consumers in the assembly.
/// Call inside an <c>AddMassTransit</c> block; <see cref="MongoDB.Driver.IMongoDatabase"/> must be
/// registered with DI before AddMassTransit is invoked.
/// </summary>
public static class SagaRegistrationExtensions
{
    public static IBusRegistrationConfigurator AddWriteSagasAndConsumers(
        this IBusRegistrationConfigurator cfg,
        string mongoConnectionString,
        string mongoDatabaseName)
    {
        cfg.AddSagaStateMachine<GuildSagaStateMachine, GuildSagaState>()
            .MongoDbRepository(r =>
            {
                r.Connection = mongoConnectionString;
                r.DatabaseName = mongoDatabaseName;
                r.CollectionName = "guild_sagas";
            });

        cfg.AddSagaStateMachine<ChannelSagaStateMachine, ChannelSagaState>()
            .MongoDbRepository(r =>
            {
                r.Connection = mongoConnectionString;
                r.DatabaseName = mongoDatabaseName;
                r.CollectionName = "channel_sagas";
            });

        cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState, MessageSagaDefinition>()
            .MongoDbRepository(r =>
            {
                r.Connection = mongoConnectionString;
                r.DatabaseName = mongoDatabaseName;
                r.CollectionName = "message_sagas";
            });

        // Consumer definitions co-located in this assembly are auto-discovered.
        cfg.AddConsumers(typeof(WriteAssemblyMarker).Assembly);

        return cfg;
    }
}
