using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.IR;
using DiscordScraper.Contracts.Requests;
using MassTransit;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Projects saga state into outbound request / event contracts.
/// Kept separate from the state machine so the mapping logic is testable in isolation
/// and the state machine body stays focused on control flow.
/// Direction is always Write → Contracts; no read-side references.
/// </summary>
internal static class MessageSagaStateExtensions
{
    public static AnalyzeMessageRequest ToAnalyzeRequest(this MessageSagaState s) =>
        new()
        {
            MessageSnowflake = s.MessageSnowflake,
            PayloadJson = s.PayloadJson,
            AuthorIsBot = s.AuthorIsBot,
        };

    public static ProjectMessageRequest ToProjectRequest(this MessageSagaState s) =>
        new()
        {
            MessageSnowflake = s.MessageSnowflake,
            ChannelId = s.ChannelId,
            GuildId = s.GuildId,
            PayloadJson = s.PayloadJson,
        };

    public static TagMessageRequested ToTagMessageRequested(this MessageSagaState s) =>
        new()
        {
            MessageSnowflake = s.MessageSnowflake,
            PlainText = IrTextFlattener.Flatten(s.IR!),
        };

    public static ClassifyMessageRequested ToClassifyMessageRequested(this MessageSagaState s) =>
        new()
        {
            MessageSnowflake = s.MessageSnowflake,
            GuildId = s.GuildId,
            ChannelId = s.ChannelId,
            AuthorId = s.AuthorId,
            CreatedAt = s.MessageCreatedAt,
            PlainText = IrTextFlattener.Flatten(s.IR!),
            Embedding = s.Embedding!,
        };

    // PublishAsync on the MT DSL binder expects Func<BehaviorContext<TSaga>, Task<SendTuple<T>>>.
    // ctx.Init<T>() returns exactly that; we delegate to it here so call sites stay one-liners.

    public static Task<SendTuple<MessageAnalyzed>> ToMessageAnalyzed(this BehaviorContext<MessageSagaState> ctx) =>
        ctx.Init<MessageAnalyzed>(new
        {
            ctx.Saga.MessageId,
            ctx.Saga.MessageSnowflake,
            ctx.Saga.ChannelId,
            ctx.Saga.GuildId,
            ctx.Saga.AuthorId,
            ctx.Saga.CurrentState,
            UpdatedOn = ctx.Saga.UpdatedOn,
            IsSubstantive = ctx.Saga.IsSubstantive!.Value,
            IsBot = ctx.Saga.IsBot!.Value,
            ctx.Saga.DetectedLanguage,
        });

    public static Task<SendTuple<MessageProjected>> ToMessageProjected(this BehaviorContext<MessageSagaState> ctx) =>
        ctx.Init<MessageProjected>(new
        {
            ctx.Saga.MessageId,
            ctx.Saga.MessageSnowflake,
            ctx.Saga.ChannelId,
            ctx.Saga.GuildId,
            ctx.Saga.AuthorId,
            ctx.Saga.CurrentState,
            UpdatedOn = ctx.Saga.UpdatedOn,
            ctx.Saga.IR,
        });

    public static Task<SendTuple<MessageEnriched>> ToMessageEnriched(this BehaviorContext<MessageSagaState> ctx) =>
        ctx.Init<MessageEnriched>(new
        {
            ctx.Saga.MessageId,
            ctx.Saga.MessageSnowflake,
            ctx.Saga.ChannelId,
            ctx.Saga.GuildId,
            ctx.Saga.AuthorId,
            ctx.Saga.CurrentState,
            UpdatedOn = ctx.Saga.UpdatedOn,
            IR = ctx.Saga.IR!,
            Tags = ctx.Saga.Tags!,
            IsSubstantive = ctx.Saga.IsSubstantive!.Value,
            IsBot = ctx.Saga.IsBot!.Value,
            ctx.Saga.MessageCreatedAt,
            ctx.Saga.EditedTimestamp,
        });
}
