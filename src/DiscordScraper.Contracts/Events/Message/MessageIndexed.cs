// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Message;

/// <summary>Published after IndexMessageConsumer writes to the vector store.</summary>
public interface MessageIndexed : MessageStateChanged
{
    DateTimeOffset IndexedAt { get; }
}
