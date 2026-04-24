using DiscordScraper.Storage.Entities;

namespace DiscordScraper.Storage.Repositories;

/// <summary>
/// Append-only writer for <c>raw_message_edits</c>. The PK is
/// <c>(message_id, edited_at)</c> so duplicate observations of the same edit
/// (same timestamp) are quietly absorbed; each genuine new edit lands as a
/// fresh row. Populated today only by the pin poller — edit visibility is
/// limited to currently-pinned messages until a gateway subsystem is built.
/// </summary>
public interface IRawMessageEditRepository
{
    /// <summary>
    /// Inserts each edit observation; existing <c>(message_id, edited_at)</c>
    /// rows are left untouched. Returns the number actually inserted.
    /// </summary>
    Task<int> InsertIfNewAsync(IReadOnlyList<RawMessageEditEntity> edits, CancellationToken ct = default);
}
