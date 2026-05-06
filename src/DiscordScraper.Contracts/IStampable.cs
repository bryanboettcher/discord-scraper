namespace DiscordScraper.Contracts;

/// <summary>
/// Marker for message-body contracts (records sent over the bus) that carry a publish-time
/// timestamp. Intentionally independent of <see cref="ITimestamped"/>, which tracks saga
/// state-transition time and is implemented by saga states that are NOT sent over the bus.
///
/// Convention: <c>Timestamp</c> is initialized to <c>default</c> (Ticks == 0) by message
/// constructors. <see cref="Filters.TimestampFilter{T}"/> detects the zero value and directly
/// sets <c>Timestamp</c> on the message object before the body is serialized. Consumers read
/// the stamped value from <c>context.Message.Timestamp</c>.
///
/// <c>Timestamp</c> is mutable (<c>{ get; set; }</c>) so the filter can stamp without
/// <c>CreateProxy</c> — required because the InMemory transport casts its send context to a
/// concrete type that does not propagate proxy-swapped messages through serialization.
///
/// Only concrete <c>sealed record</c> types sent over the bus implement this interface.
/// Saga states (<see cref="ITimestamped"/> implementors) do NOT implement this.
/// </summary>
public interface IStampable
{
    /// <summary>
    /// UTC instant this message was enqueued by the send/publish pipeline.
    /// Mutable so <see cref="Filters.TimestampFilter{T}"/> can stamp before serialization.
    /// Default (Ticks == 0) until stamped by the filter.
    /// </summary>
    DateTimeOffset Timestamp { get; set; }
}
