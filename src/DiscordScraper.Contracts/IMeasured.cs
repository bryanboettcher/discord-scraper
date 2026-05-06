namespace DiscordScraper.Contracts;

/// <summary>
/// Extends <see cref="IStampable"/> with a consume-side timestamp, enabling end-to-end
/// transport latency measurement without a send-side observer.
///
/// <c>Timestamp</c> (inherited) is stamped by <see cref="Filters.OutboundTimestampFilter{T}"/>
/// at publish/send time. <c>ReceivedOn</c> is stamped by
/// <see cref="Filters.InboundTimestampFilter{T}"/> on the consume pipe.
///
/// <c>TransportLatency</c> is a default interface method — no concrete override needed.
/// Accessible via an <c>IMeasured</c> reference.
/// </summary>
public interface IMeasured : IStampable
{
    /// <summary>
    /// UTC instant this message was received by the consume pipeline.
    /// Mutable so <see cref="Filters.InboundTimestampFilter{T}"/> can stamp before the
    /// consumer body executes. Default (Ticks == 0) until stamped.
    /// </summary>
    DateTimeOffset ReceivedOn { get; set; }

    /// <summary>
    /// Wall-clock transport latency: time between publish and consume-pipeline entry.
    /// Derived via default interface method — no concrete override required.
    /// </summary>
    TimeSpan TransportLatency => ReceivedOn - Timestamp;
}
