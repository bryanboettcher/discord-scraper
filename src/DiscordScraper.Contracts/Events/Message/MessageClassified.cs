// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Message;

/// <summary>
/// Published by ClassifyConsumer after LLM topic tagging and vector store indexing complete.
/// IndexedAt is the business timestamp set by the consumer after the vector store write —
/// used by ClassificationInvalidated to match sagas indexed before a model upgrade cutoff.
/// </summary>
public interface MessageClassified : MessageStateChanged
{
    IReadOnlyList<string> Tags { get; }
    string ClassifyModelVersion { get; }
    DateTimeOffset IndexedAt { get; }
}
