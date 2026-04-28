using MassTransit;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Base <see cref="ConsumerDefinition{TConsumer}"/> for all read-side batch consumers.
/// Configures BatchOptions uniformly — 100 messages per batch, 5 s time limit starting
/// from first arrival, ConcurrencyLimit=1 per endpoint.
///
/// Subclasses inherit this and supply the two type parameters; no body required:
/// <code>
/// internal sealed class MessageReadConsumerDefinition
///     : ReadModelBatchConsumerDefinition&lt;MessageReadConsumer, MessageStateChanged&gt;;
/// </code>
/// </summary>
public abstract class ReadModelBatchConsumerDefinition<TConsumer, TEvent>
    : ConsumerDefinition<TConsumer>
    where TConsumer : class, IConsumer<Batch<TEvent>>
    where TEvent : class
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<TConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // PrefetchCount defaults to MessageLimit × ConcurrencyLimit (100 × 1 = 100),
        // handled automatically by BatchOptions.DefaultConfigurationCallback.
        consumerConfigurator.Options<BatchOptions>(opts => opts
            .SetMessageLimit(100)
            .SetTimeLimit(TimeSpan.FromSeconds(5))
            .SetTimeLimitStart(BatchTimeLimitStart.FromFirst)
            .SetConcurrencyLimit(1));
    }
}
