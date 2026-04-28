using System.Collections;
using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.IR;
using DiscordScraper.Read.Consumers;
using DiscordScraper.Read.Data;
using DiscordScraper.Read.Data.Entities;
using DiscordScraper.Read.Mapping;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiscordScraper.Read.Tests.Consumers;

// Castle.Core (NSubstitute) requires T in proxied interfaces from strong-named assemblies to be
// publicly accessible. Concrete test doubles implement the interface directly, bypassing Castle.Core.
public sealed class TestMessageEnriched : MessageEnriched
{
    public Guid MessageId { get; init; } = Guid.NewGuid();
    public long MessageSnowflake { get; init; }
    public long ChannelId { get; init; }
    public long GuildId { get; init; }
    public long AuthorId { get; init; }
    public string CurrentState { get; init; } = "Enriched";
    public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CorrelationId => DeterministicGuid.FromSnowflake(MessageSnowflake);

    public MessageIR IR { get; init; } = new([], [], [], null, DateTimeOffset.UtcNow);
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool IsSubstantive { get; init; } = true;
    public bool IsBot { get; init; }
    public DateTimeOffset MessageCreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EditedTimestamp { get; init; }
}

public sealed class TestMessageProjected : MessageProjected
{
    public Guid MessageId { get; init; } = Guid.NewGuid();
    public long MessageSnowflake { get; init; }
    public long ChannelId { get; init; }
    public long GuildId { get; init; }
    public long AuthorId { get; init; }
    public string CurrentState { get; init; } = "Projected";
    public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CorrelationId => DeterministicGuid.FromSnowflake(MessageSnowflake);
    public MessageIR IR { get; init; } = new([], [], [], null, DateTimeOffset.UtcNow);
}

