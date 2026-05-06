using System.Collections;
using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.IR;
using DiscordScraper.Read.Consumers;
using DiscordScraper.Read.Data.Entities;
using DiscordScraper.Read.Mapping;
using MassTransit;
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
    public DateTimeOffset UpdatedOn { get; set; } = DateTimeOffset.UtcNow;
    public Guid CorrelationId => DeterministicGuid.FromSnowflake(MessageSnowflake);

    public MessageIR IR { get; init; } = new([], [], [], null, DateTimeOffset.UtcNow);
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool IsSubstantive { get; init; } = true;
    public bool IsBot { get; init; }
    public DateTimeOffset MessageCreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EditedTimestamp { get; init; }
}

/// <summary>
/// Per-projection consumer tests for <see cref="MessageEnriched"/>.
/// Five nested classes, one per table. Each consumer is tested independently.
/// </summary>
[TestFixture]
public sealed class MessageReadConsumerTests
{
    // ---------------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------------

    private static ConsumeContext<Batch<MessageEnriched>> BuildBatchContext(
        params MessageEnriched[] events)
    {
        var batch = new FakeMessageEnrichedBatch(events);
        var ctx = Substitute.For<ConsumeContext<Batch<MessageEnriched>>>();
        ctx.Message.Returns(batch);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private sealed class FakeMessageEnrichedBatch(MessageEnriched[] events) : Batch<MessageEnriched>
    {
        private readonly ConsumeContext<MessageEnriched>[] _messages = events
            .Select(e =>
            {
                var m = Substitute.For<ConsumeContext<MessageEnriched>>();
                m.Message.Returns(e);
                return m;
            })
            .ToArray();

        public BatchCompletionMode Mode => BatchCompletionMode.Time;
        public DateTime FirstMessageReceived => DateTime.UtcNow;
        public DateTime LastMessageReceived => DateTime.UtcNow;
        public ConsumeContext<MessageEnriched> this[int i] => _messages[i];
        public int Length => _messages.Length;

        public IEnumerator<ConsumeContext<MessageEnriched>> GetEnumerator() =>
            ((IEnumerable<ConsumeContext<MessageEnriched>>)_messages).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static MessageIR SimpleIr(params MessageNode[] body) =>
        new(Body: body, Attachments: [], Embeds: [], ReplyTo: null, CapturedAt: DateTimeOffset.UtcNow);

    // ---------------------------------------------------------------------------
    // ReadMessage projection
    // ---------------------------------------------------------------------------

    [TestFixture]
    public sealed class ReadMessageProjection
    {
        private static ReadMessageProjectionConsumer BuildConsumer(IBulkWriter<ReadMessage> writer) =>
            new(new BatchProjectionPipeline<MessageEnriched, ReadMessage>(
                new MapperlyReadMessageProjector(),
                writer,
                NullLogger<BatchProjectionPipeline<MessageEnriched, ReadMessage>>.Instance));

        [Test]
        public void Projector_FieldMapping_ScalarFields()
        {
            var projector = new MapperlyReadMessageProjector();
            var evt = new TestMessageEnriched
            {
                MessageSnowflake = 111_000_000L,
                ChannelId        = 222L,
                GuildId          = 333L,
                AuthorId         = 444L,
                IR               = SimpleIr(new TextNode("hello")),
            };

            var msg = projector.Project(evt).Single();

            msg.MessageId.ShouldBe(111_000_000L);
            msg.ChannelId.ShouldBe(222L);
            msg.GuildId.ShouldBe(333L);
            msg.AuthorId.ShouldBe(444L);
        }

        [Test]
        public void Projector_PlainText_DerivedFromIr()
        {
            var projector = new MapperlyReadMessageProjector();
            var evt = new TestMessageEnriched
            {
                MessageSnowflake = 1000L,
                IR               = SimpleIr(new TextNode("hello world"), new TextNode("!")),
            };

            var msg = projector.Project(evt).Single();
            msg.PlainText.ShouldNotBeNullOrWhiteSpace();
            msg.PlainText.ShouldContain("hello world");
        }

        [Test]
        public void Projector_NoAttachmentsNoEmbeds_FlagsCorrect()
        {
            var projector = new MapperlyReadMessageProjector();
            var msg = projector.Project(new TestMessageEnriched
            {
                MessageSnowflake = 1200L,
                IR               = SimpleIr(new TextNode("just text")),
            }).Single();

            msg.HasAttachments.ShouldBeFalse();
            msg.HasEmbeds.ShouldBeFalse();
            msg.HasCode.ShouldBeFalse();
        }

        [Test]
        public void Projector_CodeBlock_HasCodeFlagSet()
        {
            var projector = new MapperlyReadMessageProjector();
            var msg = projector.Project(new TestMessageEnriched
            {
                MessageSnowflake = 1300L,
                IR               = SimpleIr(new CodeBlockNode("csharp", "var x = 1;")),
            }).Single();

            msg.HasCode.ShouldBeTrue();
        }

        [Test]
        public void Projector_ReplyContext_ReplyToIdSet()
        {
            var projector = new MapperlyReadMessageProjector();
            var ir = new MessageIR(
                Body:        [new TextNode("reply text")],
                Attachments: [],
                Embeds:      [],
                ReplyTo:     new ReplyContext(ReplyToMessageId: 12345L, ReplyToChannelId: 99L, ReplyToAuthorId: 77L),
                CapturedAt:  DateTimeOffset.UtcNow);

            var msg = projector.Project(new TestMessageEnriched { MessageSnowflake = 900L, IR = ir }).Single();
            msg.ReplyToId.ShouldBe(12345L);
        }

        [Test]
        public void Projector_WithAttachments_HasAttachmentsFlagSet()
        {
            var projector = new MapperlyReadMessageProjector();
            var ir = new MessageIR(
                Body:        [new TextNode("see attachment")],
                Attachments: [new AttachmentIR(99L, "https://cdn.example.com/img.png", "image/png", 2048L, "img.png")],
                Embeds:      [],
                ReplyTo:     null,
                CapturedAt:  DateTimeOffset.UtcNow);

            var msg = projector.Project(new TestMessageEnriched { MessageSnowflake = 500L, IR = ir }).Single();
            msg.HasAttachments.ShouldBeTrue();
        }

        [Test]
        public async Task Consumer_SingleEnriched_WriterReceivesOneReadMessage()
        {
            var writer = Substitute.For<IBulkWriter<ReadMessage>>();
            await BuildConsumer(writer).Consume(BuildBatchContext(new TestMessageEnriched
            {
                MessageSnowflake = 111_000_000L,
                IR               = SimpleIr(new TextNode("hello")),
            }));

            await writer.Received(1).WriteAsync(
                Arg.Is<IEnumerable<ReadMessage>>(e => e.Count() == 1),
                Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task Consumer_BatchOf5_WriterReceives5ReadMessages()
        {
            var writer = Substitute.For<IBulkWriter<ReadMessage>>();
            var events = Enumerable.Range(1, 5)
                .Select(i => (MessageEnriched)new TestMessageEnriched
                {
                    MessageSnowflake = i * 1000L,
                    IR               = SimpleIr(new TextNode($"message {i}")),
                })
                .ToArray();

            await BuildConsumer(writer).Consume(BuildBatchContext(events));

            await writer.Received(1).WriteAsync(
                Arg.Is<IEnumerable<ReadMessage>>(e => e.Count() == 5),
                Arg.Any<CancellationToken>());
        }
    }

    // ---------------------------------------------------------------------------
    // MessageReference projection
    // ---------------------------------------------------------------------------

    [TestFixture]
    public sealed class MessageReferenceProjection
    {
        private static MessageReferenceProjectionConsumer BuildConsumer(IBulkWriter<MessageReference> writer) =>
            new(new BatchProjectionPipeline<MessageEnriched, MessageReference>(
                new MapperlyMessageReferenceProjector(),
                writer,
                NullLogger<BatchProjectionPipeline<MessageEnriched, MessageReference>>.Instance));

        [Test]
        public void Projector_MentionAndChannelRef_ProduceReferenceRows_StableOrdinals()
        {
            var projector = new MapperlyMessageReferenceProjector();
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

            var refs = projector.Project(new TestMessageEnriched { MessageSnowflake = 1100L, IR = ir }).ToList();

            refs.Count.ShouldBe(2);
            var mention = refs.Single(r => r.Kind == (short)MentionKind.User);
            mention.TargetId.ShouldBe(42L);
            mention.Ordinal.ShouldBe((short)0);

            var chanRef = refs.Single(r => r.Kind == ReferenceKind.ChannelRef);
            chanRef.TargetId.ShouldBe(55L);
            chanRef.Ordinal.ShouldBe((short)1);
        }

        [Test]
        public void Projector_NoReferences_ReturnsEmpty()
        {
            var projector = new MapperlyMessageReferenceProjector();
            var refs = projector.Project(new TestMessageEnriched
            {
                MessageSnowflake = 999L,
                IR               = SimpleIr(new TextNode("plain")),
            }).ToList();

            refs.ShouldBeEmpty();
        }

        [Test]
        public async Task Consumer_WithReferences_WriterCalledWithReferenceRows()
        {
            var writer = Substitute.For<IBulkWriter<MessageReference>>();
            var ir = new MessageIR(
                Body: [new MentionNode(MentionKind.User, Id: 42L, Fallback: "alice")],
                Attachments: [],
                Embeds:      [],
                ReplyTo:     null,
                CapturedAt:  DateTimeOffset.UtcNow);

            await BuildConsumer(writer).Consume(BuildBatchContext(new TestMessageEnriched { MessageSnowflake = 1100L, IR = ir }));

            await writer.Received(1).WriteAsync(
                Arg.Is<IEnumerable<MessageReference>>(e => e.Count() == 1),
                Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task Consumer_NoReferences_WriterNotCalled()
        {
            var writer = Substitute.For<IBulkWriter<MessageReference>>();
            await BuildConsumer(writer).Consume(BuildBatchContext(new TestMessageEnriched
            {
                MessageSnowflake = 999L,
                IR               = SimpleIr(new TextNode("no refs")),
            }));

            await writer.DidNotReceive().WriteAsync(Arg.Any<IEnumerable<MessageReference>>(), Arg.Any<CancellationToken>());
        }
    }

    // ---------------------------------------------------------------------------
    // MessageAttachment projection
    // ---------------------------------------------------------------------------

    [TestFixture]
    public sealed class MessageAttachmentProjection
    {
        private static MessageAttachmentProjectionConsumer BuildConsumer(IBulkWriter<MessageAttachment> writer) =>
            new(new BatchProjectionPipeline<MessageEnriched, MessageAttachment>(
                new MapperlyMessageAttachmentProjector(),
                writer,
                NullLogger<BatchProjectionPipeline<MessageEnriched, MessageAttachment>>.Instance));

        [Test]
        public void Projector_WithAttachment_FieldsMapped()
        {
            var projector = new MapperlyMessageAttachmentProjector();
            var ir = new MessageIR(
                Body:        [new TextNode("see attachment")],
                Attachments: [new AttachmentIR(99L, "https://cdn.example.com/img.png", "image/png", 2048L, "img.png")],
                Embeds:      [],
                ReplyTo:     null,
                CapturedAt:  DateTimeOffset.UtcNow);

            var att = projector.Project(new TestMessageEnriched { MessageSnowflake = 500L, IR = ir }).Single();

            att.AttachmentId.ShouldBe(99L);
            att.Url.ShouldBe("https://cdn.example.com/img.png");
            att.ContentType.ShouldBe("image/png");
            att.SizeBytes.ShouldBe(2048L);
        }

        [Test]
        public void Projector_NoAttachments_ReturnsEmpty()
        {
            var projector = new MapperlyMessageAttachmentProjector();
            projector.Project(new TestMessageEnriched { MessageSnowflake = 700L, IR = SimpleIr(new TextNode("x")) })
                .ShouldBeEmpty();
        }

        [Test]
        public async Task Consumer_WithAttachment_WriterCalledWithAttachmentRow()
        {
            var writer = Substitute.For<IBulkWriter<MessageAttachment>>();
            var ir = new MessageIR(
                Body:        [new TextNode("see attachment")],
                Attachments: [new AttachmentIR(99L, "https://cdn.example.com/img.png", "image/png", 2048L, "img.png")],
                Embeds:      [],
                ReplyTo:     null,
                CapturedAt:  DateTimeOffset.UtcNow);

            await BuildConsumer(writer).Consume(BuildBatchContext(new TestMessageEnriched { MessageSnowflake = 500L, IR = ir }));

            await writer.Received(1).WriteAsync(
                Arg.Is<IEnumerable<MessageAttachment>>(e => e.Count() == 1),
                Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task Consumer_NoAttachments_WriterNotCalled()
        {
            var writer = Substitute.For<IBulkWriter<MessageAttachment>>();
            await BuildConsumer(writer).Consume(BuildBatchContext(new TestMessageEnriched
            {
                MessageSnowflake = 700L,
                IR               = SimpleIr(new TextNode("no attachments")),
            }));

            await writer.DidNotReceive().WriteAsync(Arg.Any<IEnumerable<MessageAttachment>>(), Arg.Any<CancellationToken>());
        }
    }

    // ---------------------------------------------------------------------------
    // MessageEmbed projection
    // ---------------------------------------------------------------------------

    [TestFixture]
    public sealed class MessageEmbedProjection
    {
        private static MessageEmbedProjectionConsumer BuildConsumer(IBulkWriter<MessageEmbed> writer) =>
            new(new BatchProjectionPipeline<MessageEnriched, MessageEmbed>(
                new MapperlyMessageEmbedProjector(),
                writer,
                NullLogger<BatchProjectionPipeline<MessageEnriched, MessageEmbed>>.Instance));

        [Test]
        public void Projector_WithEmbed_FieldsMapped()
        {
            var projector = new MapperlyMessageEmbedProjector();
            var ir = new MessageIR(
                Body:        [new TextNode("embed")],
                Attachments: [],
                Embeds:      [new EmbedIR(0, "link", "https://example.com", "Title", "Desc")],
                ReplyTo:     null,
                CapturedAt:  DateTimeOffset.UtcNow);

            var emb = projector.Project(new TestMessageEnriched { MessageSnowflake = 600L, IR = ir }).Single();

            emb.EmbedIndex.ShouldBe((short)0);
            emb.Title.ShouldBe("Title");
            emb.Url.ShouldBe("https://example.com");
        }

        [Test]
        public void Projector_NoEmbeds_ReturnsEmpty()
        {
            var projector = new MapperlyMessageEmbedProjector();
            projector.Project(new TestMessageEnriched { MessageSnowflake = 600L, IR = SimpleIr(new TextNode("x")) })
                .ShouldBeEmpty();
        }

        [Test]
        public async Task Consumer_WithEmbed_WriterCalledWithEmbedRow()
        {
            var writer = Substitute.For<IBulkWriter<MessageEmbed>>();
            var ir = new MessageIR(
                Body:        [new TextNode("embed")],
                Attachments: [],
                Embeds:      [new EmbedIR(0, "link", "https://example.com", "Title", "Desc")],
                ReplyTo:     null,
                CapturedAt:  DateTimeOffset.UtcNow);

            await BuildConsumer(writer).Consume(BuildBatchContext(new TestMessageEnriched { MessageSnowflake = 600L, IR = ir }));

            await writer.Received(1).WriteAsync(
                Arg.Is<IEnumerable<MessageEmbed>>(e => e.Count() == 1),
                Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task Consumer_NoEmbeds_WriterNotCalled()
        {
            var writer = Substitute.For<IBulkWriter<MessageEmbed>>();
            await BuildConsumer(writer).Consume(BuildBatchContext(new TestMessageEnriched
            {
                MessageSnowflake = 700L,
                IR               = SimpleIr(new TextNode("no embeds")),
            }));

            await writer.DidNotReceive().WriteAsync(Arg.Any<IEnumerable<MessageEmbed>>(), Arg.Any<CancellationToken>());
        }
    }

    // ---------------------------------------------------------------------------
    // MessageTag projection
    // ---------------------------------------------------------------------------

    [TestFixture]
    public sealed class MessageTagProjection
    {
        private static MessageTagProjectionConsumer BuildConsumer(IBulkWriter<MessageTag> writer) =>
            new(new BatchProjectionPipeline<MessageEnriched, MessageTag>(
                new MapperlyMessageTagProjector(),
                writer,
                NullLogger<BatchProjectionPipeline<MessageEnriched, MessageTag>>.Instance));

        [Test]
        public void Projector_WithTags_ProducesOneTagPerEntry()
        {
            var projector = new MapperlyMessageTagProjector();
            var tags = projector.Project(new TestMessageEnriched
            {
                MessageSnowflake = 800L,
                IR               = SimpleIr(new TextNode("tagged")),
                Tags             = ["csharp", "dotnet", "masstransit"],
            }).ToList();

            tags.Count.ShouldBe(3);
            tags.Select(t => t.Tag).ShouldContain("csharp");
            tags.Select(t => t.Tag).ShouldContain("dotnet");
            tags.Select(t => t.Tag).ShouldContain("masstransit");
        }

        [Test]
        public void Projector_EmptyTags_ReturnsEmpty()
        {
            var projector = new MapperlyMessageTagProjector();
            projector.Project(new TestMessageEnriched
            {
                MessageSnowflake = 700L,
                IR               = SimpleIr(new TextNode("plain")),
                Tags             = [],
            }).ShouldBeEmpty();
        }

        [Test]
        public async Task Consumer_WithTags_WriterCalledWithTagRows()
        {
            var writer = Substitute.For<IBulkWriter<MessageTag>>();
            await BuildConsumer(writer).Consume(BuildBatchContext(new TestMessageEnriched
            {
                MessageSnowflake = 800L,
                IR               = SimpleIr(new TextNode("tagged")),
                Tags             = ["csharp", "dotnet", "masstransit"],
            }));

            await writer.Received(1).WriteAsync(
                Arg.Is<IEnumerable<MessageTag>>(e => e.Count() == 3),
                Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task Consumer_EmptyTags_WriterNotCalled()
        {
            var writer = Substitute.For<IBulkWriter<MessageTag>>();
            await BuildConsumer(writer).Consume(BuildBatchContext(new TestMessageEnriched
            {
                MessageSnowflake = 700L,
                IR               = SimpleIr(new TextNode("plain message")),
                Tags             = [],
            }));

            await writer.DidNotReceive().WriteAsync(Arg.Any<IEnumerable<MessageTag>>(), Arg.Any<CancellationToken>());
        }
    }
}
