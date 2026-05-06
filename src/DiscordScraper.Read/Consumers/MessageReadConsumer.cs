using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Read.Data.Entities;
using MassTransit;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Projects <see cref="MessageEnriched"/> events into <see cref="ReadMessage"/> rows.
/// One of five per-table consumers that together fully project a <see cref="MessageEnriched"/>
/// event onto the message read tables.
/// </summary>
public sealed class ReadMessageProjectionConsumer(
    BatchProjectionPipeline<MessageEnriched, ReadMessage> pipeline)
    : IConsumer<Batch<MessageEnriched>>
{
    public Task Consume(ConsumeContext<Batch<MessageEnriched>> context)
        => pipeline.Project(context);
}

/// <summary>Projects <see cref="MessageEnriched"/> events into <see cref="MessageReference"/> rows.</summary>
public sealed class MessageReferenceProjectionConsumer(
    BatchProjectionPipeline<MessageEnriched, MessageReference> pipeline)
    : IConsumer<Batch<MessageEnriched>>
{
    public Task Consume(ConsumeContext<Batch<MessageEnriched>> context)
        => pipeline.Project(context);
}

/// <summary>Projects <see cref="MessageEnriched"/> events into <see cref="MessageAttachment"/> rows.</summary>
public sealed class MessageAttachmentProjectionConsumer(
    BatchProjectionPipeline<MessageEnriched, MessageAttachment> pipeline)
    : IConsumer<Batch<MessageEnriched>>
{
    public Task Consume(ConsumeContext<Batch<MessageEnriched>> context)
        => pipeline.Project(context);
}

/// <summary>Projects <see cref="MessageEnriched"/> events into <see cref="MessageEmbed"/> rows.</summary>
public sealed class MessageEmbedProjectionConsumer(
    BatchProjectionPipeline<MessageEnriched, MessageEmbed> pipeline)
    : IConsumer<Batch<MessageEnriched>>
{
    public Task Consume(ConsumeContext<Batch<MessageEnriched>> context)
        => pipeline.Project(context);
}

/// <summary>Projects <see cref="MessageEnriched"/> events into <see cref="MessageTag"/> rows.</summary>
public sealed class MessageTagProjectionConsumer(
    BatchProjectionPipeline<MessageEnriched, MessageTag> pipeline)
    : IConsumer<Batch<MessageEnriched>>
{
    public Task Consume(ConsumeContext<Batch<MessageEnriched>> context)
        => pipeline.Project(context);
}
