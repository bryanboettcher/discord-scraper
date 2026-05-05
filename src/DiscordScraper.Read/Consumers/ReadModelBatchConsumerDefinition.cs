using DiscordScraper.Read.Configuration;
using MassTransit;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Base <see cref="ConsumerDefinition{TConsumer}"/> for all read-side batch consumers.
/// Applies <c>UseInMemoryInboxOutbox</c> on the endpoint — read-side consumers are idempotent
/// via BulkInsertOrUpdate, but in-memory inbox avoids a Postgres round-trip on rapid same-message
/// redelivery within a single process lifetime. The outbox half is harmless because these consumers
/// publish nothing downstream.
///
/// Subclasses pass their bound <see cref="IReadBatchOptions"/> implementation through and supply
/// the two type parameters; no <c>ConfigureConsumer</c> override is required:
/// <code>
/// internal sealed class ChannelReadConsumerDefinition(IOptions&lt;ChannelReadBatchOptions&gt; opts)
///     : ReadModelBatchConsumerDefinition&lt;ChannelReadConsumer, ChannelChanged&gt;(opts.Value);
/// </code>
/// </summary>
public abstract class ReadModelBatchConsumerDefinition<TConsumer, TEvent>(IReadBatchOptions options)
    : ConsumerDefinition<TConsumer>
    where TConsumer : class, IConsumer<Batch<TEvent>>
    where TEvent : class
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<TConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseInMemoryInboxOutbox(context);

        // PrefetchCount defaults to MessageLimit × ConcurrencyLimit, handled automatically
        // by BatchOptions.DefaultConfigurationCallback.
        consumerConfigurator.Options<BatchOptions>(opts => opts
            .SetMessageLimit(options.BatchMessageLimit)
            .SetTimeLimit(options.BatchTimeout)
            .SetTimeLimitStart(BatchTimeLimitStart.FromFirst)
            .SetConcurrencyLimit(1));
    }
}
