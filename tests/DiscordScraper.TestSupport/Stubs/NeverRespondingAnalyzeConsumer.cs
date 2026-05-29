using DiscordScraper.Contracts.Requests;
using MassTransit;

namespace DiscordScraper.TestSupport.Stubs;

/// <summary>
/// Stub consumer for <see cref="AnalyzeMessageRequest"/> that receives the request but
/// never sends a response. The saga sits in <c>AnalyzeMessage.Pending</c> until the
/// configured request timeout fires, producing <c>RequestTimeoutExpired&lt;AnalyzeMessageRequest&gt;</c>.
///
/// Used by Issue #11 reproduction: a burst of <see cref="MessageCaptured"/> events all
/// time out on the Analyze phase, producing concurrent timeout-expired events that expose
/// the E11000 duplicate-key race in the saga repository.
/// </summary>
public sealed class NeverRespondingAnalyzeConsumer : IConsumer<AnalyzeMessageRequest>
{
    public Task Consume(ConsumeContext<AnalyzeMessageRequest> context) => Task.CompletedTask;
}

/// <summary>
/// Consumer definition for <see cref="NeverRespondingAnalyzeConsumer"/>.
/// Minimal config — no retry (retrying a never-responding consumer wastes time), no KillSwitch.
/// </summary>
public sealed class NeverRespondingAnalyzeConsumerDefinition : ConsumerDefinition<NeverRespondingAnalyzeConsumer>
{
    public NeverRespondingAnalyzeConsumerDefinition()
    {
        // No concurrent limit — we want this consumer to ACK requests immediately
        // and leave the saga waiting on a response that never arrives.
    }
}
