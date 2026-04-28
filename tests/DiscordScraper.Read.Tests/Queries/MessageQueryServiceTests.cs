using DiscordScraper.Contracts.IR;
using DiscordScraper.Core.Graph;
using DiscordScraper.Core.Queries;
using DiscordScraper.Core.Search;
using DiscordScraper.Core.Vector;
using DiscordScraper.Read.Data;
using DiscordScraper.Read.Data.Entities;
using DiscordScraper.Read.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DiscordScraper.Read.Tests.Queries;

[TestFixture]
public sealed class MessageQueryServiceTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private IVectorStore _vectorStore = null!;
    private ISearchService _searchService = null!;
    private IConversationGraph _conversationGraph = null!;
    private IDbContextFactory<ReadDbContext> _dbFactory = null!;

    private sealed class FakeReadDbContextFactory(DbContextOptions<ReadDbContext> opts)
        : IDbContextFactory<ReadDbContext>
    {
        public ReadDbContext CreateDbContext() => new(opts);

        public ValueTask<ReadDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new ReadDbContext(opts));
    }

    private IDbContextFactory<ReadDbContext> BuildFactory(string? dbName = null)
    {
        var opts = new DbContextOptionsBuilder<ReadDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new FakeReadDbContextFactory(opts);
    }

    private static IOptions<MessageQueryServiceOptions> DefaultOptions() =>
        Options.Create(new MessageQueryServiceOptions());

    private static IOptions<MessageQueryServiceOptions> CustomOptions(float lexical, float semantic) =>
        Options.Create(new MessageQueryServiceOptions { LexicalWeight = lexical, SemanticWeight = semantic });

    private MessageQueryService BuildService(
        IDbContextFactory<ReadDbContext>? factory = null,
        IOptions<MessageQueryServiceOptions>? opts = null) =>
        new(
            _vectorStore,
            _searchService,
            _conversationGraph,
            factory ?? _dbFactory,
            opts ?? DefaultOptions(),
            NullLogger<MessageQueryService>.Instance);

    private static MessageIR SimpleIr() =>
        new(Body: [new TextNode("hello")], Attachments: [], Embeds: [], ReplyTo: null, CapturedAt: Now);

    private static ReadMessage MakeMessage(long id, long channelId = 1L, long guildId = 2L, long authorId = 3L) =>
        new()
        {
            MessageId  = id,
            ChannelId  = channelId,
            GuildId    = guildId,
            AuthorId   = authorId,
            CreatedAt  = Now,
            Ir         = SimpleIr(),
            PlainText  = "hello"
        };

    private static SearchResult EmptySearchResult() =>
        new(Hits: [], TotalEstimate: 0);

    private static SearchResult SearchResultWith(params (long id, float rank, string snippet)[] hits) =>
        new(Hits: hits.Select(h => new SearchHit(
            MessageId: h.id, ChannelId: 1L, GuildId: 2L, AuthorId: 3L, CreatedAt: Now,
            Rank: h.rank, Snippet: h.snippet)).ToList(),
            TotalEstimate: hits.Length);

    private static IReadOnlyList<VectorMatch> VectorMatches(params (long id, float score)[] matches) =>
        matches.Select(m => new VectorMatch(
            MessageId: m.id, Score: m.score, ChannelId: 1L, GuildId: 2L, AuthorId: 3L,
            CreatedAt: Now, Tags: [])).ToList();

    [SetUp]
    public void SetUp()
    {
        _vectorStore       = Substitute.For<IVectorStore>();
        _searchService     = Substitute.For<ISearchService>();
        _conversationGraph = Substitute.For<IConversationGraph>();
        _dbFactory         = BuildFactory();
    }

    // -------------------------------------------------------------------------
    // SearchAsync — signal routing
    // -------------------------------------------------------------------------

    [Test]
    public async Task SearchAsync_NeitherTextNorEmbedding_ReturnsEmpty()
    {
        var svc   = BuildService();
        var query = new MessageSearchQuery(
            TextQuery: null, Embedding: null, GuildId: null, ChannelId: null,
            AuthorId: null, After: null, Before: null, AnyTags: null,
            TopK: 10, OutputFormat: RenderFormat.PlainText);

        var result = await svc.SearchAsync(query, CancellationToken.None);

        result.ShouldBeEmpty();
        await _searchService.DidNotReceive().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
        await _vectorStore.DidNotReceive().SearchAsync(
            Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<VectorFilter>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SearchAsync_TextQueryOnly_OnlyLexicalCalled_SemanticScoreZero()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(1L));
            await db.SaveChangesAsync();
        }

        _searchService
            .SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(SearchResultWith((1L, 0.9f, "snippet")));

        var svc = BuildService(factory);
        var query = new MessageSearchQuery(
            TextQuery: "hello", Embedding: null, GuildId: null, ChannelId: null,
            AuthorId: null, After: null, Before: null, AnyTags: null,
            TopK: 10, OutputFormat: RenderFormat.PlainText);

        var results = await svc.SearchAsync(query, CancellationToken.None);

        await _vectorStore.DidNotReceive().SearchAsync(
            Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<VectorFilter>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        results.Count.ShouldBe(1);
        results[0].SemanticScore.ShouldBe(0f);
        results[0].LexicalScore.ShouldBe(0.9f);
        // CombinedScore = lexicalScore only when text-only
        results[0].CombinedScore.ShouldBe(0.9f);
    }

    [Test]
    public async Task SearchAsync_EmbeddingOnly_OnlySemanticCalled_LexicalScoreZero()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(2L));
            await db.SaveChangesAsync();
        }

        _vectorStore
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<VectorFilter>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(VectorMatches((2L, 0.85f)));

        var svc = BuildService(factory);
        var embedding = new ReadOnlyMemory<float>(new float[] { 0.1f, 0.2f });
        var query = new MessageSearchQuery(
            TextQuery: null, Embedding: embedding, GuildId: null, ChannelId: null,
            AuthorId: null, After: null, Before: null, AnyTags: null,
            TopK: 10, OutputFormat: RenderFormat.PlainText);

        var results = await svc.SearchAsync(query, CancellationToken.None);

        await _searchService.DidNotReceive().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());

        results.Count.ShouldBe(1);
        results[0].LexicalScore.ShouldBe(0f);
        results[0].SemanticScore.ShouldBe(0.85f);
        results[0].CombinedScore.ShouldBe(0.85f);
    }

    [Test]
    public async Task SearchAsync_BothSignals_BothServicesCalledAndResultsMerged()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(10L));
            db.ReadMessages.Add(MakeMessage(20L));
            await db.SaveChangesAsync();
        }

        _searchService
            .SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(SearchResultWith((10L, 0.8f, "lex snippet")));

        _vectorStore
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<VectorFilter>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(VectorMatches((20L, 0.9f)));

        var svc = BuildService(factory);
        var embedding = new ReadOnlyMemory<float>(new float[] { 0.1f });
        var query = new MessageSearchQuery(
            TextQuery: "hello", Embedding: embedding, GuildId: null, ChannelId: null,
            AuthorId: null, After: null, Before: null, AnyTags: null,
            TopK: 10, OutputFormat: RenderFormat.PlainText);

        var results = await svc.SearchAsync(query, CancellationToken.None);

        await _searchService.Received(1).SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
        await _vectorStore.Received(1).SearchAsync(
            Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<VectorFilter>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        results.Count.ShouldBe(2);
        // Union: message 10 is lexical-only, message 20 is semantic-only
        var msg10 = results.First(r => r.Message.MessageId == 10L);
        var msg20 = results.First(r => r.Message.MessageId == 20L);
        msg10.LexicalScore.ShouldBe(0.8f);
        msg10.SemanticScore.ShouldBe(0f);
        msg20.SemanticScore.ShouldBe(0.9f);
        msg20.LexicalScore.ShouldBe(0f);
    }

    [Test]
    public async Task SearchAsync_BothSignals_CombinedScoreBlendedWithDefaultWeights()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(5L));
            await db.SaveChangesAsync();
        }

        _searchService
            .SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(SearchResultWith((5L, 0.6f, "")));

        _vectorStore
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<VectorFilter>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(VectorMatches((5L, 0.4f)));

        var svc = BuildService(factory, DefaultOptions()); // default 0.5/0.5
        var embedding = new ReadOnlyMemory<float>(new float[] { 0.1f });
        var query = new MessageSearchQuery(
            TextQuery: "hello", Embedding: embedding, GuildId: null, ChannelId: null,
            AuthorId: null, After: null, Before: null, AnyTags: null,
            TopK: 10, OutputFormat: RenderFormat.PlainText);

        var results = await svc.SearchAsync(query, CancellationToken.None);

        results.Count.ShouldBe(1);
        // 0.5 * 0.6 + 0.5 * 0.4 = 0.5
        results[0].CombinedScore.ShouldBe(0.5f, tolerance: 0.0001f);
    }

    [Test]
    public async Task SearchAsync_BothSignals_CustomWeights_CombinedScoreReflectsWeights()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(7L));
            await db.SaveChangesAsync();
        }

        _searchService
            .SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(SearchResultWith((7L, 1.0f, "")));

        _vectorStore
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<VectorFilter>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(VectorMatches((7L, 1.0f)));

        var svc = BuildService(factory, CustomOptions(lexical: 0.3f, semantic: 0.7f));
        var embedding = new ReadOnlyMemory<float>(new float[] { 0.1f });
        var query = new MessageSearchQuery(
            TextQuery: "hello", Embedding: embedding, GuildId: null, ChannelId: null,
            AuthorId: null, After: null, Before: null, AnyTags: null,
            TopK: 10, OutputFormat: RenderFormat.PlainText);

        var results = await svc.SearchAsync(query, CancellationToken.None);

        results.Count.ShouldBe(1);
        // 0.3 * 1.0 + 0.7 * 1.0 = 1.0
        results[0].CombinedScore.ShouldBe(1.0f, tolerance: 0.0001f);
    }

    [Test]
    public async Task SearchAsync_ResultsOrderedByCombinedScoreDescending()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(100L));
            db.ReadMessages.Add(MakeMessage(200L));
            db.ReadMessages.Add(MakeMessage(300L));
            await db.SaveChangesAsync();
        }

        _vectorStore
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<VectorFilter>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(VectorMatches((100L, 0.3f), (200L, 0.9f), (300L, 0.6f)));

        var svc = BuildService(factory);
        var embedding = new ReadOnlyMemory<float>(new float[] { 0.1f });
        var query = new MessageSearchQuery(
            TextQuery: null, Embedding: embedding, GuildId: null, ChannelId: null,
            AuthorId: null, After: null, Before: null, AnyTags: null,
            TopK: 10, OutputFormat: RenderFormat.PlainText);

        var results = await svc.SearchAsync(query, CancellationToken.None);

        results.Count.ShouldBe(3);
        results[0].Message.MessageId.ShouldBe(200L);
        results[1].Message.MessageId.ShouldBe(300L);
        results[2].Message.MessageId.ShouldBe(100L);
    }

    [Test]
    public async Task SearchAsync_SnippetPopulatedFromLexicalHit()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(50L));
            await db.SaveChangesAsync();
        }

        _searchService
            .SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(SearchResultWith((50L, 0.5f, "the quick brown fox")));

        var svc = BuildService(factory);
        var query = new MessageSearchQuery(
            TextQuery: "fox", Embedding: null, GuildId: null, ChannelId: null,
            AuthorId: null, After: null, Before: null, AnyTags: null,
            TopK: 10, OutputFormat: RenderFormat.PlainText);

        var results = await svc.SearchAsync(query, CancellationToken.None);

        results[0].Snippet.ShouldBe("the quick brown fox");
    }

    [Test]
    public async Task SearchAsync_CancellationToken_ThreadedToSearchService()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        _searchService
            .SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(EmptySearchResult());

        var svc = BuildService();
        var query = new MessageSearchQuery(
            TextQuery: "hello", Embedding: null, GuildId: null, ChannelId: null,
            AuthorId: null, After: null, Before: null, AnyTags: null,
            TopK: 10, OutputFormat: RenderFormat.PlainText);

        await svc.SearchAsync(query, token);

        await _searchService.Received(1).SearchAsync(Arg.Any<SearchQuery>(), token);
    }

    // -------------------------------------------------------------------------
    // GetMessageAsync
    // -------------------------------------------------------------------------

    [Test]
    public async Task GetMessageAsync_ExistingId_ReturnsRenderedMessage()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(1001L, channelId: 77L));
            db.ReadChannels.Add(new ReadChannel
            {
                ChannelId = 77L, GuildId = 2L, Name = "test-channel", UpdatedAt = Now
            });
            await db.SaveChangesAsync();
        }

        var svc    = BuildService(factory);
        var result = await svc.GetMessageAsync(1001L, RenderFormat.PlainText, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.MessageId.ShouldBe(1001L);
        result.ChannelId.ShouldBe(77L);
        result.ChannelName.ShouldBe("test-channel");
        result.Body.ShouldBe("hello");
    }

    [Test]
    public async Task GetMessageAsync_MissingId_ReturnsNull()
    {
        var svc    = BuildService();
        var result = await svc.GetMessageAsync(9999L, RenderFormat.PlainText, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetMessageAsync_WithTags_TagsPopulated()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(2002L));
            db.MessageTags.AddRange(
                new MessageTag { MessageId = 2002L, Tag = "dotnet" },
                new MessageTag { MessageId = 2002L, Tag = "csharp" });
            await db.SaveChangesAsync();
        }

        var svc    = BuildService(factory);
        var result = await svc.GetMessageAsync(2002L, RenderFormat.PlainText, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Tags.ShouldContain("dotnet");
        result.Tags.ShouldContain("csharp");
    }

    [Test]
    public async Task GetMessageAsync_NoChannelInDb_ChannelNameFallsBackToSnowflake()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(3003L, channelId: 88L));
            // deliberately no ReadChannel for 88L
            await db.SaveChangesAsync();
        }

        var svc    = BuildService(factory);
        var result = await svc.GetMessageAsync(3003L, RenderFormat.PlainText, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.ChannelName.ShouldBe("88");
    }

    // -------------------------------------------------------------------------
    // GetConversationContextAsync
    // -------------------------------------------------------------------------

    [Test]
    public async Task GetConversationContextAsync_PopulatesCenterAndRelatives()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(500L)); // center
            db.ReadMessages.Add(MakeMessage(501L)); // ancestor
            db.ReadMessages.Add(MakeMessage(502L)); // descendant
            await db.SaveChangesAsync();
        }

        var center     = new ConversationNode(500L, null,  1L, 2L, 3L, Now, 0);
        var ancestor   = new ConversationNode(501L, null,  1L, 2L, 3L, Now, -1);
        var descendant = new ConversationNode(502L, 500L,  1L, 2L, 3L, Now, 1);

        var cluster = new ConversationCluster(center, [ancestor], [descendant]);
        _conversationGraph
            .ExpandConversationAsync(500L, 3, Arg.Any<CancellationToken>())
            .Returns(cluster);

        var svc    = BuildService(factory);
        var result = await svc.GetConversationContextAsync(500L, 3, RenderFormat.PlainText, CancellationToken.None);

        result.Center.ShouldNotBeNull();
        result.Center!.MessageId.ShouldBe(500L);
        result.Ancestors.Count.ShouldBe(1);
        result.Ancestors[0].MessageId.ShouldBe(501L);
        result.Descendants.Count.ShouldBe(1);
        result.Descendants[0].MessageId.ShouldBe(502L);
    }

    [Test]
    public async Task GetConversationContextAsync_EmptyCluster_EmptyAncestorsAndDescendants()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(600L));
            await db.SaveChangesAsync();
        }

        var center  = new ConversationNode(600L, null, 1L, 2L, 3L, Now, 0);
        var cluster = new ConversationCluster(center, [], []);
        _conversationGraph
            .ExpandConversationAsync(600L, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(cluster);

        var svc    = BuildService(factory);
        var result = await svc.GetConversationContextAsync(600L, 1, RenderFormat.PlainText, CancellationToken.None);

        result.Center.ShouldNotBeNull();
        result.Center!.MessageId.ShouldBe(600L);
        result.Ancestors.ShouldBeEmpty();
        result.Descendants.ShouldBeEmpty();
    }

    [Test]
    public async Task GetConversationContextAsync_NodeIdNotInDb_FallbackRenderedMessage()
    {
        // ConversationGraph can return a node that has no corresponding ReadMessage yet.
        // The service should return a fallback RenderedMessage rather than throw.
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(700L)); // center exists
            // 701 (descendant) deliberately absent
            await db.SaveChangesAsync();
        }

        var center     = new ConversationNode(700L, null, 1L, 2L, 3L, Now, 0);
        var descendant = new ConversationNode(701L, 700L, 1L, 2L, 3L, Now, 1);
        var cluster    = new ConversationCluster(center, [], [descendant]);
        _conversationGraph
            .ExpandConversationAsync(700L, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(cluster);

        var svc    = BuildService(factory);
        var result = await svc.GetConversationContextAsync(700L, 1, RenderFormat.PlainText, CancellationToken.None);

        result.Center.ShouldNotBeNull();
        result.Center!.MessageId.ShouldBe(700L);
        result.Descendants.Count.ShouldBe(1);
        // Fallback message has empty body
        result.Descendants[0].MessageId.ShouldBe(701L);
        result.Descendants[0].Body.ShouldBe(string.Empty);
    }

    [Test]
    public async Task GetConversationContextAsync_CancellationToken_ThreadedToConversationGraph()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        var center  = new ConversationNode(800L, null, 1L, 2L, 3L, Now, 0);
        var cluster = new ConversationCluster(center, [], []);
        _conversationGraph
            .ExpandConversationAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(cluster);

        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(800L));
            await db.SaveChangesAsync();
        }

        var svc = BuildService(factory);
        await svc.GetConversationContextAsync(800L, 1, RenderFormat.PlainText, token);

        await _conversationGraph.Received(1).ExpandConversationAsync(800L, 1, token);
    }

    [Test]
    public async Task GetConversationContextAsync_MissingCenter_ReturnsNullCenterWithEmptyLists()
    {
        // ExpandConversationAsync returns null Center when the message is not in read_messages.
        var cluster = new ConversationCluster(null, [], []);
        _conversationGraph
            .ExpandConversationAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(cluster);

        var svc    = BuildService();
        var result = await svc.GetConversationContextAsync(42L, 2, RenderFormat.PlainText, CancellationToken.None);

        result.Center.ShouldBeNull();
        result.Ancestors.ShouldBeEmpty();
        result.Descendants.ShouldBeEmpty();
    }
}
