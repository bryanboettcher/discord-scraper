using DiscordScraper.Storage.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiscordScraper.Storage;

public sealed class DiscordScraperDbContext(DbContextOptions<DiscordScraperDbContext> options) : DbContext(options)
{
    public DbSet<RawMessageEntity> RawMessages => Set<RawMessageEntity>();
    public DbSet<RawMessageEditEntity> RawMessageEdits => Set<RawMessageEditEntity>();
    public DbSet<RawGuildEntity> RawGuilds => Set<RawGuildEntity>();
    public DbSet<RawChannelEntity> RawChannels => Set<RawChannelEntity>();
    public DbSet<RawSyncStateEntity> RawSyncState => Set<RawSyncStateEntity>();
    public DbSet<IngestionRunEntity> IngestionRuns => Set<IngestionRunEntity>();

    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<MessageEnrichmentEntity> MessageEnrichments => Set<MessageEnrichmentEntity>();

    public DbSet<RawPinEntity> RawPins => Set<RawPinEntity>();

    public DbSet<GuildCurrentView> GuildsCurrent => Set<GuildCurrentView>();
    public DbSet<ChannelCurrentView> ChannelsCurrent => Set<ChannelCurrentView>();
    public DbSet<PinCurrentView> PinsCurrent => Set<PinCurrentView>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ------------------------------------------------------------------
        // Tier 1: raw append-only capture. Schema churn here requires a real
        // migration; the contents are authoritative and never truncated.
        // ------------------------------------------------------------------

