using DiscordScraper.Read.Data;

namespace DiscordScraper.Read.Tests.Data;

/// <summary>
/// Unit tests for ReadSchemaInitializer.GenerateStatements().
/// All assertions run against the static method — no DB connection required.
/// </summary>
[TestFixture]
public sealed class ReadSchemaInitializerTests
{
    private IReadOnlyList<string> _statements = null!;

    [SetUp]
    public void SetUp()
    {
        _statements = ReadSchemaInitializer.GenerateStatements();
    }

    [Test]
    public void GenerateStatements_IsNonEmpty()
    {
        _statements.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public void GenerateStatements_ContainsTimescaleExtension()
    {
        _statements.ShouldContain(s => s.Contains("CREATE EXTENSION IF NOT EXISTS timescaledb"));
    }

    [Test]
    public void GenerateStatements_ContainsVectorExtension()
    {
        _statements.ShouldContain(s => s.Contains("CREATE EXTENSION IF NOT EXISTS vector"));
    }

    [Test]
    public void GenerateStatements_ContainsReadMessagesTable()
    {
        _statements.ShouldContain(s =>
            s.Contains("CREATE TABLE IF NOT EXISTS read_messages") &&
            s.Contains("message_id") &&
            s.Contains("created_at") &&
            s.Contains("ir") &&
            s.Contains("JSONB"));
    }

    [Test]
    public void GenerateStatements_ReadMessages_HasGeneratedTsvColumn()
    {
        _statements.ShouldContain(s =>
            s.Contains("tsv") &&
            s.Contains("GENERATED ALWAYS AS") &&
            s.Contains("to_tsvector"));
    }

    [Test]
    public void GenerateStatements_ContainsCreateHypertable()
    {
        _statements.ShouldContain(s =>
            s.Contains("create_hypertable") &&
            s.Contains("read_messages") &&
            s.Contains("created_at") &&
            s.Contains("if_not_exists => TRUE"));
    }

    [Test]
    public void GenerateStatements_ContainsCompressionPolicy()
    {
        _statements.ShouldContain(s =>
            s.Contains("add_compression_policy") &&
            s.Contains("read_messages") &&
            s.Contains("if_not_exists => TRUE"));
    }

    [Test]
    public void GenerateStatements_ContainsContinuousAggregate()
    {
        _statements.ShouldContain(s =>
            s.Contains("messages_per_channel_per_hour") &&
            s.Contains("timescaledb.continuous"));
    }

    [Test]
    public void GenerateStatements_ContainsContinuousAggregatePolicy()
    {
        _statements.ShouldContain(s =>
            s.Contains("add_continuous_aggregate_policy") &&
            s.Contains("messages_per_channel_per_hour") &&
            s.Contains("if_not_exists     => TRUE"));
    }

    [Test]
    public void GenerateStatements_ContainsExtractionTables()
    {
        var tableNames = new[]
        {
            "message_references",
            "message_attachments",
            "message_embeds",
            "message_tags",
        };

        foreach (var table in tableNames)
            _statements.ShouldContain(s => s.Contains($"CREATE TABLE IF NOT EXISTS {table}"),
                $"Expected DDL for {table}");
    }

    [Test]
    public void GenerateStatements_ContainsEntityContextTables()
    {
        _statements.ShouldContain(s => s.Contains("CREATE TABLE IF NOT EXISTS read_channels"));
        _statements.ShouldContain(s => s.Contains("CREATE TABLE IF NOT EXISTS read_guilds"));
    }

    [Test]
    public void GenerateStatements_HypertableAppearsAfterTableCreation()
    {
        var tableIdx = _statements
            .Select((s, i) => (s, i))
            .First(x => x.s.Contains("CREATE TABLE IF NOT EXISTS read_messages")).i;

        var hypertableIdx = _statements
            .Select((s, i) => (s, i))
            .First(x => x.s.Contains("create_hypertable")).i;

        hypertableIdx.ShouldBeGreaterThan(tableIdx,
            "create_hypertable must execute after the table exists");
    }

    [Test]
    public void GenerateStatements_ContinuousAggregateAppearsAfterHypertable()
    {
        var hypertableIdx = _statements
            .Select((s, i) => (s, i))
            .First(x => x.s.Contains("create_hypertable")).i;

        var caIdx = _statements
            .Select((s, i) => (s, i))
            .First(x => x.s.Contains("messages_per_channel_per_hour") &&
                        x.s.Contains("timescaledb.continuous")).i;

        caIdx.ShouldBeGreaterThan(hypertableIdx,
            "continuous aggregate must be created after the hypertable");
    }
}
