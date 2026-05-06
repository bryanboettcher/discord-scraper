using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.Events.Sync;
using MassTransit;
using MassTransit.Middleware;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Partitions the events that carry MessageSnowflake on a single shared partition lock.
/// This eliminates same-saga InsertOnInitial races during MessageCaptured fan-out from
/// channel scrapes — the dominant DuplicateKey source under backfill load.
///
/// Unpartitioned events on this endpoint:
/// - MessageReplayRequested, TagsInvalidated, ClassificationInvalidated — broadcast events
///   that fan out via CorrelateBy predicates to many sagas; no per-saga key. MT's CorrelateBy
///   already serialises dispatch to each matching saga.
/// - The four request-response pairs (Analyze/Tag/Classify/Project) — response payloads
///   carry no MessageSnowflake. Wiki ADR-002 (envelope-record propagation) addresses this.
///
/// Retry middleware retained as a safety net for response-vs-event races and broadcast-vs-event
/// races until ADR-002 lands.
/// </summary>
public sealed class MessageSagaDefinition : SagaDefinition<MessageSagaState>
{
    public MessageSagaDefinition()
    {
        ConcurrentMessageLimit = 16;
    }

    protected override void ConfigureSaga(
        IReceiveEndpointConfigurator endpointConfigurator,
        ISagaConfigurator<MessageSagaState> sagaConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.PrefetchCount = 500;

        var partition = new Partitioner(16, new Murmur3UnsafeHashGenerator());

        endpointConfigurator.UsePartitioner<MessageCaptured>(partition, m => DeterministicGuid.FromSnowflake(m.Message.MessageSnowflake));
        endpointConfigurator.UsePartitioner<MessageEditObserved>(partition, m => DeterministicGuid.FromSnowflake(m.Message.MessageSnowflake));

        endpointConfigurator.UseMessageRetry(r =>
            r.Exponential(3,
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(50)));

        endpointConfigurator.UseMongoDbOutbox(context);
    }
}
