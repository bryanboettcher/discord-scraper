// ReSharper disable InconsistentNaming
using DiscordScraper.Contracts.IR;

namespace DiscordScraper.Contracts.Events.Message;

/// <summary>Published after ProjectMessageConsumer populates the IR on the saga.</summary>
public interface MessageProjected : MessageStateChanged
{
    MessageIR IR { get; }
}
