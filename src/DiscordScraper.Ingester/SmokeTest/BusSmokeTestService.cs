using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Ingester.SmokeTest;

internal sealed class BusSmokeTestService(
    IPublishEndpoint publish,
    BusSmokeTestCompletionSource completion,
    ILogger<BusSmokeTestService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // IPublishEndpoint is available immediately but RabbitMQ topology may not be bound yet;
        // a short delay lets MT finish endpoint setup before the first publish.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        var correlationId = Guid.NewGuid();
        logger.LogInformation("Smoke test publishing ping — correlation={CorrelationId}", correlationId);

        await publish.Publish(new PingPublished(correlationId, DateTimeOffset.UtcNow), cancellationToken);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await completion.Task.WaitAsync(linked.Token);
            logger.LogInformation("Smoke test PASSED — bus boots, outbox round-trips, consumer fires.");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            logger.LogError(
                "Smoke test FAILED — PingPublished was not consumed within 30 seconds. " +
                "Check RabbitMQ connectivity, Mongo outbox config, and consumer registration.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
