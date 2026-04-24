namespace DiscordScraper.Discord.Models;

/// <summary>
/// Response shape for <c>GET /channels/{id}/pins</c>. Carries the original JSON
/// array for <c>raw_pins.payload</c> storage plus pre-parsed per-message
/// records so the sync worker can upsert <c>raw_messages</c> and append
/// <c>raw_message_edits</c> without re-parsing.
/// </summary>
public sealed record DiscordChannelPins(
    string PayloadJson,
    IReadOnlyList<DiscordMessageRaw> Messages);
