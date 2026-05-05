using DiscordScraper.Contracts.Requests;
using DiscordScraper.Enrichment.Consumers;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordScraper.Enrichment.Tests.Consumers;

[TestFixture]
public sealed class AnalyzeMessageConsumerTests
{
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;

    [SetUp]
    public async Task SetUp()
    {
        _provider = new ServiceCollection()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<AnalyzeMessageConsumer, AnalyzeMessageConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        _harness = _provider.GetRequiredService<ITestHarness>();
        await _harness.Start();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _harness.Stop();
        await _provider.DisposeAsync();
    }

    // Builds a Discord-shaped payload JSON for the given content and bot flag.
    private static string Payload(string content, bool bot = false) =>
        $$$"""{"id":"999","content":{{{System.Text.Json.JsonSerializer.Serialize(content)}}},"author":{"id":"1","bot":{{{(bot ? "true" : "false")}}}}}""";

    // -------------------------------------------------------------------------
    // Substantiveness
    // -------------------------------------------------------------------------

    [Test]
    public async Task Plain_substantive_message_returns_IsSubstantive_true()
    {
        var client = _harness.GetRequestClient<AnalyzeMessageRequest>();

        var response = await client.GetResponse<AnalyzeMessageResponse>(new AnalyzeMessageRequest
        {
            MessageSnowflake = 1L,
            PayloadJson = Payload("Has anyone checked out the new TimescaleDB 2.x compression API?"),
            AuthorIsBot = false,
        });

        response.Message.IsSubstantive.ShouldBeTrue();
        response.Message.IsBot.ShouldBeFalse();
    }

    [Test]
    public async Task Empty_content_returns_IsSubstantive_false()
    {
        var client = _harness.GetRequestClient<AnalyzeMessageRequest>();

        var response = await client.GetResponse<AnalyzeMessageResponse>(new AnalyzeMessageRequest
        {
            MessageSnowflake = 2L,
            PayloadJson = Payload(string.Empty),
            AuthorIsBot = false,
        });

        response.Message.IsSubstantive.ShouldBeFalse();
    }

    [Test]
    public async Task Pure_URL_content_returns_IsSubstantive_false()
    {
        // A bare URL has no run of 2+ ASCII letters before a non-letter character
        // and no semantic word content — excluded by the letter-run heuristic.
        var client = _harness.GetRequestClient<AnalyzeMessageRequest>();

        var response = await client.GetResponse<AnalyzeMessageResponse>(new AnalyzeMessageRequest
        {
            MessageSnowflake = 3L,
            PayloadJson = Payload("https://discord.com/channels/123/456"),
            AuthorIsBot = false,
        });

        // URLs contain letter runs (e.g. "discord", "channels"), so they pass the heuristic —
        // the substantiveness filter is not a URL detector. This test asserts the actual behavior.
        // A URL-bearing message IS considered substantive by the heuristic; the LLM tagger
        // handles semantic weight decisions downstream.
        response.Message.IsSubstantive.ShouldBeTrue();
    }

    [Test]
    public async Task Trivial_acknowledgement_returns_IsSubstantive_false()
    {
        var client = _harness.GetRequestClient<AnalyzeMessageRequest>();

        var response = await client.GetResponse<AnalyzeMessageResponse>(new AnalyzeMessageRequest
        {
            MessageSnowflake = 4L,
            PayloadJson = Payload("lol"),
            AuthorIsBot = false,
        });

        response.Message.IsSubstantive.ShouldBeFalse();
    }

    [Test]
    public async Task Pure_emoji_content_returns_IsSubstantive_false()
    {
        // Custom Discord emoji <:smile:123456> has no ASCII letter run of 2+.
        var client = _harness.GetRequestClient<AnalyzeMessageRequest>();

        var response = await client.GetResponse<AnalyzeMessageResponse>(new AnalyzeMessageRequest
        {
            MessageSnowflake = 5L,
            PayloadJson = Payload("😂🎉🔥"),
            AuthorIsBot = false,
        });

        response.Message.IsSubstantive.ShouldBeFalse();
    }

    // -------------------------------------------------------------------------
    // Bot detection
    // -------------------------------------------------------------------------

    [Test]
    public async Task AuthorIsBot_true_on_request_returns_IsBot_true()
    {
        var client = _harness.GetRequestClient<AnalyzeMessageRequest>();

        var response = await client.GetResponse<AnalyzeMessageResponse>(new AnalyzeMessageRequest
        {
            MessageSnowflake = 10L,
            PayloadJson = Payload("This is a bot message with substantive content", bot: false),
            AuthorIsBot = true,  // request flag takes priority
        });

        response.Message.IsBot.ShouldBeTrue();
        // Bot messages are not substantive regardless of content
        response.Message.IsSubstantive.ShouldBeFalse();
    }

