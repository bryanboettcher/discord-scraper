using MassTransit;

namespace DiscordScraper.Contracts.Events.Sync;

/// <summary>
/// Published when the embedding model is upgraded. MT CorrelateBy dispatches this to every
/// MessageSaga in state Enriched whose EmbeddingModelVersion differs from the new model,
/// re-entering the Tag → Classify path for each match.
///
/// Trigger: direct bus publish (e.g. via admin endpoint or manual IBus.Publish).
/// </summary>
[ExcludeFromTopology]
public interface TagsInvalidated
{
    /// <summary>Identifier of the new embedding model. Sagas with EmbeddingModelVersion != this re-enter TagRequest.</summary>
    string ModelVersion { get; }

    /// <summary>
    /// Sagas with LastUpdatedAt before this cutoff are eligible for re-tagging. Prevents
    /// fan-out from looping if the new model produces a result the saga then publishes after this cutoff.
    /// </summary>
    DateTimeOffset Cutoff { get; }
}
