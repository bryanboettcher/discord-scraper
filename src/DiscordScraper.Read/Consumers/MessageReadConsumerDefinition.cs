using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Read.Configuration;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Read.Consumers;

// All five MessageEnriched-driven projection consumers share the same MessageReadBatchOptions
// because they're driven by the same upstream event volume. Each definition is otherwise empty —
// the open-generic base supplies ConfigureConsumer.

public sealed class ReadMessageProjectionConsumerDefinition(IOptions<MessageReadBatchOptions> options)
    : ReadModelBatchConsumerDefinition<ReadMessageProjectionConsumer, MessageEnriched>(options.Value);

public sealed class MessageReferenceProjectionConsumerDefinition(IOptions<MessageReadBatchOptions> options)
    : ReadModelBatchConsumerDefinition<MessageReferenceProjectionConsumer, MessageEnriched>(options.Value);

public sealed class MessageAttachmentProjectionConsumerDefinition(IOptions<MessageReadBatchOptions> options)
    : ReadModelBatchConsumerDefinition<MessageAttachmentProjectionConsumer, MessageEnriched>(options.Value);

public sealed class MessageEmbedProjectionConsumerDefinition(IOptions<MessageReadBatchOptions> options)
    : ReadModelBatchConsumerDefinition<MessageEmbedProjectionConsumer, MessageEnriched>(options.Value);

public sealed class MessageTagProjectionConsumerDefinition(IOptions<MessageReadBatchOptions> options)
    : ReadModelBatchConsumerDefinition<MessageTagProjectionConsumer, MessageEnriched>(options.Value);
