# discord-scraper — session kickoff brief

**Read this first.** This is a handoff from a prior architecture-discussion session. Everything below is self-contained; you don't need the prior conversation.

## What this project is

A long-running service that ingests Discord messages into a cheap, durable, query-friendly store. It is the **service half** of a two-repo system:

- **This repo** (`~/src/bryanboettcher/discord-scraper`) — ingester, raw store, enrichment pipeline, admin API. Deployed first to local Docker, then to the owner's k8s cluster.
- **Sibling repo** (`~/src/projects/claude-discord`) — stateless MCP server that Claude Code invokes locally. Queries this repo's Postgres + Qdrant read-only. Does **not** share C# code with this repo; they communicate through the database schema.

The end goal is "summarize disparate ongoing discussions into a knowledge base" — eventually landing in XWiki, intermediate target is markdown. Catalyst was the owner's friend saying "I wish we could document everything we've talked about."

## Architectural commitments (don't relitigate these)

1. **Two-tier store: capture everything raw, derive everything else.**
   - Tier 1 (`raw_*` tables): immutable JSONB log of exactly what Discord returned. We never re-fetch Discord for data we already have.
   - Tier 2 (`messages`, `message_enrichments`): disposable projections rebuilt from Tier 1 whenever logic or models change.
2. **Three independent `BackgroundService` workers**, coordinating through Postgres only — no in-memory queues, no message bus.
   - `DiscordSyncWorker` — Discord REST → `raw_messages`, `raw_guilds`, `raw_channels`. Pure fetch, no interpretation.
   - `ProjectionWorker` — `raw_messages` → `messages`. Pure C#, resolves mentions, strips noise, extracts normalized fields. No LLM.
   - `EnrichmentWorker` — `messages` → `message_enrichments` + Qdrant. Calls Ollama for topic tagging + embeddings.
3. **Mirror the resume-chat codebase at `/home/insta/src/bryanboettcher/resume-chat`** for conventions: .NET 10, minimal APIs, EF Core 10 + Postgres 17, Qdrant via REST (no SDK), Ollama via HTTP, NUnit + Shouldly + NSubstitute, OpenTelemetry (no Serilog), multi-project solution, `const string SectionName` options pattern, `AddHttpClient<I, Impl>()`, static `*Endpoints.MapTo(IEndpointRouteBuilder)`, multi-stage Dockerfile that runs tests during build. **Read that repo before making structural decisions** — the owner explicitly wants reuse, not reinvention.
4. **Local Docker first.** Owner wants iteration and debugging on local hardware (Docker with GPU passthrough, 64 GB / 24 cores). Cluster deployment comes later. Cluster is CPU-only but 14B models run acceptably there.
5. **Raw store is cheap, non-negotiable, and never schema-churns.** If we want a new field later, it's already in `payload JSONB`. If Discord adds a field, we capture it for free.
6. **Conversation structure (threads, replies) is additive metadata for downstream analysis, NOT a retrieval filter.** The owner's group frequently carries thread topics into other channels. Never hide thread messages from a channel view; never split a channel summary along thread boundaries by default. Reply chains and thread IDs are hints for grouping, exposed via an explicit `get_thread` tool if needed.

## Solution layout to create

```
discord-scraper/
  DiscordScraper.sln
  Directory.Build.props
  Directory.Packages.props       # central package management
  .editorconfig
  Dockerfile                     # multi-stage, runs tests during build
  docker-compose.yml             # postgres, qdrant, ingester, api
  src/
    DiscordScraper.Core/         # shared models + interfaces, zero deps
    DiscordScraper.Discord/      # REST client, rate limiting, pagination, 429 handling
    DiscordScraper.Storage/      # EF Core, DbContext, migrations, repositories
    DiscordScraper.Ingestion/    # projection + enrichment services
    DiscordScraper.Ingester/     # BackgroundService host (Program.cs + Workers)
    DiscordScraper.Api/          # minimal API: health, admin, SSE progress
    DiscordScraper.ServiceDefaults/  # copy of resume-chat's Aspire defaults
    DiscordScraper.Cli/          # one-shot: backfill, reindex, dry-run
  tests/
    DiscordScraper.Discord.Tests/
    DiscordScraper.Storage.Tests/
    DiscordScraper.Ingestion.Tests/
    DiscordScraper.Ingester.Tests/
```

