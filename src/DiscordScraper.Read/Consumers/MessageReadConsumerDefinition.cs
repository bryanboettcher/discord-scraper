using DiscordScraper.Contracts.Events.Message;

namespace DiscordScraper.Read.Consumers;

/// <summary>Configures the MessageReadConsumer endpoint via the base class defaults.</summary>
internal sealed class MessageReadConsumerDefinition
    : ReadModelBatchConsumerDefinition<MessageReadConsumer, MessageStateChanged>;
