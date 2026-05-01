using DiscordScraper.Write.Sagas;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace DiscordScraper.Write.Configuration;

/// <summary>
/// Registers Write-assembly sagas (Guild, Channel, Message) plus all consumers in the assembly.
/// Call inside an <c>AddMassTransit</c> block; <see cref="MongoDB.Driver.IMongoClient"/> and
/// <see cref="MongoDB.Driver.IMongoDatabase"/> must be registered with DI before AddMassTransit
/// is invoked — the saga repositories pull them from the host container via factories.
/// </summary>
/// <remarks>
/// We deliberately avoid <c>r.Connection = ...</c> on the MongoDbRepository configurator. That
/// setter calls <c>new MongoClient(connectionString)</c> internally and registers the result via
/// <c>TryAddSingleton&lt;IMongoClient&gt;</c>. Today that's a no-op because Program.cs registers
/// IMongoClient first, but reordering would silently spawn a phantom client with its own
/// connection pool. Explicit factories make the dependency on the host singleton load-bearing.
/// </remarks>
public static class SagaRegistrationExtensions
{
    public static IBusRegistrationConfigurator AddWriteSagasAndConsumers(
        this IBusRegistrationConfigurator cfg)
    {
        cfg.AddSagaStateMachine<GuildSagaStateMachine, GuildSagaState, GuildSagaDefinition>()
            .MongoDbRepository(r =>
            {
                r.ClientFactory(sp => sp.GetRequiredService<IMongoClient>());
                r.DatabaseFactory(sp => sp.GetRequiredService<IMongoDatabase>());
                r.CollectionName = "guild_sagas";
            });

        cfg.AddSagaStateMachine<ChannelSagaStateMachine, ChannelSagaState, ChannelSagaDefinition>()
            .MongoDbRepository(r =>
            {
                r.ClientFactory(sp => sp.GetRequiredService<IMongoClient>());
                r.DatabaseFactory(sp => sp.GetRequiredService<IMongoDatabase>());
                r.CollectionName = "channel_sagas";
            });

        cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState, MessageSagaDefinition>()
            .MongoDbRepository(r =>
            {
                r.ClientFactory(sp => sp.GetRequiredService<IMongoClient>());
                r.DatabaseFactory(sp => sp.GetRequiredService<IMongoDatabase>());
                r.CollectionName = "message_sagas";
            });

        // Consumer definitions co-located in this assembly are auto-discovered.
        cfg.AddConsumers(typeof(WriteAssemblyMarker).Assembly);

        return cfg;
    }
}
