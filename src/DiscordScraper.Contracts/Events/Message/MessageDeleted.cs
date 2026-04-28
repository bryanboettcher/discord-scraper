// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Message;

/// <summary>Published when Discord reports a message deletion.</summary>
public interface MessageDeleted : BaseMessageEvent
{
    DateTimeOffset DeletedAt { get; }
}
