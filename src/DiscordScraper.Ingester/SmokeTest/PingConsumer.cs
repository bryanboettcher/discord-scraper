using MassTransit;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Ingester.SmokeTest;

internal sealed class PingConsumer(
    ILogger<PingConsumer> logger,
    BusSmokeTestCompletionSource completion) : IConsumer<PingPublished>
{
    public Task Consume(ConsumeContext<PingPublished> context)
    {
        var elapsed = DateTimeOffset.UtcNow - context.Message.SentAt;
        logger.LogInformation(
            "Smoke test ping received — correlation={CorrelationId}, round-trip={ElapsedMs}ms",
            context.Message.CorrelationId,
            elapsed.TotalMilliseconds);

        completion.TrySetResult(true);
        return Task.CompletedTask;
    }
}
