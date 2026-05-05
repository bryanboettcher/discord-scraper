namespace DiscordScraper.Contracts;

public interface ITimestamped
{
    DateTimeOffset UpdatedOn { get; set; }
}
