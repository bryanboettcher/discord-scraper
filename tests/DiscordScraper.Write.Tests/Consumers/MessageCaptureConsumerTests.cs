using System.Text.Json;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Configuration;
using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.TestSupport.Capture;
using DiscordScraper.Write.Consumers;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DiscordScraper.Write.Tests.Consumers;

[TestFixture]
public sealed class MessageCaptureConsumerTests
{
    private const long GuildId   = 1_374_441_548_594_282_659L;
    private const long ChannelId = 2_000_000_000_000_001L;
    private const long MsgSnowflake = 3_000_000_000_000_001L;

    // ---------------------------------------------------------------------------
    // No-op when disabled: consumer consumes but does NOT write any file
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_WhenDisabled_NoOpAndNoFileCreated()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await using var provider = BuildProvider(enabled: false, outputDir);
            var harness = provider.GetTestHarness();
            await harness.Start();

            await PublishAllFourFacts(harness.Bus);

            var consumerHarness = harness.GetConsumerHarness<MessageCaptureConsumer>();
            // All four interface bindings must be consumed
            (await consumerHarness.Consumed.Any<MessageCaptured>()).ShouldBeTrue("MessageCaptured must be consumed");
            (await consumerHarness.Consumed.Any<MessageEditObserved>()).ShouldBeTrue("MessageEditObserved must be consumed");
            (await consumerHarness.Consumed.Any<ChannelChanged>()).ShouldBeTrue("ChannelChanged must be consumed");
            (await consumerHarness.Consumed.Any<GuildChanged>()).ShouldBeTrue("GuildChanged must be consumed");

            // No file should exist — the no-op consumer never writes
            Directory.Exists(outputDir).ShouldBeFalse(
                "Disabled consumer must not create the output directory or any file");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------
    // Round-trip: enable capture, publish all four fact types, read back JSONL,
    // verify body equality via FixtureReplayPublisher.LoadEnvelopes
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_WhenEnabled_WritesAllFourKindsToJsonl_RoundTrip()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await using var provider = BuildProvider(enabled: true, outputDir);
            var harness = provider.GetTestHarness();
            await harness.Start();

            await PublishAllFourFacts(harness.Bus);

            var consumerHarness = harness.GetConsumerHarness<MessageCaptureConsumer>();
            (await consumerHarness.Consumed.Any<MessageCaptured>()).ShouldBeTrue();
            (await consumerHarness.Consumed.Any<MessageEditObserved>()).ShouldBeTrue();
            (await consumerHarness.Consumed.Any<ChannelChanged>()).ShouldBeTrue();
            (await consumerHarness.Consumed.Any<GuildChanged>()).ShouldBeTrue();

            // Give the consumer a moment to flush all async writes before reading back
            await harness.Stop();

            var files = Directory.GetFiles(outputDir, "*.jsonl");
            files.Length.ShouldBe(1, "Exactly one JSONL file should be created per consumer instance");

            var lines = await File.ReadAllLinesAsync(files[0]);
            // 1 header + 4 body lines
            lines.Count(l => !string.IsNullOrWhiteSpace(l)).ShouldBe(5,
                "Should have 1 header + 4 body lines");

