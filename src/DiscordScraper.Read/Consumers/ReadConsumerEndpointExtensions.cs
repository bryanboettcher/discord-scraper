using DiscordScraper.Read.Configuration;
using MassTransit;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Shared endpoint configuration for read-side batch consumers. Composition replacement for
/// the previous abstract <c>ReadModelBatchConsumerDefinition</c> base — concrete definitions
/// call this helper from their <c>ConfigureConsumer</c> rather than inheriting from an
/// abstract generic base, avoiding any abstract <see cref="IConsumerDefinition{T}"/> in the
/// scanned assembly.
/// </summary>
public static class ReadConsumerEndpointExtensions
{
    public static void ConfigureReadBatchConsumer<TConsumer>(
        this IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<TConsumer> consumerConfigurator,
        IRegistrationContext context,
        IReadBatchOptions options)
        where TConsumer : class, IConsumer
    {
        // In-memory inbox avoids a Postgres round-trip on rapid same-message redelivery within
        // a single process lifetime. Outbox half is harmless because these consumers publish nothing.
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
