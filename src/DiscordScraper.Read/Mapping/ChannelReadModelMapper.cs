using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Read.Data.Entities;
using Riok.Mapperly.Abstractions;

namespace DiscordScraper.Read.Mapping;

/// <summary>
/// Maps <see cref="ChannelChanged"/> → <see cref="ReadChannel"/>.
///
/// Mapping notes:
///   - LastUpdatedAt → UpdatedAt: name mismatch resolved via [MapProperty].
///   - ChannelType → Type: name mismatch resolved via [MapProperty].
///   - CurrentState, CorrelationId: event-routing fields, not stored; explicitly ignored.
/// </summary>
[Mapper]
public static partial class ChannelReadModelMapper
{
    [MapProperty(nameof(ChannelChanged.LastUpdatedAt), nameof(ReadChannel.UpdatedAt))]
    [MapProperty(nameof(ChannelChanged.ChannelType), nameof(ReadChannel.Type))]
    [MapperIgnoreSource(nameof(ChannelChanged.CurrentState))]
    [MapperIgnoreSource(nameof(ChannelChanged.CorrelationId))]
    public static partial ReadChannel ToReadChannel(ChannelChanged evt);
}
