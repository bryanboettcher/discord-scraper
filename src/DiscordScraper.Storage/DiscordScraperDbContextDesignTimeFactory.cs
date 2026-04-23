using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DiscordScraper.Storage;

/// <summary>
/// Used only by the <c>dotnet ef</c> CLI tooling to spin up a
/// <see cref="DiscordScraperDbContext"/> without the full host. Reads the
/// connection string from <c>DISCORD_SCRAPER_DESIGN_TIME_CONNECTION</c> if
/// set, otherwise defaults to the docker-compose-managed local Postgres.
/// Not part of the runtime path.
/// </summary>
public sealed class DiscordScraperDbContextDesignTimeFactory : IDesignTimeDbContextFactory<DiscordScraperDbContext>
{
    public DiscordScraperDbContext CreateDbContext(string[] args)
    {
        var connection =
            Environment.GetEnvironmentVariable("DISCORD_SCRAPER_DESIGN_TIME_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=discord_scraper;Username=discord;Password=discord";

        var options = new DbContextOptionsBuilder<DiscordScraperDbContext>()
            .UseNpgsql(connection)
            .Options;

        return new DiscordScraperDbContext(options);
    }
}
