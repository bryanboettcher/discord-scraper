using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Storage;

/// <summary>
/// Applies pending EF Core migrations at startup. Retries Postgres connection
/// errors while docker-compose brings the database up in parallel.
/// </summary>
/// <remarks>
/// Only one host in the system (the ingester) should run this. The API host
/// is a pure reader and must not race against schema changes.
/// </remarks>
public sealed class MigrationHostedService(
    IDbContextFactory<DiscordScraperDbContext> factory,
    ILogger<MigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        const int maxRetries = 10;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await using var context = await factory.CreateDbContextAsync(ct);

                var pending = (await context.Database.GetPendingMigrationsAsync(ct)).ToArray();
                if (pending.Length == 0)
                {
                    logger.LogInformation("Database schema up to date");
                    return;
                }

                logger.LogInformation(
                    "Applying {Count} pending migrations: {Migrations}",
                    pending.Length,
                    string.Join(", ", pending));

                await context.Database.MigrateAsync(ct);
                logger.LogInformation("Database migrations applied");
                return;
            }
            catch (Exception ex) when (attempt < maxRetries && !ct.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex,
                    "Database not ready (attempt {Attempt}/{MaxRetries}), retrying in 2s...",
                    attempt,
                    maxRetries);
                await Task.Delay(2000, ct);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
