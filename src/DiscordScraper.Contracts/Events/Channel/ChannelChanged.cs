// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Channel;

/// <summary>Published when Discord reports a channel metadata update (name, topic, type, etc.).</summary>
public interface ChannelChanged : BaseChannelEvent
{
    string Name { get; }
    string? Topic { get; }

    /// <summary>Discord's numeric channel type (0=text, 5=announcement, 10/11/12=thread variants).</summary>
    int ChannelType { get; }

    /// <summary>For threads: the parent text channel snowflake. Null for top-level channels.</summary>
    long? ParentId { get; }

    /// <summary>
    /// False when the bot received 403 or 404 for this channel, indicating the channel is inaccessible
    /// or deleted. Defaults to true so existing publishers don't need to set it explicitly.
    /// </summary>
    bool IsPresent { get; }
}
