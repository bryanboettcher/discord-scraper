using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Read.Consumers;
using DiscordScraper.Read.Data.Entities;

namespace DiscordScraper.Read.Mapping;

/// <summary>Projects <see cref="MessageEnriched"/> into <see cref="MessageEmbed"/> rows.</summary>
internal sealed class MapperlyMessageEmbedProjector : IBatchProjector<MessageEnriched, MessageEmbed>
{
    public IEnumerable<MessageEmbed> Project(MessageEnriched evt) =>
        MessageRefExtractor.ExtractEmbeds(evt.MessageSnowflake, evt.IR);
}
