using System.Text.Json;
using DiscordScraper.Contracts.IR;
using DiscordScraper.Read.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DiscordScraper.Read.Data.Configurations;

internal sealed class ReadMessageConfiguration : IEntityTypeConfiguration<ReadMessage>
{
    // Reuse a single serializer options instance — JsonPolymorphic/JsonDerivedType attributes
    // on MessageNode are discovered at startup; no per-call overhead.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<ReadMessage> b)
    {
        b.ToTable("read_messages");

        // EF uses MessageId as the tracking key. The DB-level table is a hypertable with no
        // physical PK constraint — see architecture-plan.md for the reasoning.
        b.HasKey(e => e.MessageId);
        b.Property(e => e.MessageId).HasColumnName("message_id").ValueGeneratedNever();

        b.Property(e => e.ChannelId).HasColumnName("channel_id");
        b.Property(e => e.GuildId).HasColumnName("guild_id");
        b.Property(e => e.AuthorId).HasColumnName("author_id");
        b.Property(e => e.CreatedAt).HasColumnName("created_at");
        b.Property(e => e.EditedAt).HasColumnName("edited_at");
        b.Property(e => e.ReplyToId).HasColumnName("reply_to_id");
        b.Property(e => e.PlainText).HasColumnName("plain_text");
        b.Property(e => e.HasCode).HasColumnName("has_code");
        b.Property(e => e.HasAttachments).HasColumnName("has_attachments");
        b.Property(e => e.HasEmbeds).HasColumnName("has_embeds");
        b.Property(e => e.IsSubstantive).HasColumnName("is_substantive");
        b.Property(e => e.IsBot).HasColumnName("is_bot");

        b.Property(e => e.Ir)
            .HasColumnName("ir")
            .HasColumnType("jsonb")
            .HasConversion(new ValueConverter<MessageIR, string>(
                ir => JsonSerializer.Serialize(ir, SerializerOptions),
                json => JsonSerializer.Deserialize<MessageIR>(json, SerializerOptions)!));

        // Tsv is a GENERATED ALWAYS AS column with type 'tsvector' — Npgsql's EF Core provider
        // doesn't have a built-in mapping for tsvector to string. Ignore the property in EF;
        // ReadSchemaInitializer creates the column + GIN index via raw DDL, and PgSearchService
        // queries it directly through Npgsql (also bypassing EF). The CLR property exists on the
        // entity for completeness but is never read or written by the DbContext.
        b.Ignore(e => e.Tsv);

        // Btree indexes mirroring the DDL in the architecture plan.
        b.HasIndex(e => e.MessageId)
            .HasDatabaseName("read_messages_message_id_idx");

        b.HasIndex(e => new { e.ChannelId, e.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("read_messages_channel_created_idx");

        b.HasIndex(e => e.AuthorId)
            .HasDatabaseName("read_messages_author_idx");

        // GIN index on tsv lives in raw DDL (ReadSchemaInitializer) since Tsv is Ignore'd.
    }
}
