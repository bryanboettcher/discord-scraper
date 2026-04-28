namespace DiscordScraper.Contracts.Requests;

public sealed record EnhanceMessageResponse(
    IReadOnlyList<string> Tags,
    IReadOnlyList<float> Embedding,
    string EmbeddingModelVersion,
    string TagModelVersion);
