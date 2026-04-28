using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Read.Data.Entities;
using Riok.Mapperly.Abstractions;

namespace DiscordScraper.Read.Mapping;

/// <summary>
/// Maps <see cref="ChannelChanged"/> → <see cref="ReadChannel"/>.
///
/// Mapping notes:
///   - LastUpdatedAt → UpdatedAt: name mismatch resolved via [MapProperty].
///   - CurrentState, CorrelationId: event-routing fields, not stored; explicitly ignored.
///   - Type: absent from ChannelChanged — the contract captures only metadata updates,
///     not the full channel object. Defaults to 0 (unknown) until ChannelSaga stamps it.
///   - ParentId: same gap; nullable target defaults to null automatically.
/// </summary>
[Mapper]
public static partial class ChannelReadModelMapper
{
    [MapProperty(nameof(ChannelChanged.LastUpdatedAt), nameof(ReadChannel.UpdatedAt))]
    [MapperIgnoreSource(nameof(ChannelChanged.CurrentState))]
    [MapperIgnoreSource(nameof(ChannelChanged.CorrelationId))]
    [MapperIgnoreTarget(nameof(ReadChannel.Type))]
    [MapperIgnoreTarget(nameof(ReadChannel.ParentId))]
    public static partial ReadChannel ToReadChannel(ChannelChanged evt);
}
