// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Message;

/// <summary>Published after TagRequest.Completed; carries the embedding vector and model version.</summary>
public interface MessageTagged : MessageStateChanged
{
    float[] Embedding { get; }
    string EmbeddingModelVersion { get; }
}
