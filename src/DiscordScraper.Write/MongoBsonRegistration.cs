using DiscordScraper.Contracts.IR;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;

namespace DiscordScraper.Write;

/// <summary>
/// Registers BSON class maps for the polymorphic MessageNode hierarchy and the
/// MessageIR / AttachmentIR / EmbedIR / ReplyContext types stored as sub-documents inside
/// MessageSagaState.IR.
/// </summary>
/// <remarks>
/// Explicit per-type TryRegisterClassMap rather than a discriminator convention — conventions
/// apply globally and can collide with the MT Mongo integration's own CamelCaseElementNameConvention
/// + SagaConvention registered for ISagaVersion types.
///
/// Discriminator values mirror the STJ "$t" short tokens from MessageNode.cs so both wire formats
/// share a vocabulary. Treat the strings as stable: changing one requires migrating every
/// MessageSagaState.IR document already persisted in Mongo.
///
/// Call <see cref="RegisterAll"/> once at startup before the bus starts. The method is idempotent
/// (TryRegisterClassMap is a no-op when a map is already registered).
/// </remarks>
public static class MongoBsonRegistration
{
    private static bool _registered;
    private static readonly Lock _lock = new();

    public static void RegisterAll()
    {
        if (_registered) return;
        lock (_lock)
        {
            if (_registered) return;

            RegisterMessageNodeHierarchy();
            RegisterMessageIrTypes();

            _registered = true;
        }
    }

    private static void RegisterMessageNodeHierarchy()
    {
        // Abstract base — AutoMap; no discriminator required (it's never stored directly).
        // The discriminator is written by the concrete subtypes.
        BsonClassMap.TryRegisterClassMap<MessageNode>(cm =>
        {
            cm.AutoMap();
            cm.SetIsRootClass(true);
            cm.SetDiscriminatorIsRequired(true);
        });

        BsonClassMap.TryRegisterClassMap<TextNode>(cm =>
        {
            cm.AutoMap();
            cm.SetDiscriminator("text");
        });

        BsonClassMap.TryRegisterClassMap<MentionNode>(cm =>
        {
            cm.AutoMap();
            cm.SetDiscriminator("mention");
        });

        BsonClassMap.TryRegisterClassMap<ChannelRefNode>(cm =>
        {
            cm.AutoMap();
            cm.SetDiscriminator("chan");
        });

        BsonClassMap.TryRegisterClassMap<EmojiNode>(cm =>
        {
            cm.AutoMap();
            cm.SetDiscriminator("emoji");
        });

        BsonClassMap.TryRegisterClassMap<TimestampNode>(cm =>
        {
            cm.AutoMap();
            cm.SetDiscriminator("ts");
            // DateTimeOffset is not natively handled by BSON — store as UTC DateTime ticks.
            // The driver maps DateTimeOffset → DateTime via BsonDateTimeSerializer by convention;
            // offset is lost but timestamps in Discord are UTC, so this is acceptable.
        });

        BsonClassMap.TryRegisterClassMap<FormattingNode>(cm =>
        {
            cm.AutoMap();
            cm.SetDiscriminator("fmt");
        });

        BsonClassMap.TryRegisterClassMap<CodeBlockNode>(cm =>
        {
            cm.AutoMap();
            cm.SetDiscriminator("code");
        });

        BsonClassMap.TryRegisterClassMap<InlineCodeNode>(cm =>
        {
            cm.AutoMap();
            cm.SetDiscriminator("icode");
        });

        BsonClassMap.TryRegisterClassMap<QuoteNode>(cm =>
        {
            cm.AutoMap();
            cm.SetDiscriminator("quote");
        });

        BsonClassMap.TryRegisterClassMap<LinkNode>(cm =>
        {
            cm.AutoMap();
            cm.SetDiscriminator("link");
        });
    }

    private static void RegisterMessageIrTypes()
    {
        // These types are not polymorphic — no discriminator needed. AutoMap handles
        // the CamelCase field naming that the broader MT Mongo convention pack also applies,
        // but explicit registration ensures we control the mapping even if the convention
        // pack isn't registered (e.g., in test contexts).
        BsonClassMap.TryRegisterClassMap<MessageIR>(cm => cm.AutoMap());
        BsonClassMap.TryRegisterClassMap<AttachmentIR>(cm => cm.AutoMap());
        BsonClassMap.TryRegisterClassMap<EmbedIR>(cm => cm.AutoMap());
        BsonClassMap.TryRegisterClassMap<ReplyContext>(cm => cm.AutoMap());
    }
}
