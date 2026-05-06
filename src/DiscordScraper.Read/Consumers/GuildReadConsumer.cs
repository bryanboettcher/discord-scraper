using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Read.Data.Entities;
using MassTransit;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Projects <see cref="GuildChanged"/> events into <see cref="ReadGuild"/> rows,
/// one row per event. Delegates to <see cref="BatchProjectionPipeline{TEvent,TEntity}"/>.
/// </summary>
public sealed class GuildReadConsumer(
    BatchProjectionPipeline<GuildChanged, ReadGuild> pipeline)
    : IConsumer<Batch<GuildChanged>>
{
    public Task Consume(ConsumeContext<Batch<GuildChanged>> context)
        => pipeline.Project(context);
}
