namespace DiscordScraper.Contracts;

public interface ITimestamped
{
    DateTimeOffset LastUpdatedAt { get; set; }
}
