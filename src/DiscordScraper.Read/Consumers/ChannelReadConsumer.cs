using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Read.Data.Entities;
using MassTransit;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Projects <see cref="ChannelChanged"/> events into <see cref="ReadChannel"/> rows,
/// one row per event. Delegates to <see cref="BatchProjectionPipeline{TEvent,TEntity}"/>.
/// </summary>
public sealed class ChannelReadConsumer(
    BatchProjectionPipeline<ChannelChanged, ReadChannel> pipeline)
    : IConsumer<Batch<ChannelChanged>>
{
    public Task Consume(ConsumeContext<Batch<ChannelChanged>> context)
        => pipeline.Project(context);
}
