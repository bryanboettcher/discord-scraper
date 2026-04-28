using DiscordScraper.Read.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiscordScraper.Read.Data.Configurations;

internal sealed class MessageEmbedConfiguration : IEntityTypeConfiguration<MessageEmbed>
{
    public void Configure(EntityTypeBuilder<MessageEmbed> b)
    {
        b.ToTable("message_embeds");
        b.HasKey(e => new { e.MessageId, e.EmbedIndex });
        b.Property(e => e.MessageId).HasColumnName("message_id");
        b.Property(e => e.EmbedIndex).HasColumnName("embed_index");
        b.Property(e => e.EmbedType).HasColumnName("embed_type");
        b.Property(e => e.Url).HasColumnName("url");
        b.Property(e => e.Title).HasColumnName("title");
        b.Property(e => e.Description).HasColumnName("description");
    }
}
