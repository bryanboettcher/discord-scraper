using DiscordScraper.Contracts.IR;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.Write.Consumers;
using DiscordScraper.Write.Parsing;
using DiscordScraper.Write.Repositories;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiscordScraper.Write.Tests.Consumers;

[TestFixture]
public sealed class ProjectMessageConsumerTests
{
    private static readonly DateTimeOffset At = new(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // Builds a minimal Discord message JSON with the given content string.
    private static string PayloadJson(string content, object[]? mentions = null)
    {
        var mentionsJson = mentions is not null
            ? System.Text.Json.JsonSerializer.Serialize(mentions)
            : "[]";
        return $$"""{"content":{{System.Text.Json.JsonSerializer.Serialize(content)}},"mentions":{{mentionsJson}}}""";
    }

    // Sets up a harness with real MessageParser and stubbed repos.
    private static async Task<(ITestHarness Harness, IChannelNameRepo Channels, IGuildRoleNameRepo GuildRoles, ServiceProvider Provider)>
        BuildHarness(
            IChannelNameRepo? channelRepo = null,
            IGuildRoleNameRepo? roleRepo = null)
    {
        if (channelRepo is null)
        {
            channelRepo = Substitute.For<IChannelNameRepo>();
            channelRepo.GetNamesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<long, string>());
        }

        if (roleRepo is null)
        {
            roleRepo = Substitute.For<IGuildRoleNameRepo>();
            roleRepo.GetRoleNamesAsync(Arg.Any<long>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<long, string>());
        }

        var provider = new ServiceCollection()
            .AddSingleton<IMessageParser>(new MessageParser(NullLogger<MessageParser>.Instance))
            .AddSingleton(channelRepo)
            .AddSingleton(roleRepo)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<ProjectMessageConsumer, ProjectMessageConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        return (harness, channelRepo, roleRepo, provider);
    }

    private static ProjectMessageRequest MakeRequest(string payloadJson, long channelId = 100L, long guildId = 999L) =>
        new() { MessageSnowflake = 1L, ChannelId = channelId, GuildId = guildId, PayloadJson = payloadJson };

    // ── Test 1: channel ref resolved from repo ────────────────────────────────

    [Test]
    public async Task Channel_ref_fallback_populated_from_repo()
    {
        var channelRepo = Substitute.For<IChannelNameRepo>();
        channelRepo.GetNamesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string> { [123L] = "general" });

        var (harness, _, _, provider) = await BuildHarness(channelRepo: channelRepo);
        await using var _ = provider;

