using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DiscordScraper.Contracts.Configuration;
using DiscordScraper.Contracts.IR;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.Enrichment.Consumers;
using DiscordScraper.Enrichment.Ollama;
using DiscordScraper.Enrichment.Ollama.Options;
using DiscordScraper.Write.Consumers;
using DiscordScraper.Write.Parsing;
using DiscordScraper.Write.Repositories;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DiscordScraper.E2E.Tests.Consumers;

/// <summary>
/// Integration tests that drive AnalyzeMessageConsumer, TagConsumer, and ProjectMessageConsumer
/// through MassTransit's InMemory test harness using IRequestClient — the same correlation
/// path the saga uses.
///
/// Purpose: distinguish "response routing broken" from "consumer genuinely slow".
/// If these tests time out, response correlation is broken at the MT level.
/// If they pass quickly, the saga's RequestTimeoutExpired faults are config/wiring issues.
///
/// Ollama requirement: localhost:11434 must be reachable with nomic-embed-text loaded.
/// The fixture verifies this before running Tag and fails loudly if Ollama is absent.
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class ConsumerRoutingTests
{
    private const string OllamaBaseUrl = "http://localhost:11434";
    private const string EmbedModel = "nomic-embed-text";
    private const int Iterations = 5;

    // Realistic Discord message JSON for payload-dependent consumers.
    private const string SamplePayloadJson = """
        {
          "id": "1234567890123456789",
          "content": "This is a substantive technical discussion about architecture patterns.",
          "author": { "id": "987654321", "username": "testuser", "bot": false },
          "mentions": [],
          "attachments": [],
          "embeds": []
        }
        """;

    // -------------------------------------------------------------------------
    // Ollama availability check — shared across Tag tests
    // -------------------------------------------------------------------------

    private static async Task AssertOllamaReachableAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var response = await http.GetAsync($"{OllamaBaseUrl}/api/tags");
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>();
            var hasModel = payload?.Models?.Any(m =>
                m.Name.StartsWith(EmbedModel, StringComparison.OrdinalIgnoreCase)) ?? false;

            if (!hasModel)
                Assert.Fail(
                    $"Ollama is reachable at {OllamaBaseUrl} but '{EmbedModel}' is not loaded. " +
                    $"Available models: {string.Join(", ", payload?.Models?.Select(m => m.Name) ?? [])}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Assert.Fail(
                $"Ollama unreachable at {OllamaBaseUrl}: {ex.Message}. " +
                "Start Ollama and ensure nomic-embed-text is pulled before running this test.");
        }
    }

    private sealed record OllamaTagsResponse(
        [property: JsonPropertyName("models")] List<OllamaModel>? Models);

    private sealed record OllamaModel(
        [property: JsonPropertyName("name")] string Name);

    // -------------------------------------------------------------------------
    // Analyze — no I/O, pure CPU
    // -------------------------------------------------------------------------

    [Test]
    public async Task Analyze_RequestClientReceivesResponse_WithinTimeout()
    {
        await using var provider = BuildAnalyzeProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var client = harness.GetRequestClient<AnalyzeMessageRequest>();
            var latencies = new List<long>(Iterations);

            for (var i = 0; i < Iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                var result = await client.GetResponse<AnalyzeMessageResponse>(new AnalyzeMessageRequest
                {
                    MessageSnowflake = 100L + i,
                    PayloadJson = SamplePayloadJson,
                    AuthorIsBot = false,
                });
                sw.Stop();
                latencies.Add(sw.ElapsedMilliseconds);

                result.Message.ShouldNotBeNull("MT response envelope must not be null");
                result.Message.IsBot.ShouldBeFalse("human message payload — IsBot must be false");
                result.Message.IsSubstantive.ShouldBeTrue("non-trivial content — IsSubstantive must be true");
                result.Message.DetectedLanguage.ShouldBe("en");
            }

            ReportLatencies("AnalyzeMessageConsumer", latencies);

            var maxMs = latencies.Max();
            maxMs.ShouldBeLessThan(30_000,
                $"Analyze max latency {maxMs}ms exceeded 30s saga timeout — routing or executor issue");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Test]
    public async Task Analyze_BotPayload_ResponseReflectsBotFlag()
    {
        await using var provider = BuildAnalyzeProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var client = harness.GetRequestClient<AnalyzeMessageRequest>();
            var result = await client.GetResponse<AnalyzeMessageResponse>(new AnalyzeMessageRequest
            {
                MessageSnowflake = 999L,
                PayloadJson = SamplePayloadJson,
                AuthorIsBot = true,
            });

            result.Message.IsBot.ShouldBeTrue("AuthorIsBot=true must propagate to response");
            result.Message.IsSubstantive.ShouldBeFalse("bot message is never substantive");
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static ServiceProvider BuildAnalyzeProvider()
    {
        return new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<AnalyzeMessageConsumer, AnalyzeMessageConsumerDefinition>();
            })
            .BuildServiceProvider(true);
    }

    // -------------------------------------------------------------------------
    // Tag — real Ollama embed call
    // -------------------------------------------------------------------------

    [Test]
    public async Task Tag_RequestClientReceivesEmbeddingResponse_WithinTimeout()
    {
        await AssertOllamaReachableAsync();

        await using var provider = BuildTagProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var client = harness.GetRequestClient<TagMessageRequest>();
            var latencies = new List<long>(Iterations);

            for (var i = 0; i < Iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                var result = await client.GetResponse<TagMessageResponse>(new TagMessageRequest
                {
                    MessageSnowflake = 200L + i,
                    PlainText = $"Integration test message number {i} for embedding latency measurement",
                });
                sw.Stop();
                latencies.Add(sw.ElapsedMilliseconds);

                result.Message.ShouldNotBeNull();
                result.Message.Embedding.Count.ShouldBe(768,
                    $"nomic-embed-text must return 768-dim vector, got {result.Message.Embedding.Count}");
                result.Message.EmbeddingModelVersion.ShouldNotBeNullOrEmpty();
                result.Message.GeneratedAt.ShouldNotBeNull();

                // Sanity: non-zero vector (all-zero would indicate a silent failure from Ollama)
                result.Message.Embedding.Any(f => f != 0f).ShouldBeTrue(
                    "Embedding vector must not be all-zeros — check Ollama returned a real vector");
            }

            ReportLatencies("TagConsumer (nomic-embed-text, real Ollama)", latencies);

            var maxMs = latencies.Max();
            maxMs.ShouldBeLessThan(30_000,
                $"Tag max latency {maxMs}ms exceeded 30s saga timeout. " +
                "If this is consistently > 5s, raise EnrichmentTagOptions.RequestTimeout.");
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static ServiceProvider BuildTagProvider()
    {
        var tagOptions = new OllamaTagOptions
        {
            BaseUrl = OllamaBaseUrl,
            Model = EmbedModel,
            VectorSize = 768,
        };

        var enrichmentTagOptions = new EnrichmentTagOptions
        {
            // 60s HTTP timeout for the real Ollama call — intentionally generous to
            // separate "slow" from "broken". The test's own 30s assertion is the SLA gate.
            HttpTimeout = TimeSpan.FromSeconds(60),
            ConcurrentMessageLimit = 1,
            PrefetchCount = 1,
        };

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information))
            .AddSingleton<IOptions<OllamaTagOptions>>(Options.Create(tagOptions))
            .AddSingleton<IOptions<EnrichmentTagOptions>>(Options.Create(enrichmentTagOptions))
            .AddSingleton<IOptions<EnrichmentClassifyOptions>>(
                Options.Create(new EnrichmentClassifyOptions()))
            .AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>((sp, http) =>
            {
                http.BaseAddress = new Uri(OllamaBaseUrl);
                http.Timeout = TimeSpan.FromSeconds(60);
            })
            .Services
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<TagConsumer, TagConsumerDefinition>();
            });

        return services.BuildServiceProvider(true);
    }

    // -------------------------------------------------------------------------
    // Project — requires IMessageParser, IChannelNameRepo, IGuildRoleNameRepo
    //           Mongo repos are stubbed (return empty maps); parser is the real impl.
    // -------------------------------------------------------------------------

    [Test]
    public async Task Project_RequestClientReceivesIrResponse_WithinTimeout()
    {
        await using var provider = BuildProjectProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var client = harness.GetRequestClient<ProjectMessageRequest>();
            var latencies = new List<long>(Iterations);

            for (var i = 0; i < Iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                var result = await client.GetResponse<ProjectMessageResponse>(new ProjectMessageRequest
                {
                    MessageSnowflake = 300L + i,
                    ChannelId = 111111111L,
                    GuildId = 222222222L,
                    PayloadJson = SamplePayloadJson,
                });
                sw.Stop();
                latencies.Add(sw.ElapsedMilliseconds);

                result.Message.ShouldNotBeNull();
                result.Message.IR.ShouldNotBeNull("ProjectMessageResponse must carry a non-null IR");
                // Body may be empty (content scanner produced nodes or not) but the IR itself must exist.
                result.Message.IR.Body.ShouldNotBeNull();
                result.Message.IR.CapturedAt.ShouldNotBe(default(DateTimeOffset));
            }

            ReportLatencies("ProjectMessageConsumer (stubbed Mongo repos)", latencies);

            var maxMs = latencies.Max();
            maxMs.ShouldBeLessThan(30_000,
                $"Project max latency {maxMs}ms exceeded 30s — check repo stub latency or routing issue");
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static ServiceProvider BuildProjectProvider()
    {
        // IChannelNameRepo and IGuildRoleNameRepo are the only Mongo-bound dependencies.
        // Stub them to return empty dictionaries — the consumer handles missing entries by
        // leaving fallback strings null, which is the same behavior as an empty Mongo collection.
        var channelRepo = Substitute.For<IChannelNameRepo>();
        channelRepo
            .GetNamesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string>());

        var roleRepo = Substitute.For<IGuildRoleNameRepo>();
        roleRepo
            .GetRoleNamesAsync(
                Arg.Any<long>(),
                Arg.Any<IReadOnlyCollection<long>>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string>());

        return new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information))
            .AddSingleton<IMessageParser, MessageParser>()
            .AddSingleton(channelRepo)
            .AddSingleton(roleRepo)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<ProjectMessageConsumer, ProjectMessageConsumerDefinition>();
            })
            .BuildServiceProvider(true);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void ReportLatencies(string label, IReadOnlyList<long> latencies)
    {
        var min = latencies.Min();
        var max = latencies.Max();
        var avg = (long)latencies.Average();

        TestContext.Out.WriteLine(
            $"[LATENCY] {label} — n={latencies.Count} min={min}ms avg={avg}ms max={max}ms");
        TestContext.Out.WriteLine(
            $"  individual: {string.Join(", ", latencies.Select(ms => $"{ms}ms"))}");
    }
}