Follow resume-chat's naming and dependency rules: `.Core` has zero deps, `.Storage` depends on `.Core`, `.Ingestion` depends on `.Storage` + `.Discord`, the host projects depend on everything, `.ServiceDefaults` is shared by all hosts.

## Schema (Postgres 17)

### Tier 1 — raw (immutable, JSONB-heavy)

```sql
-- Messages: one row per Discord message as returned by the API.
raw_messages(
    message_id   BIGINT PRIMARY KEY,           -- Discord snowflake
    channel_id   BIGINT NOT NULL,
    guild_id     BIGINT NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL,         -- decoded from snowflake once
    fetched_at   TIMESTAMPTZ NOT NULL,
    payload      JSONB NOT NULL                -- untouched Discord response
);
CREATE INDEX ON raw_messages (channel_id, created_at DESC);
CREATE INDEX ON raw_messages (guild_id, created_at DESC);

-- Edits: append-only, one row per observed edit.
raw_message_edits(
    message_id   BIGINT NOT NULL,
    edited_at    TIMESTAMPTZ NOT NULL,
    fetched_at   TIMESTAMPTZ NOT NULL,
    payload      JSONB NOT NULL,
    PRIMARY KEY (message_id, edited_at)
);

-- Guilds: append-only snapshot log. New row only when something meaningful changes.
raw_guilds(
    guild_id     BIGINT NOT NULL,
    fetched_at   TIMESTAMPTZ NOT NULL,
    payload      JSONB NOT NULL,
    PRIMARY KEY (guild_id, fetched_at)
);
CREATE VIEW guilds_current AS
    SELECT DISTINCT ON (guild_id) *
    FROM raw_guilds
    ORDER BY guild_id, fetched_at DESC;

-- Channels: same snapshot pattern. Includes threads (Discord channel type).
raw_channels(
    channel_id   BIGINT NOT NULL,
    guild_id     BIGINT NOT NULL,
    fetched_at   TIMESTAMPTZ NOT NULL,
    payload      JSONB NOT NULL,
    PRIMARY KEY (channel_id, fetched_at)
);
CREATE VIEW channels_current AS
    SELECT DISTINCT ON (channel_id) *
    FROM raw_channels
    ORDER BY channel_id, fetched_at DESC;

-- Sync bookkeeping: per-channel cursor for delta fetches.
raw_sync_state(
    channel_id            BIGINT PRIMARY KEY,
    last_message_id       BIGINT NOT NULL,        -- highest snowflake seen
    last_synced_at        TIMESTAMPTZ NOT NULL,
    last_error            TEXT,
    consecutive_errors    INT NOT NULL DEFAULT 0
);

-- Run log for observability / admin UI.
ingestion_runs(
    id                   BIGSERIAL PRIMARY KEY,
    worker               TEXT NOT NULL,           -- 'sync' | 'projection' | 'enrichment'
    started_at           TIMESTAMPTZ NOT NULL,
    completed_at         TIMESTAMPTZ,
    status               TEXT NOT NULL,           -- 'running' | 'success' | 'failed'
    items_processed      INT NOT NULL DEFAULT 0,
    error                TEXT
);
```

**Guild/channel snapshot write rule:** sync worker fetches the current payload, compares against `*_current` for a stable subset of fields (name, topic, parent_id, archived, nsfw — skip volatile stuff like member counts), and writes a new snapshot row only if that subset changed. First ever sighting always writes.

### Tier 2 — projections (disposable, rebuildable)

