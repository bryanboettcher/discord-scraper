using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace DiscordScraper.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ingestion_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    worker = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    items_processed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "raw_channels",
                columns: table => new
                {
                    channel_id = table.Column<long>(type: "bigint", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raw_channels", x => new { x.channel_id, x.fetched_at });
                });

            migrationBuilder.CreateTable(
                name: "raw_guilds",
                columns: table => new
                {
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raw_guilds", x => new { x.guild_id, x.fetched_at });
                });

            migrationBuilder.CreateTable(
                name: "raw_message_edits",
                columns: table => new
                {
                    message_id = table.Column<long>(type: "bigint", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raw_message_edits", x => new { x.message_id, x.edited_at });
                });

            migrationBuilder.CreateTable(
                name: "raw_messages",
                columns: table => new
                {
                    message_id = table.Column<long>(type: "bigint", nullable: false),
                    channel_id = table.Column<long>(type: "bigint", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raw_messages", x => x.message_id);
                });

            migrationBuilder.CreateTable(
                name: "raw_sync_state",
                columns: table => new
                {
                    channel_id = table.Column<long>(type: "bigint", nullable: false),
                    last_message_id = table.Column<long>(type: "bigint", nullable: false),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    consecutive_errors = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raw_sync_state", x => x.channel_id);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    message_id = table.Column<long>(type: "bigint", nullable: false),
                    channel_id = table.Column<long>(type: "bigint", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    author_id = table.Column<long>(type: "bigint", nullable: false),
                    author_name = table.Column<string>(type: "text", nullable: false),
                    author_is_bot = table.Column<bool>(type: "boolean", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reply_to_id = table.Column<long>(type: "bigint", nullable: true),
                    thread_id = table.Column<long>(type: "bigint", nullable: true),
                    root_channel_id = table.Column<long>(type: "bigint", nullable: false),
                    has_attachments = table.Column<bool>(type: "boolean", nullable: false),
                    content_tsv = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "to_tsvector('english', content)", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.message_id);
                    table.ForeignKey(
                        name: "FK_messages_raw_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "raw_messages",
                        principalColumn: "message_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "message_enrichments",
                columns: table => new
                {
                    message_id = table.Column<long>(type: "bigint", nullable: false),
                    embedding_model = table.Column<string>(type: "text", nullable: false),
                    qdrant_point_id = table.Column<string>(type: "text", nullable: false),
                    topic_tags = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    is_substantive = table.Column<bool>(type: "boolean", nullable: false),
                    enriched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    enrichment_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_enrichments", x => x.message_id);
                    table.ForeignKey(
                        name: "FK_message_enrichments_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "messages",
                        principalColumn: "message_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ingestion_runs_worker_started",
                table: "ingestion_runs",
                columns: new[] { "worker", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_message_enrichments_topic_tags",
                table: "message_enrichments",
                column: "topic_tags")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_message_enrichments_version",
                table: "message_enrichments",
                column: "enrichment_version");

            migrationBuilder.CreateIndex(
                name: "ix_messages_channel_created",
                table: "messages",
                columns: new[] { "channel_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_messages_content_tsv",
                table: "messages",
                column: "content_tsv")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_messages_guild_created",
                table: "messages",
                columns: new[] { "guild_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_messages_reply_to_id",
                table: "messages",
                column: "reply_to_id",
                filter: "reply_to_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messages_thread_id",
                table: "messages",
                column: "thread_id",
                filter: "thread_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_raw_messages_channel_created",
                table: "raw_messages",
                columns: new[] { "channel_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_raw_messages_guild_created",
                table: "raw_messages",
                columns: new[] { "guild_id", "created_at" },
                descending: new[] { false, true });

            // Views are disposable reads over the append-only raw_guilds /
            // raw_channels snapshot logs. DISTINCT ON returns the most recent
            // row per entity, which is how the sync worker resolves current
            // names/topics/parents without materialized maintenance.
            migrationBuilder.Sql("""
                CREATE VIEW guilds_current AS
                    SELECT DISTINCT ON (guild_id) guild_id, fetched_at, payload
                    FROM raw_guilds
                    ORDER BY guild_id, fetched_at DESC;
                """);

            migrationBuilder.Sql("""
                CREATE VIEW channels_current AS
                    SELECT DISTINCT ON (channel_id) channel_id, guild_id, fetched_at, payload
                    FROM raw_channels
                    ORDER BY channel_id, fetched_at DESC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Views depend on raw_channels / raw_guilds; drop before the tables
            // or Postgres rejects the DropTable with a dependency violation.
            migrationBuilder.Sql("DROP VIEW IF EXISTS channels_current;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS guilds_current;");

            migrationBuilder.DropTable(
                name: "ingestion_runs");

            migrationBuilder.DropTable(
                name: "message_enrichments");

            migrationBuilder.DropTable(
                name: "raw_channels");

            migrationBuilder.DropTable(
                name: "raw_guilds");

            migrationBuilder.DropTable(
                name: "raw_message_edits");

            migrationBuilder.DropTable(
                name: "raw_sync_state");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "raw_messages");
        }
    }
}
