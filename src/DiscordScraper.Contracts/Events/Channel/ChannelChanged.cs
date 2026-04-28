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
}
