using MassTransit;

namespace DiscordScraper.Contracts.Events.Sync;

/// <summary>
/// Published when the LLM classification model is upgraded. MT CorrelateBy dispatches this
/// to every MessageSaga in state Enriched whose ClassifyModelVersion differs from the new model,
/// re-entering the Classify path for each match (embedding preserved, Tag phase skipped).
///
/// Trigger: direct bus publish (e.g. via admin endpoint or manual IBus.Publish).
/// </summary>
[ExcludeFromTopology]
public interface ClassificationInvalidated
{
    /// <summary>Identifier of the new classification model. Sagas with ClassifyModelVersion != this re-enter ClassifyRequest.</summary>
    string ModelVersion { get; }

    /// <summary>
    /// Sagas with LastUpdatedAt before this cutoff are eligible for re-classification. Prevents
    /// fan-out from looping if the new model produces a result the saga then publishes after this cutoff.
    /// </summary>
    DateTimeOffset Cutoff { get; }
}
