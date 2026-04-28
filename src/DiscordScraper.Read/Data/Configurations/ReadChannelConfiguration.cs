using DiscordScraper.Read.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiscordScraper.Read.Data.Configurations;

internal sealed class ReadChannelConfiguration : IEntityTypeConfiguration<ReadChannel>
{
    public void Configure(EntityTypeBuilder<ReadChannel> b)
    {
        b.ToTable("read_channels");
        b.HasKey(e => e.ChannelId);
        b.Property(e => e.ChannelId).HasColumnName("channel_id").ValueGeneratedNever();
        b.Property(e => e.GuildId).HasColumnName("guild_id");
        b.Property(e => e.Name).HasColumnName("name");
        b.Property(e => e.Type).HasColumnName("type");
        b.Property(e => e.ParentId).HasColumnName("parent_id");
        b.Property(e => e.Topic).HasColumnName("topic");
        b.Property(e => e.UpdatedAt).HasColumnName("updated_at");
    }
}
