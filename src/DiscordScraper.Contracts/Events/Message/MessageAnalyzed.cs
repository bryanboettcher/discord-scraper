// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Message;

/// <summary>Published after AnalyzeMessageConsumer completes; carries substantiveness + language facts.</summary>
public interface MessageAnalyzed : MessageStateChanged
{
    bool IsSubstantive { get; }
    bool IsBot { get; }
    string? DetectedLanguage { get; }
}
