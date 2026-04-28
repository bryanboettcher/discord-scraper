using DiscordScraper.Read.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiscordScraper.Read.Data;

/// <summary>
/// EF DbContext for the read model. Pooled lifetime; schema is bootstrapped by
/// <see cref="ReadSchemaInitializer"/> using idempotent CREATE TABLE IF NOT EXISTS — no EF
/// migrations (deferred by <c>.planning/architecture-plan.md</c>). <c>message_vectors</c> is
/// intentionally absent: <c>PgVectorStore</c> owns that table via raw Npgsql.
/// </summary>
public sealed class ReadDbContext(DbContextOptions<ReadDbContext> options) : DbContext(options)
{
    public DbSet<ReadMessage> ReadMessages => Set<ReadMessage>();
    public DbSet<MessageReference> MessageReferences => Set<MessageReference>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<MessageEmbed> MessageEmbeds => Set<MessageEmbed>();
    public DbSet<MessageTag> MessageTags => Set<MessageTag>();
    public DbSet<ReadChannel> ReadChannels => Set<ReadChannel>();
    public DbSet<ReadGuild> ReadGuilds => Set<ReadGuild>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.ApplyConfigurationsFromAssembly(typeof(ReadDbContext).Assembly);
    }
}
