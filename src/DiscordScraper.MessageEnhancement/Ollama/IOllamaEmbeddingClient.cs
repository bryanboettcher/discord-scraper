namespace DiscordScraper.MessageEnhancement.Ollama;

/// <summary>
/// Thin typed HttpClient over Ollama's <c>/api/embed</c> endpoint. The model
/// identity is surfaced so downstream (storage, vector point ids) can tag
/// vectors with the model they came from — bumping models becomes a version
/// re-enrich rather than a silent mix of incompatible vectors.
/// </summary>
public interface IOllamaEmbeddingClient
{
    /// <summary>Model string the server will be asked to use.</summary>
    string Model { get; }

    Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct = default);
}
