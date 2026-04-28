using DiscordScraper.Contracts.Requests;
using DiscordScraper.Write.Parsing;
using DiscordScraper.Write.Repositories;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Write.Consumers;

/// <summary>
/// Parses a raw Discord message payload into a typed IR, resolves channel and role name fallbacks
/// via concurrent Mongo lookups, and returns the completed IR to the requesting saga.
/// </summary>
/// <remarks>
/// Role name resolution is a stub — GuildSagaState does not carry role metadata yet, so
/// <see cref="MongoGuildRoleNameRepo"/> always returns an empty map and role mentions render as
/// "&lt;unknown role&gt;".
/// </remarks>
internal sealed class ProjectMessageConsumer(
    IMessageParser parser,
    IChannelNameRepo channels,
    IGuildRoleNameRepo guildRoles,
    ILogger<ProjectMessageConsumer> logger) : IConsumer<ProjectMessageRequest>
{
    public async Task Consume(ConsumeContext<ProjectMessageRequest> context)
    {
        var req = context.Message;
        var ct = context.CancellationToken;

        // ProjectMessageRequest doesn't carry the original capture time, so use wall-clock UtcNow.
        // For edit-triggered re-projections that's the correct semantic anyway — the IR timestamp
        // tracks when the IR was built, not when Discord first served the message.
        var parseCtx = new ParseContext(
            CapturedAt: DateTimeOffset.UtcNow,
            HomeChannelId: req.ChannelId,
            HomeChannelName: null);

        var preliminaryIr = parser.Parse(req.PayloadJson, parseCtx);
        var (channelIds, roleIds) = IrFallbackWalker.CollectMissingFallbacks(preliminaryIr);

        var (channelNames, roleNames) = await TaskExt.WhenAll(
            channels.GetNamesAsync(channelIds, ct),
            guildRoles.GetRoleNamesAsync(req.GuildId, roleIds, ct));

        var resolvedIr = IrFallbackWalker.PopulateFallbacks(preliminaryIr, channelNames, roleNames);

        logger.LogDebug(
            "ProjectMessage: msg={MessageSnowflake} channel={ChannelId} channelRefs={ChannelRefCount} roleRefs={RoleRefCount}",
            req.MessageSnowflake, req.ChannelId, channelIds.Count, roleIds.Count);

        await context.RespondAsync(new ProjectMessageResponse(resolvedIr));
    }
}
