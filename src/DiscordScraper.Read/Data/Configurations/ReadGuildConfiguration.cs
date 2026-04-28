using DiscordScraper.Read.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiscordScraper.Read.Data.Configurations;

internal sealed class ReadGuildConfiguration : IEntityTypeConfiguration<ReadGuild>
{
    public void Configure(EntityTypeBuilder<ReadGuild> b)
    {
        b.ToTable("read_guilds");
        b.HasKey(e => e.GuildId);
        b.Property(e => e.GuildId).HasColumnName("guild_id").ValueGeneratedNever();
        b.Property(e => e.Name).HasColumnName("name");
        b.Property(e => e.UpdatedAt).HasColumnName("updated_at");
    }
}
