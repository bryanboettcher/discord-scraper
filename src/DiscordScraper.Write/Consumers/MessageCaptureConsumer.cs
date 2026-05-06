using DiscordScraper.Contracts.Configuration;
using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Contracts.Events.Message;
using MassTransit;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Write.Consumers;

/// <summary>
/// Appends lifecycle fact bodies to a JSONL capture file for use as test fixtures.
///
/// When <see cref="CaptureOptions.Enabled"/> is false the consumer body no-ops; the full
/// MT consume pipeline still executes (deserialization, scope creation, middleware). This is
/// intentional — the phantom load doubles as a passive bus-capacity exerciser during
/// performance-focused runs without requiring a separate load-generator instance.
///
/// All four <c>IConsumer&lt;T&gt;</c> implementations delegate to a shared
/// <see cref="CaptureWriter"/> singleton, which owns the file lock. MT instantiates one
/// consumer scope per receive, but the underlying writer is shared so the header is written
/// exactly once per file.
/// </summary>
public sealed class MessageCaptureConsumer(
    IOptions<CaptureOptions> options,
    CaptureWriter writer) :
    IConsumer<MessageCaptured>,
    IConsumer<MessageEditObserved>,
    IConsumer<ChannelChanged>,
    IConsumer<GuildChanged>
{
    private readonly CaptureOptions _options = options.Value;

    public Task Consume(ConsumeContext<MessageCaptured> context)
        => _options.Enabled
            ? writer.AppendAsync("messageCaptured", context.Message, context.Message.GuildId, context.CancellationToken)
            : Task.CompletedTask;

    public Task Consume(ConsumeContext<MessageEditObserved> context)
        => _options.Enabled
            ? writer.AppendAsync("messageEditObserved", context.Message, context.Message.GuildId, context.CancellationToken)
            : Task.CompletedTask;

    public Task Consume(ConsumeContext<ChannelChanged> context)
        => _options.Enabled
            ? writer.AppendAsync("channelChanged", context.Message, context.Message.GuildId, context.CancellationToken)
            : Task.CompletedTask;

    public Task Consume(ConsumeContext<GuildChanged> context)
        => _options.Enabled
            ? writer.AppendAsync("guildChanged", context.Message, context.Message.GuildId, context.CancellationToken)
            : Task.CompletedTask;
}
