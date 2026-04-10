Sources:
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Storage/ResumeChatDbContext.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Storage/MigrationHostedService.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Storage/Entities/InteractionEntity.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Storage/Repositories/InteractionRepository.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Storage/Extensions/ServiceCollectionExtensions.cs`

## `src/ResumeChat.Storage/ResumeChatDbContext.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using ResumeChat.Storage.Entities;

namespace ResumeChat.Storage;

public sealed class ResumeChatDbContext(DbContextOptions<ResumeChatDbContext> options) : DbContext(options)
{
    public DbSet<InteractionEntity> Interactions => Set<InteractionEntity>();
    public DbSet<CorpusDocumentEntity> CorpusDocuments => Set<CorpusDocumentEntity>();
    public DbSet<CorpusChunkEntity> CorpusChunks => Set<CorpusChunkEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InteractionEntity>(entity =>
        {
            entity.ToTable("interactions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            entity.Property(e => e.OriginalQuery).HasColumnName("original_query");
            entity.Property(e => e.ProcessedQuery).HasColumnName("processed_query");
            entity.Property(e => e.ResponseText).HasColumnName("response_text");
            entity.Property(e => e.RetrievedDocuments).HasColumnName("retrieved_documents").HasColumnType("jsonb");
            entity.Property(e => e.RetrievalMs).HasColumnName("retrieval_ms");
            entity.Property(e => e.CompletionMs).HasColumnName("completion_ms");
            entity.Property(e => e.TotalMs).HasColumnName("total_ms");
            entity.Property(e => e.Provider).HasColumnName("provider");
            entity.Property(e => e.ModelName).HasColumnName("model_name");
            entity.Property(e => e.IsThreat).HasColumnName("is_threat");
            entity.Property(e => e.ThreatScore).HasColumnName("threat_score");
            entity.Property(e => e.QueryHash).HasColumnName("query_hash");
            entity.Property(e => e.CacheHit).HasColumnName("cache_hit");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");

            entity.HasIndex(e => e.QueryHash).HasDatabaseName("ix_interactions_query_hash");
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("ix_interactions_created_at");
        });

        modelBuilder.Entity<CorpusDocumentEntity>(entity =>
        {
            entity.ToTable("corpus_documents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            entity.Property(e => e.SourceFile).HasColumnName("source_file");
            entity.Property(e => e.Title).HasColumnName("title");
            entity.Property(e => e.ContentText).HasColumnName("content_text");
            entity.Property(e => e.ContentHash).HasColumnName("content_hash");
            entity.Property(e => e.Tags).HasColumnName("tags");
            entity.Property(e => e.LastModified).HasColumnName("last_modified");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            entity.HasIndex(e => e.SourceFile).IsUnique().HasDatabaseName("ix_corpus_documents_source_file");
            entity.HasIndex(e => e.ContentHash).HasDatabaseName("ix_corpus_documents_content_hash");
        });

        modelBuilder.Entity<CorpusChunkEntity>(entity =>
        {
            entity.ToTable("corpus_chunks");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            entity.Property(e => e.DocumentId).HasColumnName("document_id");
            entity.Property(e => e.ChunkIndex).HasColumnName("chunk_index");
            entity.Property(e => e.SectionHeading).HasColumnName("section_heading");
            entity.Property(e => e.ChunkText).HasColumnName("chunk_text");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            entity.HasOne(e => e.Document)
                  .WithMany(d => d.Chunks)
                  .HasForeignKey(e => e.DocumentId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.DocumentId, e.ChunkIndex })
                  .IsUnique()
                  .HasDatabaseName("ix_corpus_chunks_document_id_chunk_index");
        });
    }
}
```

## `src/ResumeChat.Storage/MigrationHostedService.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ResumeChat.Storage;

public sealed class MigrationHostedService : IHostedService
{
    private readonly IDbContextFactory<ResumeChatDbContext> _factory;
    private readonly ILogger<MigrationHostedService> _logger;

