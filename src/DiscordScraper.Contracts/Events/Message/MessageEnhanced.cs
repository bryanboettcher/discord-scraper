// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Message;

/// <summary>Published after EnhanceMessageConsumer returns tags and embedding.</summary>
public interface MessageEnhanced : MessageStateChanged
{
    IReadOnlyList<string> Tags { get; }
    IReadOnlyList<float> Embedding { get; }
}
