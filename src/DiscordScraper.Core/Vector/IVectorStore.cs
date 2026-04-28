namespace DiscordScraper.Core.Vector;

public interface IVectorStore
{
    Task UpsertManyAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct);
    Task<IReadOnlyList<VectorMatch>> SearchAsync(ReadOnlyMemory<float> query, VectorFilter filter, int topK, CancellationToken ct);
}