    public MigrationHostedService(
        IDbContextFactory<ResumeChatDbContext> factory,
        ILogger<MigrationHostedService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        const int maxRetries = 10;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await using var context = await _factory.CreateDbContextAsync(ct);
                _logger.LogInformation("Applying database schema (attempt {Attempt})...", attempt);
                await context.Database.EnsureCreatedAsync(ct);
                _logger.LogInformation("Database schema ready");
                return;
            }
            catch (Exception ex) when (attempt < maxRetries && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Database not ready (attempt {Attempt}/{MaxRetries}), retrying in 2s...",
                    attempt, maxRetries);
                await Task.Delay(2000, ct);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

## `src/ResumeChat.Storage/Entities/InteractionEntity.cs`

```csharp
namespace ResumeChat.Storage.Entities;

public sealed class InteractionEntity
{
    public long Id { get; set; }
    public required string OriginalQuery { get; set; }
    public required string ProcessedQuery { get; set; }
    public required string ResponseText { get; set; }
    public required string RetrievedDocuments { get; set; } // JSON: [{source_file, section, score}]
    public double? RetrievalMs { get; set; }
    public double? CompletionMs { get; set; }
    public double? TotalMs { get; set; }
    public required string Provider { get; set; }
    public required string ModelName { get; set; }
    public bool IsThreat { get; set; }
    public int ThreatScore { get; set; }
    public required string QueryHash { get; set; }
    public bool CacheHit { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
}
```

## `src/ResumeChat.Storage/Repositories/InteractionRepository.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using ResumeChat.Storage.Entities;

namespace ResumeChat.Storage.Repositories;

internal sealed class InteractionRepository(IDbContextFactory<ResumeChatDbContext> contextFactory) : IInteractionRepository
{
    public async Task<InteractionEntity?> FindCachedResponseAsync(string queryHash, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Interactions
            .Where(i => i.QueryHash == queryHash && i.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task LogInteractionAsync(InteractionEntity interaction, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        context.Interactions.Add(interaction);
        await context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<InteractionEntity>> GetRecentAsync(int limit = 20, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Interactions
            .OrderByDescending(i => i.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<InteractionEntity?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Interactions
            .FirstOrDefaultAsync(i => i.Id == id, ct);
    }

    public async Task<IReadOnlyList<InteractionEntity>> SearchAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var lowerQuery = query.ToLowerInvariant();
        return await context.Interactions
            .Where(i => i.OriginalQuery.ToLower().Contains(lowerQuery)
                     || i.ResponseText.ToLower().Contains(lowerQuery))
            .OrderByDescending(i => i.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<bool> PurgeAsync(long id, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var rows = await context.Interactions
            .Where(i => i.Id == id)
            .ExecuteDeleteAsync(ct);

        return rows > 0;
    }

    public async Task<bool> ExpireAsync(long id, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var rows = await context.Interactions
            .Where(i => i.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.QueryHash, "")
                .SetProperty(i => i.ExpiresAt, DateTimeOffset.UtcNow),
            ct);

        return rows > 0;
    }
}
```

## `src/ResumeChat.Storage/Extensions/ServiceCollectionExtensions.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ResumeChat.Rag.Orchestration;
using ResumeChat.Storage.Options;
using ResumeChat.Storage.Orchestration;
using ResumeChat.Storage.Repositories;
using ResumeChat.Storage.Services;

namespace ResumeChat.Storage.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddResumeChatStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<PostgresOptions>()
            .BindConfiguration(PostgresOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<CacheOptions>()
            .BindConfiguration(CacheOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddPooledDbContextFactory<ResumeChatDbContext>((sp, optionsBuilder) =>
        {
            var postgres = sp.GetRequiredService<IOptions<PostgresOptions>>().Value;
            optionsBuilder.UseNpgsql(postgres.ConnectionString);
        });

        services.AddTransient<IInteractionRepository, InteractionRepository>();
        services.AddTransient<ICorpusRepository, CorpusRepository>();
        services.AddTransient<CorpusSyncService>();
        services.AddTransient<IChatOrchestrator, CachingChatOrchestrator>();

        services.AddHostedService<MigrationHostedService>();

        return services;
    }
}
```
