using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Read.Data.Entities;
using Riok.Mapperly.Abstractions;

namespace DiscordScraper.Read.Mapping;

/// <summary>
/// Maps <see cref="GuildChanged"/> → <see cref="ReadGuild"/>.
///
/// Mapping notes:
///   - LastUpdatedAt → UpdatedAt: name mismatch resolved via [MapProperty].
///   - CurrentState, CorrelationId: event-routing fields, not stored; explicitly ignored.
///   - GuildId and Name map by name automatically.
/// </summary>
[Mapper]
public static partial class GuildReadModelMapper
{
    [MapProperty(nameof(GuildChanged.LastUpdatedAt), nameof(ReadGuild.UpdatedAt))]
    [MapperIgnoreSource(nameof(GuildChanged.CurrentState))]
    [MapperIgnoreSource(nameof(GuildChanged.CorrelationId))]
    [MapperIgnoreSource(nameof(GuildChanged.Roles))]
    public static partial ReadGuild ToReadGuild(GuildChanged evt);
}