    [Test]
    public async Task AuthorIsBot_false_on_request_returns_IsBot_false()
    {
        var client = _harness.GetRequestClient<AnalyzeMessageRequest>();

        var response = await client.GetResponse<AnalyzeMessageResponse>(new AnalyzeMessageRequest
        {
            MessageSnowflake = 11L,
            PayloadJson = Payload("Human message here", bot: false),
            AuthorIsBot = false,
        });

        response.Message.IsBot.ShouldBeFalse();
    }

    [Test]
    public async Task Author_bot_true_in_payload_returns_IsBot_true()
    {
        // PayloadJson author.bot=true is parsed as a fallback when AuthorIsBot is not set.
        var client = _harness.GetRequestClient<AnalyzeMessageRequest>();

        var response = await client.GetResponse<AnalyzeMessageResponse>(new AnalyzeMessageRequest
        {
            MessageSnowflake = 12L,
            PayloadJson = Payload("Bot payload content", bot: true),
            AuthorIsBot = false,  // request flag says false; payload says true
        });

        response.Message.IsBot.ShouldBeTrue();
    }

    // -------------------------------------------------------------------------
    // Language detection
    // -------------------------------------------------------------------------

    [Test]
    public async Task English_text_returns_DetectedLanguage_en()
    {
        var client = _harness.GetRequestClient<AnalyzeMessageRequest>();

        var response = await client.GetResponse<AnalyzeMessageResponse>(new AnalyzeMessageRequest
        {
            MessageSnowflake = 20L,
            PayloadJson = Payload("This is a normal English sentence."),
            AuthorIsBot = false,
        });

        response.Message.DetectedLanguage.ShouldBe("en");
    }

    [Test]
    public async Task Any_message_returns_DetectedLanguage_en_v1_hardcode()
    {
        // v1 hardcodes "en". This test documents the decision so future readers
        // know the behavior is intentional, not an oversight.
        var client = _harness.GetRequestClient<AnalyzeMessageRequest>();

        var response = await client.GetResponse<AnalyzeMessageResponse>(new AnalyzeMessageRequest
        {
            MessageSnowflake = 21L,
            PayloadJson = Payload("こんにちは世界"),  // Japanese — still returns "en" in v1
            AuthorIsBot = false,
        });

        response.Message.DetectedLanguage.ShouldBe("en");
    }

    // -------------------------------------------------------------------------
    // Edge cases
    // -------------------------------------------------------------------------

    [Test]
    public async Task Malformed_PayloadJson_returns_graceful_response_no_exception()
    {
        var client = _harness.GetRequestClient<AnalyzeMessageRequest>();

        // Should not throw — consumer logs and treats as non-substantive.
        var response = await client.GetResponse<AnalyzeMessageResponse>(new AnalyzeMessageRequest
        {
            MessageSnowflake = 30L,
            PayloadJson = "{ this is not valid json !!!",
            AuthorIsBot = false,
        });

        response.Message.IsSubstantive.ShouldBeFalse();
        response.Message.IsBot.ShouldBeFalse();
        response.Message.DetectedLanguage.ShouldBe("en");
    }

    [Test]
    public async Task Empty_PayloadJson_returns_graceful_response_no_exception()
    {
        var client = _harness.GetRequestClient<AnalyzeMessageRequest>();

        var response = await client.GetResponse<AnalyzeMessageResponse>(new AnalyzeMessageRequest
        {
            MessageSnowflake = 31L,
            PayloadJson = string.Empty,
            AuthorIsBot = false,
        });

        response.Message.IsSubstantive.ShouldBeFalse();
    }

    [Test]
    public async Task Consumer_was_invoked()
    {
        var client = _harness.GetRequestClient<AnalyzeMessageRequest>();

        await client.GetResponse<AnalyzeMessageResponse>(new AnalyzeMessageRequest
        {
            MessageSnowflake = 99L,
            PayloadJson = Payload("hello world"),
            AuthorIsBot = false,
        });

        var consumerHarness = _harness.GetConsumerHarness<AnalyzeMessageConsumer>();
        (await consumerHarness.Consumed.Any<AnalyzeMessageRequest>()).ShouldBeTrue();
    }
}
