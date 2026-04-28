namespace DiscordScraper.Ingester.SmokeTest;

// Internal to Ingester — not part of Contracts. Phase 1C smoke test only.
internal record PingPublished(Guid CorrelationId, DateTimeOffset SentAt);
