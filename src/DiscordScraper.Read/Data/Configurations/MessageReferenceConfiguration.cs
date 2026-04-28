using DiscordScraper.Read.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiscordScraper.Read.Data.Configurations;

internal sealed class MessageReferenceConfiguration : IEntityTypeConfiguration<MessageReference>
{
    public void Configure(EntityTypeBuilder<MessageReference> b)
    {
        b.ToTable("message_references");
        b.HasKey(e => new { e.MessageId, e.Ordinal });
        b.Property(e => e.MessageId).HasColumnName("message_id");
        b.Property(e => e.Kind).HasColumnName("kind");
        b.Property(e => e.TargetId).HasColumnName("target_id");
        b.Property(e => e.Ordinal).HasColumnName("ordinal");

        b.HasIndex(e => new { e.Kind, e.TargetId })
            .HasDatabaseName("message_references_target_idx");
    }
}
