namespace DiscordScraper.Contracts.Clock;

public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
}
