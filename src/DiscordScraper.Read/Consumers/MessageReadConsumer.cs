using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Read.Data.Entities;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Projects <see cref="MessageEnriched"/> events into <see cref="ReadMessage"/> rows.
/// One of five per-table consumers that together fully project a <see cref="MessageEnriched"/>
/// event onto the message read tables.
/// </summary>
public sealed class ReadMessageProjectionConsumer(
    IBatchProjector<MessageEnriched, ReadMessage> projector,
    IBulkWriter<ReadMessage> writer,
    ILogger<ReadMessageProjectionConsumer> logger)
    : ReadModelBatchConsumer<MessageEnriched, ReadMessage>(projector, writer, logger);

/// <summary>Projects <see cref="MessageEnriched"/> events into <see cref="MessageReference"/> rows.</summary>
public sealed class MessageReferenceProjectionConsumer(
    IBatchProjector<MessageEnriched, MessageReference> projector,
    IBulkWriter<MessageReference> writer,
    ILogger<MessageReferenceProjectionConsumer> logger)
    : ReadModelBatchConsumer<MessageEnriched, MessageReference>(projector, writer, logger);

/// <summary>Projects <see cref="MessageEnriched"/> events into <see cref="MessageAttachment"/> rows.</summary>
public sealed class MessageAttachmentProjectionConsumer(
    IBatchProjector<MessageEnriched, MessageAttachment> projector,
    IBulkWriter<MessageAttachment> writer,
    ILogger<MessageAttachmentProjectionConsumer> logger)
    : ReadModelBatchConsumer<MessageEnriched, MessageAttachment>(projector, writer, logger);

/// <summary>Projects <see cref="MessageEnriched"/> events into <see cref="MessageEmbed"/> rows.</summary>
public sealed class MessageEmbedProjectionConsumer(
    IBatchProjector<MessageEnriched, MessageEmbed> projector,
    IBulkWriter<MessageEmbed> writer,
    ILogger<MessageEmbedProjectionConsumer> logger)
    : ReadModelBatchConsumer<MessageEnriched, MessageEmbed>(projector, writer, logger);

/// <summary>Projects <see cref="MessageEnriched"/> events into <see cref="MessageTag"/> rows.</summary>
public sealed class MessageTagProjectionConsumer(
    IBatchProjector<MessageEnriched, MessageTag> projector,
    IBulkWriter<MessageTag> writer,
    ILogger<MessageTagProjectionConsumer> logger)
    : ReadModelBatchConsumer<MessageEnriched, MessageTag>(projector, writer, logger);