[TestFixture]
public sealed class MessageReadConsumerTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static IDbContextFactory<ReadDbContext> BuildFactory()
    {
        var opts = new DbContextOptionsBuilder<ReadDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new FakeReadDbContextFactory(opts);
    }

    private sealed class FakeReadDbContextFactory(DbContextOptions<ReadDbContext> opts)
        : IDbContextFactory<ReadDbContext>
    {
        public ReadDbContext CreateDbContext() => new(opts);
        public ValueTask<ReadDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new ReadDbContext(opts));
    }

    private static ConsumeContext<Batch<MessageStateChanged>> BuildBatchContext(
        params MessageStateChanged[] events)
    {
        var batch = new FakeMessageBatch(events);
        var ctx = Substitute.For<ConsumeContext<Batch<MessageStateChanged>>>();
        ctx.Message.Returns(batch);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private sealed class FakeMessageBatch(MessageStateChanged[] events) : Batch<MessageStateChanged>
    {
        private readonly ConsumeContext<MessageStateChanged>[] _messages = events
            .Select(e =>
            {
                var m = Substitute.For<ConsumeContext<MessageStateChanged>>();
                m.Message.Returns(e);
                return m;
            })
            .ToArray();

        public BatchCompletionMode Mode => BatchCompletionMode.Time;
        public DateTime FirstMessageReceived => DateTime.UtcNow;
        public DateTime LastMessageReceived => DateTime.UtcNow;
        public ConsumeContext<MessageStateChanged> this[int i] => _messages[i];
        public int Length => _messages.Length;

        public IEnumerator<ConsumeContext<MessageStateChanged>> GetEnumerator() =>
            ((IEnumerable<ConsumeContext<MessageStateChanged>>)_messages).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static MessageReadConsumer BuildConsumer(IReadBulkWriter writer) =>
        new(BuildFactory(), writer, NullLogger<MessageReadConsumer>.Instance);

    private static MessageIR SimpleIr(params MessageNode[] body) =>
        new(Body: body, Attachments: [], Embeds: [], ReplyTo: null, CapturedAt: DateTimeOffset.UtcNow);

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [Test]
    public async Task SingleEnriched_WriterReceivesOneReadMessage()
    {
        var writer = Substitute.For<IReadBulkWriter>();
        var consumer = BuildConsumer(writer);

        await consumer.Consume(BuildBatchContext(new TestMessageEnriched
        {
            MessageSnowflake = 111_000_000L,
            ChannelId        = 222L,
            GuildId          = 333L,
            AuthorId         = 444L,
            IR               = SimpleIr(new TextNode("hello")),
        }));

        await writer.Received(1).WriteAsync(
            Arg.Any<ReadDbContext>(),
            Arg.Any<IDbContextTransaction>(),
            Arg.Is<IReadOnlyDictionary<Type, IList<object>>>(d =>
                d.ContainsKey(typeof(ReadMessage)) && d[typeof(ReadMessage)].Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SingleEnriched_ReadMessageFieldsCorrect()
    {
        IReadOnlyDictionary<Type, IList<object>>? captured = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer.When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call => captured = call.ArgAt<IReadOnlyDictionary<Type, IList<object>>>(2));

        var consumer = BuildConsumer(writer);
        await consumer.Consume(BuildBatchContext(new TestMessageEnriched
        {
            MessageSnowflake = 111_000_000L,
            ChannelId        = 222L,
            GuildId          = 333L,
            AuthorId         = 444L,
            IR               = SimpleIr(new TextNode("hello")),
        }));

        captured.ShouldNotBeNull();
        captured.ShouldContainKey(typeof(ReadMessage));
        var msg = (ReadMessage)captured![typeof(ReadMessage)][0];
        msg.MessageId.ShouldBe(111_000_000L);
        msg.ChannelId.ShouldBe(222L);
        msg.GuildId.ShouldBe(333L);
        msg.AuthorId.ShouldBe(444L);
    }

    [Test]
    public async Task SingleEnriched_WithAttachmentsAndEmbeds_SubTablesProjected()
    {
        IReadOnlyDictionary<Type, IList<object>>? captured = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer.When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call => captured = call.ArgAt<IReadOnlyDictionary<Type, IList<object>>>(2));

        var ir = new MessageIR(
            Body: [new TextNode("see attachment")],
            Attachments: [new AttachmentIR(99L, "https://cdn.example.com/img.png", "image/png", 2048L, "img.png")],
            Embeds:      [new EmbedIR(0, "link", "https://example.com", "Title", "Desc")],
            ReplyTo:     null,
            CapturedAt:  DateTimeOffset.UtcNow);

        var consumer = BuildConsumer(writer);
        await consumer.Consume(BuildBatchContext(new TestMessageEnriched
        {
            MessageSnowflake = 500L,
            IR               = ir,
        }));

        captured.ShouldNotBeNull();
        captured.ShouldContainKey(typeof(MessageAttachment));
        var att = (MessageAttachment)captured![typeof(MessageAttachment)][0];
        att.AttachmentId.ShouldBe(99L);
        att.Url.ShouldBe("https://cdn.example.com/img.png");
        att.ContentType.ShouldBe("image/png");
        att.SizeBytes.ShouldBe(2048L);

        captured.ShouldContainKey(typeof(MessageEmbed));
        var emb = (MessageEmbed)captured[typeof(MessageEmbed)][0];
        emb.EmbedIndex.ShouldBe((short)0);
        emb.Title.ShouldBe("Title");
        emb.Url.ShouldBe("https://example.com");
    }

    [Test]
    public async Task IntermediateState_MessageProjected_WriterNotCalled()
    {
        var writer = Substitute.For<IReadBulkWriter>();
        var consumer = BuildConsumer(writer);

        await consumer.Consume(BuildBatchContext(new TestMessageProjected { MessageSnowflake = 600L }));

        await writer.DidNotReceive().WriteAsync(
            Arg.Any<ReadDbContext>(),
            Arg.Any<IDbContextTransaction>(),
            Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BatchOf5Enriched_WriterReceives5ReadMessages()
    {
        var writer = Substitute.For<IReadBulkWriter>();
        var consumer = BuildConsumer(writer);

        var events = Enumerable.Range(1, 5)
            .Select(i => (MessageStateChanged)new TestMessageEnriched
            {
                MessageSnowflake = i * 1000L,
                IR               = SimpleIr(new TextNode($"message {i}")),
            })
            .ToArray();

        await consumer.Consume(BuildBatchContext(events));

        await writer.Received(1).WriteAsync(
            Arg.Any<ReadDbContext>(),
            Arg.Any<IDbContextTransaction>(),
            Arg.Is<IReadOnlyDictionary<Type, IList<object>>>(d =>
                d.ContainsKey(typeof(ReadMessage)) && d[typeof(ReadMessage)].Count == 5),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EmptyTags_NoMessageTagEntities()
    {
        var writer = Substitute.For<IReadBulkWriter>();
        var consumer = BuildConsumer(writer);

        await consumer.Consume(BuildBatchContext(new TestMessageEnriched
        {
            MessageSnowflake = 700L,
            IR               = SimpleIr(new TextNode("plain message")),
            Tags             = [],
        }));

        await writer.Received(1).WriteAsync(
            Arg.Any<ReadDbContext>(),
            Arg.Any<IDbContextTransaction>(),
            Arg.Is<IReadOnlyDictionary<Type, IList<object>>>(d => !d.ContainsKey(typeof(MessageTag))),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithTags_OneMessageTagEntityPerTag()
    {
        IReadOnlyDictionary<Type, IList<object>>? captured = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer.When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call => captured = call.ArgAt<IReadOnlyDictionary<Type, IList<object>>>(2));

        var consumer = BuildConsumer(writer);
        await consumer.Consume(BuildBatchContext(new TestMessageEnriched
        {
            MessageSnowflake = 800L,
            IR               = SimpleIr(new TextNode("tagged")),
            Tags             = ["csharp", "dotnet", "mastertransit"],
        }));

        captured.ShouldNotBeNull();
        captured.ShouldContainKey(typeof(MessageTag));
        captured![typeof(MessageTag)].Count.ShouldBe(3);

        var tags = captured[typeof(MessageTag)].Cast<MessageTag>().Select(t => t.Tag).ToList();
        tags.ShouldContain("csharp");
        tags.ShouldContain("dotnet");
        tags.ShouldContain("mastertransit");
    }

    [Test]
    public async Task ReplyContext_ReadMessageReplyToIdSet()
    {
        IReadOnlyDictionary<Type, IList<object>>? captured = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer.When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call => captured = call.ArgAt<IReadOnlyDictionary<Type, IList<object>>>(2));

        var ir = new MessageIR(
            Body:        [new TextNode("reply text")],
            Attachments: [],
            Embeds:      [],
            ReplyTo:     new ReplyContext(ReplyToMessageId: 12345L, ReplyToChannelId: 99L, ReplyToAuthorId: 77L),
            CapturedAt:  DateTimeOffset.UtcNow);

        var consumer = BuildConsumer(writer);
        await consumer.Consume(BuildBatchContext(new TestMessageEnriched
        {
            MessageSnowflake = 900L,
            IR               = ir,
        }));

        captured.ShouldNotBeNull();
        var msg = (ReadMessage)captured![typeof(ReadMessage)][0];
        msg.ReplyToId.ShouldBe(12345L);
    }

    [Test]
    public async Task PlainText_DerivedFromIr_NonEmptyForTypicalInput()
    {
        IReadOnlyDictionary<Type, IList<object>>? captured = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer.When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call => captured = call.ArgAt<IReadOnlyDictionary<Type, IList<object>>>(2));

        var consumer = BuildConsumer(writer);
        await consumer.Consume(BuildBatchContext(new TestMessageEnriched
        {
            MessageSnowflake = 1000L,
            IR               = SimpleIr(new TextNode("hello world"), new TextNode("!")),
        }));

        captured.ShouldNotBeNull();
        var msg = (ReadMessage)captured![typeof(ReadMessage)][0];
        msg.PlainText.ShouldNotBeNullOrWhiteSpace();
        msg.PlainText.ShouldContain("hello world");
    }

    [Test]
    public async Task MentionAndChannelRefInIr_ProduceMessageReferenceRows_StableOrdinals()
    {
        IReadOnlyDictionary<Type, IList<object>>? captured = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer.When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call => captured = call.ArgAt<IReadOnlyDictionary<Type, IList<object>>>(2));

        var ir = new MessageIR(
            Body: [
                new TextNode("hey "),
                new MentionNode(MentionKind.User, Id: 42L, Fallback: "alice"),
                new TextNode(" check "),
                new ChannelRefNode(ChannelId: 55L, Fallback: "general"),
            ],
            Attachments: [],
            Embeds:      [],
            ReplyTo:     null,
            CapturedAt:  DateTimeOffset.UtcNow);

        var consumer = BuildConsumer(writer);
        await consumer.Consume(BuildBatchContext(new TestMessageEnriched
        {
            MessageSnowflake = 1100L,
            IR               = ir,
        }));

        captured.ShouldNotBeNull();
        captured.ShouldContainKey(typeof(MessageReference));
        var refs = captured![typeof(MessageReference)].Cast<MessageReference>().ToList();
        refs.Count.ShouldBe(2);

        // Mention is visited first (depth-first, left-to-right) → ordinal 0; ChannelRef next → ordinal 1
        var mention = refs.Single(r => r.Kind == (short)MentionKind.User);
        mention.TargetId.ShouldBe(42L);
        mention.Ordinal.ShouldBe((short)0);

        var chanRef = refs.Single(r => r.Kind == ReferenceKind.ChannelRef);
        chanRef.TargetId.ShouldBe(55L);
        chanRef.Ordinal.ShouldBe((short)1);
    }

    [Test]
    public async Task NoAttachmentsNoEmbeds_ReadMessageFlagsCorrect()
    {
        IReadOnlyDictionary<Type, IList<object>>? captured = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer.When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call => captured = call.ArgAt<IReadOnlyDictionary<Type, IList<object>>>(2));

        var consumer = BuildConsumer(writer);
        await consumer.Consume(BuildBatchContext(new TestMessageEnriched
        {
            MessageSnowflake = 1200L,
            IR               = SimpleIr(new TextNode("just text")),
        }));

        captured.ShouldNotBeNull();
        var msg = (ReadMessage)captured![typeof(ReadMessage)][0];
        msg.HasAttachments.ShouldBeFalse();
        msg.HasEmbeds.ShouldBeFalse();
        msg.HasCode.ShouldBeFalse();
    }

    [Test]
    public async Task CodeBlock_HasCodeFlagSet()
    {
        IReadOnlyDictionary<Type, IList<object>>? captured = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer.When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call => captured = call.ArgAt<IReadOnlyDictionary<Type, IList<object>>>(2));

        var consumer = BuildConsumer(writer);
        await consumer.Consume(BuildBatchContext(new TestMessageEnriched
        {
            MessageSnowflake = 1300L,
            IR               = SimpleIr(new CodeBlockNode("csharp", "var x = 1;")),
        }));

        captured.ShouldNotBeNull();
        var msg = (ReadMessage)captured![typeof(ReadMessage)][0];
        msg.HasCode.ShouldBeTrue();
    }
}
