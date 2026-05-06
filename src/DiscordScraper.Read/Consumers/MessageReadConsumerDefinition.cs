using DiscordScraper.Read.Configuration;
using MassTransit;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Read.Consumers;

// All five MessageEnriched-driven projection consumers share the same MessageReadBatchOptions
// because they're driven by the same upstream event volume. Each definition delegates to the
// shared ConfigureReadBatchConsumer extension.

public sealed class ReadMessageProjectionConsumerDefinition(IOptions<MessageReadBatchOptions> options)
    : ConsumerDefinition<ReadMessageProjectionConsumer>
{
    private readonly MessageReadBatchOptions _opts = options.Value;

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ReadMessageProjectionConsumer> consumerConfigurator,
        IRegistrationContext context)
        => endpointConfigurator.ConfigureReadBatchConsumer(consumerConfigurator, context, _opts);
}

public sealed class MessageReferenceProjectionConsumerDefinition(IOptions<MessageReadBatchOptions> options)
    : ConsumerDefinition<MessageReferenceProjectionConsumer>
{
    private readonly MessageReadBatchOptions _opts = options.Value;

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<MessageReferenceProjectionConsumer> consumerConfigurator,
        IRegistrationContext context)
        => endpointConfigurator.ConfigureReadBatchConsumer(consumerConfigurator, context, _opts);
}

public sealed class MessageAttachmentProjectionConsumerDefinition(IOptions<MessageReadBatchOptions> options)
    : ConsumerDefinition<MessageAttachmentProjectionConsumer>
{
    private readonly MessageReadBatchOptions _opts = options.Value;

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<MessageAttachmentProjectionConsumer> consumerConfigurator,
        IRegistrationContext context)
        => endpointConfigurator.ConfigureReadBatchConsumer(consumerConfigurator, context, _opts);
}

public sealed class MessageEmbedProjectionConsumerDefinition(IOptions<MessageReadBatchOptions> options)
    : ConsumerDefinition<MessageEmbedProjectionConsumer>
{
    private readonly MessageReadBatchOptions _opts = options.Value;

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<MessageEmbedProjectionConsumer> consumerConfigurator,
        IRegistrationContext context)
        => endpointConfigurator.ConfigureReadBatchConsumer(consumerConfigurator, context, _opts);
}

public sealed class MessageTagProjectionConsumerDefinition(IOptions<MessageReadBatchOptions> options)
    : ConsumerDefinition<MessageTagProjectionConsumer>
{
    private readonly MessageReadBatchOptions _opts = options.Value;

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<MessageTagProjectionConsumer> consumerConfigurator,
        IRegistrationContext context)
        => endpointConfigurator.ConfigureReadBatchConsumer(consumerConfigurator, context, _opts);
}