        modelBuilder.Entity<RawMessageEntity>(entity =>
        {
            entity.ToTable("raw_messages");
            entity.HasKey(e => e.MessageId);
            entity.Property(e => e.MessageId).HasColumnName("message_id").ValueGeneratedNever();
            entity.Property(e => e.ChannelId).HasColumnName("channel_id");
            entity.Property(e => e.GuildId).HasColumnName("guild_id");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.FetchedAt).HasColumnName("fetched_at");
            entity.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");

            entity.HasIndex(e => new { e.ChannelId, e.CreatedAt })
                  .IsDescending(false, true)
                  .HasDatabaseName("ix_raw_messages_channel_created");

            entity.HasIndex(e => new { e.GuildId, e.CreatedAt })
                  .IsDescending(false, true)
                  .HasDatabaseName("ix_raw_messages_guild_created");
        });

        modelBuilder.Entity<RawMessageEditEntity>(entity =>
        {
            entity.ToTable("raw_message_edits");
            entity.HasKey(e => new { e.MessageId, e.EditedAt });
            entity.Property(e => e.MessageId).HasColumnName("message_id");
            entity.Property(e => e.EditedAt).HasColumnName("edited_at");
            entity.Property(e => e.FetchedAt).HasColumnName("fetched_at");
            entity.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");
        });

        modelBuilder.Entity<RawGuildEntity>(entity =>
        {
            entity.ToTable("raw_guilds");
            entity.HasKey(e => new { e.GuildId, e.FetchedAt });
            entity.Property(e => e.GuildId).HasColumnName("guild_id");
            entity.Property(e => e.FetchedAt).HasColumnName("fetched_at");
            entity.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");
        });

        modelBuilder.Entity<RawChannelEntity>(entity =>
        {
            entity.ToTable("raw_channels");
            entity.HasKey(e => new { e.ChannelId, e.FetchedAt });
            entity.Property(e => e.ChannelId).HasColumnName("channel_id");
            entity.Property(e => e.GuildId).HasColumnName("guild_id");
            entity.Property(e => e.FetchedAt).HasColumnName("fetched_at");
            entity.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");
        });

        modelBuilder.Entity<RawPinEntity>(entity =>
        {
            entity.ToTable("raw_pins");
            entity.HasKey(e => new { e.ChannelId, e.FetchedAt });
            entity.Property(e => e.ChannelId).HasColumnName("channel_id");
            entity.Property(e => e.FetchedAt).HasColumnName("fetched_at");
            entity.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");
        });

        modelBuilder.Entity<RawSyncStateEntity>(entity =>
        {
            entity.ToTable("raw_sync_state");
            entity.HasKey(e => e.ChannelId);
            entity.Property(e => e.ChannelId).HasColumnName("channel_id").ValueGeneratedNever();
            entity.Property(e => e.LastMessageId).HasColumnName("last_message_id");
            entity.Property(e => e.LastSyncedAt).HasColumnName("last_synced_at");
            entity.Property(e => e.LastError).HasColumnName("last_error");
            entity.Property(e => e.ConsecutiveErrors).HasColumnName("consecutive_errors").HasDefaultValue(0);
        });

        modelBuilder.Entity<IngestionRunEntity>(entity =>
        {
            entity.ToTable("ingestion_runs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            entity.Property(e => e.Worker).HasColumnName("worker");
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.ItemsProcessed).HasColumnName("items_processed").HasDefaultValue(0);
            entity.Property(e => e.Error).HasColumnName("error");

            entity.HasIndex(e => new { e.Worker, e.StartedAt })
                  .IsDescending(false, true)
                  .HasDatabaseName("ix_ingestion_runs_worker_started");
        });

        // ------------------------------------------------------------------
        // Tier 2: disposable projections. TRUNCATE + rebuild is always safe.
        // ------------------------------------------------------------------

        modelBuilder.Entity<MessageEntity>(entity =>
        {
            entity.ToTable("messages");
            entity.HasKey(e => e.MessageId);
            entity.Property(e => e.MessageId).HasColumnName("message_id").ValueGeneratedNever();
            entity.Property(e => e.ChannelId).HasColumnName("channel_id");
            entity.Property(e => e.GuildId).HasColumnName("guild_id");
            entity.Property(e => e.AuthorId).HasColumnName("author_id");
            entity.Property(e => e.AuthorName).HasColumnName("author_name");
            entity.Property(e => e.AuthorIsBot).HasColumnName("author_is_bot");
            entity.Property(e => e.Content).HasColumnName("content");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.EditedAt).HasColumnName("edited_at");
            entity.Property(e => e.ReplyToId).HasColumnName("reply_to_id");
            entity.Property(e => e.ThreadId).HasColumnName("thread_id");
            entity.Property(e => e.RootChannelId).HasColumnName("root_channel_id");
            entity.Property(e => e.HasAttachments).HasColumnName("has_attachments");

            entity.Property(e => e.ContentTsv)
                  .HasColumnName("content_tsv")
                  .HasColumnType("tsvector")
                  .HasComputedColumnSql("to_tsvector('english', content)", stored: true);

            entity.HasOne<RawMessageEntity>()
                  .WithOne()
                  .HasForeignKey<MessageEntity>(e => e.MessageId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.ChannelId, e.CreatedAt })
                  .IsDescending(false, true)
                  .HasDatabaseName("ix_messages_channel_created");

            entity.HasIndex(e => new { e.GuildId, e.CreatedAt })
                  .IsDescending(false, true)
                  .HasDatabaseName("ix_messages_guild_created");

            entity.HasIndex(e => e.ContentTsv)
                  .HasMethod("gin")
                  .HasDatabaseName("ix_messages_content_tsv");

            entity.HasIndex(e => e.ReplyToId)
                  .HasFilter("reply_to_id IS NOT NULL")
                  .HasDatabaseName("ix_messages_reply_to_id");

            entity.HasIndex(e => e.ThreadId)
                  .HasFilter("thread_id IS NOT NULL")
                  .HasDatabaseName("ix_messages_thread_id");
        });

        modelBuilder.Entity<MessageEnrichmentEntity>(entity =>
        {
            entity.ToTable("message_enrichments");
            entity.HasKey(e => e.MessageId);
            entity.Property(e => e.MessageId).HasColumnName("message_id").ValueGeneratedNever();
            entity.Property(e => e.EmbeddingModel).HasColumnName("embedding_model");
            entity.Property(e => e.QdrantPointId).HasColumnName("qdrant_point_id");
            entity.Property(e => e.TopicTags).HasColumnName("topic_tags").HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
            entity.Property(e => e.IsSubstantive).HasColumnName("is_substantive");
            entity.Property(e => e.EnrichedAt).HasColumnName("enriched_at");
            entity.Property(e => e.EnrichmentVersion).HasColumnName("enrichment_version");

            entity.HasOne(e => e.Message)
                  .WithOne(m => m.Enrichment)
                  .HasForeignKey<MessageEnrichmentEntity>(e => e.MessageId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.EnrichmentVersion)
                  .HasDatabaseName("ix_message_enrichments_version");

            entity.HasIndex(e => e.TopicTags)
                  .HasMethod("gin")
                  .HasDatabaseName("ix_message_enrichments_topic_tags");
        });

        // ------------------------------------------------------------------
        // Views. Created/dropped via raw SQL in the initial migration; EF
        // only needs to know their shape so the read-side queries compile.
        // ------------------------------------------------------------------

        modelBuilder.Entity<GuildCurrentView>(entity =>
        {
            entity.ToView("guilds_current");
            entity.HasNoKey();
            entity.Property(e => e.GuildId).HasColumnName("guild_id");
            entity.Property(e => e.FetchedAt).HasColumnName("fetched_at");
            entity.Property(e => e.Payload).HasColumnName("payload");
        });

        modelBuilder.Entity<ChannelCurrentView>(entity =>
        {
            entity.ToView("channels_current");
            entity.HasNoKey();
            entity.Property(e => e.ChannelId).HasColumnName("channel_id");
            entity.Property(e => e.GuildId).HasColumnName("guild_id");
            entity.Property(e => e.FetchedAt).HasColumnName("fetched_at");
            entity.Property(e => e.Payload).HasColumnName("payload");
        });

        modelBuilder.Entity<PinCurrentView>(entity =>
        {
            entity.ToView("pins_current");
            entity.HasNoKey();
            entity.Property(e => e.ChannelId).HasColumnName("channel_id");
            entity.Property(e => e.FetchedAt).HasColumnName("fetched_at");
            entity.Property(e => e.Payload).HasColumnName("payload");
        });
    }
}
