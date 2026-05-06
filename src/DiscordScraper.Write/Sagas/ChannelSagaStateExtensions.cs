using DiscordScraper.Contracts.Events.Channel;
using MassTransit;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Projects saga state into outbound event contracts.
/// Kept separate from the state machine so the mapping logic is testable in isolation
/// and the state machine body stays focused on control flow.
/// Direction is always Write → Contracts; no read-side references.
/// </summary>
internal static class ChannelSagaStateExtensions
{
    // PublishAsync on the MT DSL binder expects Func<BehaviorContext<TSaga>, Task<SendTuple<T>>>.
    // ctx.Init<T>() returns exactly that; we delegate to it here so call sites stay one-liners.

    public static Task<SendTuple<PinPollDue>> ToPinPollDue(
        this BehaviorContext<ChannelSagaState> ctx,
        TimeSpan delay) =>
        ctx.Init<PinPollDue>(new
        {
            ctx.Saga.ChannelId,
            ctx.Saga.GuildId,
            CurrentState = ctx.Saga.CurrentState,
            DueAt = (ctx.SentTime ?? ctx.Saga.UpdatedOn).Add(delay),
            UpdatedOn = ctx.Saga.UpdatedOn,
        });
}
