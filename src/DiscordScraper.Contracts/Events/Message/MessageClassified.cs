// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Message;

/// <summary>
/// Published after ClassifyRequest.Completed; carries LLM tags and classify model version.
/// Replaces MessageIndexed — ClassifyConsumer absorbed indexing so the leaf event is now
/// MessageClassified rather than the deferred MessageIndexed.
/// </summary>
public interface MessageClassified : MessageStateChanged
{
    IReadOnlyList<string> Tags { get; }
    string ClassifyModelVersion { get; }
}
