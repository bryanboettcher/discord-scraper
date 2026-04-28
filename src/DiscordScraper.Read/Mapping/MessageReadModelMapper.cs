using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.IR;
using DiscordScraper.Read.Data.Entities;
using Riok.Mapperly.Abstractions;

namespace DiscordScraper.Read.Mapping;

/// <summary>
/// Mapperly-generated mapper for the message read model. Handles flat field projection from
/// MessageEnriched to ReadMessage. Computed boolean flags (HasCode, HasAttachments, HasEmbeds)
/// and derived text (PlainText) are excluded from source generation and filled by the consumer
/// after this call — those require IR tree walks, not 1:1 property copies.
/// </summary>
[Mapper]
public static partial class MessageReadModelMapper
{
    /// <summary>
    /// Projects the scalar fields of a MessageEnriched event onto a ReadMessage entity.
    /// Callers must populate <see cref="ReadMessage.PlainText"/>, <see cref="ReadMessage.HasCode"/>,
    /// <see cref="ReadMessage.HasAttachments"/>, and <see cref="ReadMessage.HasEmbeds"/> after this call.
    /// </summary>
    [MapProperty(nameof(MessageEnriched.MessageSnowflake), nameof(ReadMessage.MessageId))]
    [MapProperty(nameof(MessageEnriched.IR),               nameof(ReadMessage.Ir))]
    [MapProperty(nameof(MessageEnriched.EditedTimestamp),  nameof(ReadMessage.EditedAt))]
    [MapProperty(nameof(MessageEnriched.MessageCreatedAt), nameof(ReadMessage.CreatedAt))]
    [MapperIgnoreTarget(nameof(ReadMessage.PlainText))]
    [MapperIgnoreTarget(nameof(ReadMessage.Tsv))]
    [MapperIgnoreTarget(nameof(ReadMessage.HasCode))]
    [MapperIgnoreTarget(nameof(ReadMessage.HasAttachments))]
    [MapperIgnoreTarget(nameof(ReadMessage.HasEmbeds))]
    [MapperIgnoreTarget(nameof(ReadMessage.ReplyToId))]
    // Tags are handled separately via MessageTag entities (ToMessageTags below); not a flat field.
    [MapperIgnoreSource(nameof(MessageEnriched.Tags))]
    // MessageId on source is the Guid form — already mapped via MessageSnowflake → MessageId above.
    [MapperIgnoreSource(nameof(MessageEnriched.MessageId))]
    // CurrentState, CorrelationId, LastUpdatedAt are MassTransit-internal; not persisted to read_messages.
    [MapperIgnoreSource(nameof(MessageEnriched.CurrentState))]
    [MapperIgnoreSource(nameof(MessageEnriched.CorrelationId))]
    [MapperIgnoreSource(nameof(MessageEnriched.LastUpdatedAt))]
    public static partial ReadMessage ToReadMessage(MessageEnriched evt);

    public static IReadOnlyList<MessageTag> ToMessageTags(long messageId, IReadOnlyList<string> tags)
        => tags.Select(t => new MessageTag { MessageId = messageId, Tag = t }).ToList();
}