```sql
messages(
    message_id      BIGINT PRIMARY KEY REFERENCES raw_messages(message_id),
    channel_id      BIGINT NOT NULL,
    guild_id        BIGINT NOT NULL,
    author_id       BIGINT NOT NULL,
    author_name     TEXT NOT NULL,
    author_is_bot   BOOL NOT NULL,
    content         TEXT NOT NULL,           -- noise stripped, mentions resolved
    created_at      TIMESTAMPTZ NOT NULL,
    edited_at       TIMESTAMPTZ,
    reply_to_id     BIGINT,                  -- signal, not filter
    thread_id       BIGINT,                  -- signal, not filter
    root_channel_id BIGINT,                  -- parent of thread_id, else = channel_id
    has_attachments BOOL NOT NULL,
    content_tsv     TSVECTOR GENERATED ALWAYS AS (to_tsvector('english', content)) STORED
);
CREATE INDEX ON messages (channel_id, created_at DESC);
CREATE INDEX ON messages (guild_id, created_at DESC);
CREATE INDEX ON messages USING GIN (content_tsv);
CREATE INDEX ON messages (reply_to_id) WHERE reply_to_id IS NOT NULL;
CREATE INDEX ON messages (thread_id) WHERE thread_id IS NOT NULL;

message_enrichments(
    message_id          BIGINT PRIMARY KEY REFERENCES messages(message_id) ON DELETE CASCADE,
    embedding_model     TEXT NOT NULL,
    qdrant_point_id     TEXT NOT NULL,            -- SHA256 hex, like resume-chat
    topic_tags          TEXT[] NOT NULL DEFAULT '{}',
    is_substantive      BOOL NOT NULL,
    enriched_at         TIMESTAMPTZ NOT NULL,
    enrichment_version  INT NOT NULL              -- bump config to force re-enrich
);
CREATE INDEX ON message_enrichments (enrichment_version);
CREATE INDEX ON message_enrichments USING GIN (topic_tags);
```

### Key invariant

Tier 2 is **disposable**. `TRUNCATE messages CASCADE` + let the workers rebuild must always be a safe operation. Never put anything in Tier 2 that can't be reconstructed from Tier 1. This is the property that lets us iterate on projection logic, mention resolution, enrichment prompts, or embedding models without re-fetching Discord.

## Projection rules

The projection worker (`raw_messages` → `messages`) does pure C# work:

