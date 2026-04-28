// ReSharper disable InconsistentNaming
using MassTransit;

namespace DiscordScraper.Contracts.Events.Message;

/// <summary>
/// Rollup subscription point for read-side consumers that project any state transition.
/// A Batch&lt;MessageStateChanged&gt; consumer receives Projected, Analyzed, Enhanced, Indexed,
/// and Enriched without explicit fan-in.
/// </summary>
[ExcludeFromTopology]
[ExcludeFromImplementedTypes]
public interface MessageStateChanged : BaseMessageEvent;
