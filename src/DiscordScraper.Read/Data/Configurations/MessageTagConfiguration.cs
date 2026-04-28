using DiscordScraper.Read.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiscordScraper.Read.Data.Configurations;

internal sealed class MessageTagConfiguration : IEntityTypeConfiguration<MessageTag>
{
    public void Configure(EntityTypeBuilder<MessageTag> b)
    {
        b.ToTable("message_tags");
        b.HasKey(e => new { e.MessageId, e.Tag });
        b.Property(e => e.MessageId).HasColumnName("message_id");
        b.Property(e => e.Tag).HasColumnName("tag");

        b.HasIndex(e => e.Tag)
            .HasDatabaseName("message_tags_tag_idx");
    }
}
