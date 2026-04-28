using DiscordScraper.Contracts.IR;

namespace DiscordScraper.Write.Parsing;

/// <summary>
/// Parses a raw Discord message payload (JSON) into a typed IR.
///
/// Captures structure with typed references; renders no text. Fallback strings
/// for user mentions are pulled from the payload's mentions[] array; channel and role
/// refs leave Fallback null for ProjectMessageConsumer to populate via repo lookups.
/// </summary>
public interface IMessageParser
{
    /// <summary>
    /// Parses a raw Discord message payload (JSON) into a typed IR.
    /// </summary>
    /// <param name="payloadJson">Verbatim Discord message JSON (raw_messages.payload).</param>
    /// <param name="context">
    /// Capture-time context. <see cref="ParseContext.HomeChannelName"/> is an optimization:
    /// ChannelSyncConsumer stamps the home channel name onto MessageCaptured so the parser
    /// can populate the Fallback on ChannelRefNodes that reference the message's own channel
    /// without a repo lookup. Cross-channel refs still produce null Fallback; ProjectMessageConsumer
    /// resolves those via <c>_channelRepo.GetNamesAsync</c>.
    /// </param>
    MessageIR Parse(string payloadJson, ParseContext context);
}

/// <summary>
/// Context stamped by ChannelSyncConsumer when publishing MessageCaptured.
/// </summary>
/// <param name="CapturedAt">Wall-clock time of ingestion; stored on the IR.</param>
/// <param name="HomeChannelId">
/// Snowflake of the channel this message was fetched from. Used together with
/// <see cref="HomeChannelName"/> to populate ChannelRefNode.Fallback for self-references.
/// </param>
/// <param name="HomeChannelName">
/// Display name of the home channel. Null when the caller does not have it loaded
/// (e.g., direct test invocations). When null, ChannelRefNode.Fallback is always null
/// and ProjectMessageConsumer must look up all channel names.
/// </param>
public sealed record ParseContext(
    DateTimeOffset CapturedAt,
    long? HomeChannelId = null,
    string? HomeChannelName = null);