            // Verify header
            var header = JsonSerializer.Deserialize<CaptureHeader>(
                lines[0], new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            header.ShouldNotBeNull();
            header!.SchemaVersion.ShouldBe(1);
            header.SourceGuild.ShouldBe(GuildId.ToString());

            // Verify kinds via deserialized envelopes
            var envelopes = ParseEnvelopes(lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)));
            var kinds = envelopes.Select(e => e.Kind).ToList();
            kinds.ShouldContain("messageCaptured");
            kinds.ShouldContain("messageEditObserved");
            kinds.ShouldContain("channelChanged");
            kinds.ShouldContain("guildChanged");

            // Body equality check for MessageCaptured
            var mc = envelopes.Single(e => e.Kind == "messageCaptured")
                              .Data.Deserialize<MessageCapturedBody>(
                                  new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            mc.ShouldNotBeNull();
            mc!.MessageSnowflake.ShouldBe(MsgSnowflake);
            mc.GuildId.ShouldBe(GuildId);
            mc.ChannelId.ShouldBe(ChannelId);
            mc.PayloadJson.ShouldBe("{\"content\":\"hello\"}");
            mc.AuthorIsBot.ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------
    // Schema version guard: header always carries schemaVersion=1
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_WhenEnabled_HeaderContainsCorrectSchemaVersion()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await using var provider = BuildProvider(enabled: true, outputDir);
            var harness = provider.GetTestHarness();
            await harness.Start();

            // One message is enough to trigger header creation
            await harness.Bus.Publish<MessageCaptured>(BuildMessageCapturedAnon());
            var consumerHarness = harness.GetConsumerHarness<MessageCaptureConsumer>();
            (await consumerHarness.Consumed.Any<MessageCaptured>()).ShouldBeTrue();
            await harness.Stop();

            var file = Directory.GetFiles(outputDir, "*.jsonl").Single();
            var firstLine = (await File.ReadAllLinesAsync(file))[0];

            using var doc = JsonDocument.Parse(firstLine);
            doc.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
            doc.RootElement.GetProperty("kind").GetString().ShouldBe("header");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static ServiceProvider BuildProvider(bool enabled, string outputDir)
    {
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        var options = Options.Create(new CaptureOptions
        {
            Enabled = enabled,
            OutputPath = outputDir,
        });

        return new ServiceCollection()
            .AddSingleton<ISystemClock>(clock)
            .AddSingleton<IOptions<CaptureOptions>>(options)
            .AddSingleton<CaptureWriter>()
            .AddLogging()
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<MessageCaptureConsumer>();
            })
            .BuildServiceProvider(true);
    }

    private static async Task PublishAllFourFacts(IBus bus)
    {
        await bus.Publish<MessageCaptured>(BuildMessageCapturedAnon());

        await bus.Publish<MessageEditObserved>(new
        {
            MessageId = DiscordScraper.Contracts.DeterministicGuid.FromSnowflake(MsgSnowflake),
            MessageSnowflake = MsgSnowflake,
            ChannelId = ChannelId,
            GuildId = GuildId,
            AuthorId = 9_000_000_000_000_001L,
            CurrentState = "Edited",
            UpdatedOn = DateTimeOffset.UtcNow,
            EditedAt = DateTimeOffset.UtcNow,
            UpdatedPayloadJson = "{\"content\":\"edited\"}",
        });

        await bus.Publish<ChannelChanged>(new
        {
            ChannelId = ChannelId,
            GuildId = GuildId,
            CurrentState = "Active",
            UpdatedOn = DateTimeOffset.UtcNow,
            Name = "general",
            Topic = (string?)null,
            ChannelType = 0,
            ParentId = (long?)null,
            IsPresent = true,
        });

        await bus.Publish<GuildChanged>(new
        {
            GuildId = GuildId,
            CurrentState = "Synced",
            UpdatedOn = DateTimeOffset.UtcNow,
            Name = "TestGuild",
            Roles = Array.Empty<DiscordScraper.Contracts.Events.Guild.GuildRole>(),
            IsPresent = true,
        });
    }

    private static object BuildMessageCapturedAnon() => new
    {
        MessageId = DiscordScraper.Contracts.DeterministicGuid.FromSnowflake(MsgSnowflake),
        MessageSnowflake = MsgSnowflake,
        ChannelId = ChannelId,
        GuildId = GuildId,
        AuthorId = 9_000_000_000_000_001L,
        CurrentState = "Captured",
        UpdatedOn = DateTimeOffset.UtcNow,
        PayloadJson = "{\"content\":\"hello\"}",
        AuthorIsBot = false,
        HomeChannelName = (string?)null,
    };

    private static IReadOnlyList<CaptureEnvelope> ParseEnvelopes(IEnumerable<string> lines)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return lines
            .Select(l => JsonSerializer.Deserialize<CaptureEnvelope>(l, opts)!)
            .ToList();
    }
}
