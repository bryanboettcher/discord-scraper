using DiscordScraper.Read.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiscordScraper.Read.Data.Configurations;

internal sealed class MessageAttachmentConfiguration : IEntityTypeConfiguration<MessageAttachment>
{
    public void Configure(EntityTypeBuilder<MessageAttachment> b)
    {
        b.ToTable("message_attachments");
        b.HasKey(e => new { e.MessageId, e.AttachmentId });
        b.Property(e => e.MessageId).HasColumnName("message_id");
        b.Property(e => e.AttachmentId).HasColumnName("attachment_id");
        b.Property(e => e.Url).HasColumnName("url");
        b.Property(e => e.ContentType).HasColumnName("content_type");
        b.Property(e => e.SizeBytes).HasColumnName("size_bytes");
    }
}