- Resolve user mentions: `<@123>` → `@BryanB` using current guild member data (fetched separately; see open decisions).
- Resolve channel mentions: `<#456>` → `#general` via `channels_current`.
- Resolve role mentions: `<@&789>` → `@admins` via guild roles payload.
- Leave custom emoji as `:name:` (don't try to resolve to image URLs).
- Strip bot-command prefixes and zero-width junk.
- Extract `reply_to_id` from `payload->'message_reference'->'message_id'`.
- If the channel is a thread (type 10/11/12), set `thread_id = channel_id` and `root_channel_id = parent_id`; otherwise `thread_id = NULL`, `root_channel_id = channel_id`.
- Decode `has_attachments` from payload.

Projection is idempotent: `INSERT ... ON CONFLICT (message_id) DO UPDATE`. Rerunning it rebuilds the table.

## Enrichment rules

The enrichment worker processes `messages` rows that are missing from `message_enrichments` OR whose `enrichment_version` is below the current config value.

Pipeline per message:
1. Call Ollama tagging endpoint with a prompt that returns `{topic_tags: string[], is_substantive: bool}`. Filter single-word replies, reactions, gifs to `is_substantive = false`.
2. If substantive, call Ollama embedding endpoint (`nomic-embed-text`), upsert to Qdrant with `qdrant_point_id = sha256(message_id + embedding_model)` as hex.
3. Write `message_enrichments` row with current `enrichment_version`.

Bumping `Enrichment:Version` in config causes the worker to re-process all rows. No schema migration needed.

## Configuration shape

Mirror resume-chat's options pattern. Each class has `const string SectionName`, registered via `AddOptions<T>().BindConfiguration(T.SectionName).ValidateDataAnnotations().ValidateOnStart()`.

```csharp
public sealed class DiscordOptions
{
    public const string SectionName = "Discord";
    [Required] public string BotToken { get; init; } = "";
    public TimeSpan SyncInterval { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxPageSize { get; init; } = 100;
}

public sealed class PostgresOptions
{
    public const string SectionName = "Postgres";
    [Required] public string ConnectionString { get; init; } = "";
}

public sealed class QdrantOptions
{
    public const string SectionName = "Qdrant";
    [Required] public string BaseUrl { get; init; } = "";
    [Required] public string CollectionName { get; init; } = "discord-messages";
}

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";
    public OllamaEndpointOptions Embedding { get; init; } = new();
    public OllamaEndpointOptions Tagging   { get; init; } = new();
}

public sealed class OllamaEndpointOptions
{
    [Required] public string BaseUrl { get; init; } = "";
    [Required] public string Model   { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 120;
}

public sealed class EnrichmentOptions
{
    public const string SectionName = "Enrichment";
    public int Version { get; init; } = 1;          // bump to force re-enrich
    public int BatchSize { get; init; } = 50;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);
}
```

`appsettings.Development.json` points Ollama at `http://host.docker.internal:11434` with `nomic-embed-text` for embeddings and `llama3.1:8b` for tagging (owner runs these locally with GPU). Cluster appsettings swap `BaseUrl` to `https://llm.mallcop.dev` and model to a 14B CPU-friendly model.

## docker-compose.yml (local dev)

```yaml
services:
  postgres:
    image: postgres:17-alpine
    environment:
      POSTGRES_USER: discord
      POSTGRES_PASSWORD: discord
      POSTGRES_DB: discord_scraper
    ports: ["5432:5432"]
    volumes: [pgdata:/var/lib/postgresql/data]

  qdrant:
    image: qdrant/qdrant:latest
    ports: ["6333:6333"]
    volumes: [qdrantdata:/qdrant/storage]

  ingester:
    build: .
    environment:
      Discord__BotToken: ${DISCORD_BOT_TOKEN}
      Postgres__ConnectionString: "Host=postgres;Port=5432;Database=discord_scraper;Username=discord;Password=discord"
      Qdrant__BaseUrl: "http://qdrant:6333"
      Qdrant__CollectionName: "discord-messages"
      Ollama__Embedding__BaseUrl: "http://host.docker.internal:11434"
      Ollama__Embedding__Model: "nomic-embed-text"
      Ollama__Tagging__BaseUrl: "http://host.docker.internal:11434"
      Ollama__Tagging__Model: "llama3.1:8b"
    depends_on: [postgres, qdrant]

  api:
    build: .
    command: ["dotnet", "DiscordScraper.Api.dll"]
    environment: # same as ingester
    ports: ["5000:5000"]
    depends_on: [postgres, qdrant]

volumes:
  pgdata:
  qdrantdata:
```

Ollama stays on the host (GPU passthrough). Containers reach it via `host.docker.internal`.

## Discord bot setup (owner's responsibility, flag if not done)

Owner must have (or create) a Discord application in the developer portal with:
- Bot user created
- **Message Content Intent** enabled (privileged, but doesn't require review until 100+ servers)
- Permissions: `View Channels`, `Read Message History` only — no write perms
- Invited to the owner's servers via OAuth URL with `bot` scope
- Bot token in `DISCORD_BOT_TOKEN` env var

If the bot doesn't exist yet when you start, stop and ask — this is step zero and takes 5 minutes in the web UI.

## Build order (suggested)

1. **Solution skeleton + `Directory.Build.props` + `Directory.Packages.props`.** Empty projects with correct references.
2. **`DiscordScraper.Storage`**: `DbContext`, entity classes, initial migration that creates all tables and views. Get `dotnet ef database update` working against local Postgres before touching anything else.
3. **`DiscordScraper.Discord`**: REST client with pagination and 429 handling. Test against a real server with a small fetch limit. No storage yet — just proves auth and pagination.
4. **`DiscordScraper.Ingestion` — sync worker**: wire Discord client + raw_messages insert. Single channel, bounded fetch, idempotent. Verify rows land in `raw_messages`.
5. **Projection worker**: `raw_messages` → `messages`. Mention resolution, thread detection, `ON CONFLICT DO UPDATE`. Unit tests on known fixtures from captured JSONB.
6. **Enrichment worker**: Ollama tagging + embedding + Qdrant writes. This is the slowest to iterate — keep the batch size small until you're happy with the prompts.
7. **`DiscordScraper.Api`**: health check, `/admin/stats`, `/admin/backfill?channel=X&since=Y`, SSE progress endpoint.
8. **Dockerfile** (copy from resume-chat, adjust project names). Tests run during build.
9. **docker-compose up** end-to-end on one small test server.

## Open decisions (resolve with owner before that step)

- **Guild member cache for mention resolution.** The Discord REST `/guilds/{id}/members` endpoint requires the **Guild Members intent** (privileged). Without it, we can only resolve mentions for users who have actually posted. Decision: (a) enable Guild Members intent and maintain a `raw_members` table, or (b) resolve mentions opportunistically from observed authors only, falling back to `@user_123`. Owner's call; (b) is simpler and probably fine.
- **Attachment handling.** Storing Discord's CDN URLs is free but those URLs expire (Discord rotates them ~24h now). Options: (i) store the URL anyway, accept that old ones 404, (ii) proactively download attachments to local disk / MinIO, (iii) ignore. Recommend (i) for v1.
- **Which guilds to ingest.** Ingest every guild the bot is in, or an allowlist from config? Recommend allowlist — the bot might end up in guilds the owner doesn't want scraped.
- **Qdrant collection dimensions.** `nomic-embed-text` is 768. Bake into collection creation migration.
- **Initial backfill bounds.** First run on a busy server could be millions of messages. Add a config `BackfillSinceUtc` so the first sync isn't unbounded.

## Reference resources on disk

- `/home/insta/src/bryanboettcher/resume-chat` — canonical .NET 10 RAG reference. Copy conventions from here, don't reinvent. Specifically useful:
  - `backend/src/ResumeChat.Api/Extensions/WebApplicationBuilderExtensions.cs` — DI wire-up pattern
  - `backend/src/ResumeChat.Api/Endpoints/*Endpoints.cs` — `MapTo(IEndpointRouteBuilder)` pattern
  - `backend/src/ResumeChat.Rag/Embedding/OllamaEmbeddingProvider.cs` — Ollama HTTP client shape
  - `backend/src/ResumeChat.Rag/VectorStore/QdrantVectorStore.cs` — Qdrant REST client, SHA256 point IDs
  - `backend/src/ResumeChat.Storage/ResumeChatDbContext.cs` — EF fluent mapping, pooled factory
  - `backend/src/ResumeChat.ServiceDefaults/Extensions.cs` — OpenTelemetry setup
  - `backend/Dockerfile` — multi-stage with tests during build
  - `docker-compose.yml` — service layout
  - `CLAUDE.md` — comprehensive project conventions

## Working style for this owner

- Direct and terse. No "great question!" preamble.
- Push back on weak reasoning. Challenge bad decisions. The owner expects this.
- Prefer subagent delegation (Explore, code-explorer, etc.) for research over dumping tool output into your own context.
- Use `git -C /abs/path` instead of `cd`-ing.
- Don't add docs unless asked. Don't over-comment code.
- Owner is a deep C# backend engineer. Skip basic explanations of .NET concepts.
- Discussion-first for architectural choices, but once a decision is made, execute decisively.
