using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Read.Data.Entities;
using Riok.Mapperly.Abstractions;

namespace DiscordScraper.Read.Mapping;

/// <summary>
/// Maps <see cref="GuildChanged"/> → <see cref="ReadGuild"/>.
///
/// Mapping notes:
///   - UpdatedOn → UpdatedAt: name mismatch resolved via [MapProperty].
///   - CurrentState, CorrelationId: event-routing fields, not stored; explicitly ignored.
///   - GuildId and Name map by name automatically.
/// </summary>
[Mapper]
public static partial class GuildReadModelMapper
{
    [MapProperty(nameof(GuildChanged.UpdatedOn), nameof(ReadGuild.UpdatedAt))]
    [MapperIgnoreSource(nameof(GuildChanged.CurrentState))]
    [MapperIgnoreSource(nameof(GuildChanged.CorrelationId))]
    [MapperIgnoreSource(nameof(GuildChanged.Roles))]
    [MapperIgnoreSource(nameof(GuildChanged.IsPresent))]
    [MapperIgnoreSource(nameof(GuildChanged.SyncedAt))]
    public static partial ReadGuild ToReadGuild(GuildChanged evt);
}
