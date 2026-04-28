using MassTransit;

namespace DiscordScraper.Contracts.Events.Sync;

/// <summary>
/// Published when the tagging model or prompt changes. MT CorrelateBy dispatches this to every
/// MessageSaga in state Enriched whose TagModelVersion differs from the new model,
/// re-entering the Enhance → Index path for each match.
///
/// v1 over-work: EnhanceMessage re-runs both tagging and embedding even for a tag-only
/// upgrade. A split Embed/Tag consumer is a clean follow-up once separation is warranted.
///
/// Trigger: direct bus publish (e.g. via admin endpoint or manual IBus.Publish).
/// Example: Publish&lt;ReTagRequested&gt;(new { ModelVersion = "llama3.1:70b", Cutoff = DateTimeOffset.UtcNow })
/// </summary>
[ExcludeFromTopology]
public interface ReTagRequested
{
    /// <summary>Identifier of the new tagging model. Sagas with TagModelVersion != this re-enter EnhanceMessage.</summary>
    string ModelVersion { get; }

    /// <summary>
    /// Sagas with LastUpdatedAt before this cutoff are eligible for re-enrichment. Prevents
    /// fan-out from looping if the new model produces a result the saga then publishes after this cutoff.
    /// </summary>
    DateTimeOffset Cutoff { get; }
}