        try
        {
            var client = harness.GetRequestClient<ProjectMessageRequest>();
            var response = await client.GetResponse<ProjectMessageResponse>(
                MakeRequest(PayloadJson("<#123>")));

            var chan = response.Message.IR.Body
                .OfType<ChannelRefNode>()
                .Single();

            chan.ChannelId.ShouldBe(123L);
            chan.Fallback.ShouldBe("general");
        }
        finally
        {
            await harness.Stop();
        }
    }

    // ── Test 2: role mention fallback populated from repo ────────────────────

    [Test]
    public async Task Role_mention_fallback_populated_from_repo()
    {
        var roleRepo = Substitute.For<IGuildRoleNameRepo>();
        roleRepo.GetRoleNamesAsync(Arg.Any<long>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string> { [456L] = "Mods" });

        var (harness, _, _, provider) = await BuildHarness(roleRepo: roleRepo);
        await using var _ = provider;

        try
        {
            var client = harness.GetRequestClient<ProjectMessageRequest>();
            var response = await client.GetResponse<ProjectMessageResponse>(
                MakeRequest(PayloadJson("<@&456>")));

            var mention = response.Message.IR.Body
                .OfType<MentionNode>()
                .Single(m => m.Kind == MentionKind.Role);

            mention.Id.ShouldBe(456L);
            mention.Fallback.ShouldBe("Mods");
        }
        finally
        {
            await harness.Stop();
        }
    }

    // ── Test 3: unknown channel gets default fallback string ─────────────────

    [Test]
    public async Task Channel_ref_unknown_to_repo_gets_unknown_fallback()
    {
        // Channel repo returns empty — channel 789 not found.
        var (harness, _, _, provider) = await BuildHarness();
        await using var _ = provider;

        try
        {
            var client = harness.GetRequestClient<ProjectMessageRequest>();
            var response = await client.GetResponse<ProjectMessageResponse>(
                MakeRequest(PayloadJson("<#789>")));

            var chan = response.Message.IR.Body
                .OfType<ChannelRefNode>()
                .Single();

            chan.ChannelId.ShouldBe(789L);
            chan.Fallback.ShouldBe("<unknown channel>");
        }
        finally
        {
            await harness.Stop();
        }
    }

    // ── Test 4: both repo methods called concurrently (parallel lookup) ───────

    [Test]
    public async Task Both_repos_called_when_payload_has_channel_and_role_refs()
    {
        var channelRepo = Substitute.For<IChannelNameRepo>();
        channelRepo.GetNamesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string>());

        var roleRepo = Substitute.For<IGuildRoleNameRepo>();
        roleRepo.GetRoleNamesAsync(Arg.Any<long>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string>());

        var (harness, _, _, provider) = await BuildHarness(
            channelRepo: channelRepo,
            roleRepo: roleRepo);
        await using var _ = provider;

        try
        {
            var client = harness.GetRequestClient<ProjectMessageRequest>();
            await client.GetResponse<ProjectMessageResponse>(
                MakeRequest(PayloadJson("<#123> and <@&456>")));

            await channelRepo.Received(1).GetNamesAsync(
                Arg.Is<IReadOnlyCollection<long>>(ids => ids.Contains(123L)),
                Arg.Any<CancellationToken>());

            await roleRepo.Received(1).GetRoleNamesAsync(
                Arg.Any<long>(),
                Arg.Is<IReadOnlyCollection<long>>(ids => ids.Contains(456L)),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await harness.Stop();
        }
    }

    // ── Test 5: nested fallback inside FormattingNode reached by recursive walk

    [Test]
    public async Task Channel_ref_nested_inside_bold_formatting_gets_resolved()
    {
        var channelRepo = Substitute.For<IChannelNameRepo>();
        channelRepo.GetNamesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string> { [321L] = "announcements" });

        var (harness, _, _, provider) = await BuildHarness(channelRepo: channelRepo);
        await using var _ = provider;

        try
        {
            var client = harness.GetRequestClient<ProjectMessageRequest>();
            var response = await client.GetResponse<ProjectMessageResponse>(
                MakeRequest(PayloadJson("check **<#321>** now")));

            // Navigate: Body[1] = FormattingNode(Bold) → Children[0] = ChannelRefNode
            var bold = response.Message.IR.Body
                .OfType<FormattingNode>()
                .Single(f => f.Kind == FormattingKind.Bold);

            var chan = bold.Children
                .OfType<ChannelRefNode>()
                .Single();

            chan.ChannelId.ShouldBe(321L);
            chan.Fallback.ShouldBe("announcements");
        }
        finally
        {
            await harness.Stop();
        }
    }

    // ── Test 6: no refs → repos called with empty collections ────────────────

    [Test]
    public async Task No_refs_in_payload_repos_called_with_empty_collections()
    {
        var channelRepo = Substitute.For<IChannelNameRepo>();
        channelRepo.GetNamesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string>());

        var roleRepo = Substitute.For<IGuildRoleNameRepo>();
        roleRepo.GetRoleNamesAsync(Arg.Any<long>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string>());

        var (harness, _, _, provider) = await BuildHarness(
            channelRepo: channelRepo,
            roleRepo: roleRepo);
        await using var _ = provider;

        try
        {
            var client = harness.GetRequestClient<ProjectMessageRequest>();
            var response = await client.GetResponse<ProjectMessageResponse>(
                MakeRequest(PayloadJson("hello world, no refs here")));

            // Consumer still calls repos (with empty sets) — the WhenAll always fires.
            await channelRepo.Received(1).GetNamesAsync(
                Arg.Is<IReadOnlyCollection<long>>(ids => ids.Count == 0),
                Arg.Any<CancellationToken>());

            await roleRepo.Received(1).GetRoleNamesAsync(
                Arg.Any<long>(),
                Arg.Is<IReadOnlyCollection<long>>(ids => ids.Count == 0),
                Arg.Any<CancellationToken>());

            // IR body is just a text node.
            response.Message.IR.Body.Count.ShouldBe(1);
            response.Message.IR.Body[0].ShouldBeOfType<TextNode>().Text.ShouldBe("hello world, no refs here");
        }
        finally
        {
            await harness.Stop();
        }
    }
}
