using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscordScraper.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddRawPins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "raw_pins",
                columns: table => new
                {
                    channel_id = table.Column<long>(type: "bigint", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raw_pins", x => new { x.channel_id, x.fetched_at });
                });

            migrationBuilder.Sql("""
                CREATE VIEW pins_current AS
                    SELECT DISTINCT ON (channel_id) channel_id, fetched_at, payload
                    FROM raw_pins
                    ORDER BY channel_id, fetched_at DESC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS pins_current;");

            migrationBuilder.DropTable(
                name: "raw_pins");
        }
    }
}
