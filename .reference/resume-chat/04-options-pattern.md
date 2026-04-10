Sources:
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Api/Options/ApiKeyOptions.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Api/Options/RateLimitOptions.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Api/Options/CorpusOptions.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Rag/Embedding/OllamaEmbeddingOptions.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Rag/VectorStore/QdrantOptions.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Rag/Completion/CompletionSecurityOptions.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Storage/Options/PostgresOptions.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Storage/Options/CacheOptions.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Api/Extensions/WebApplicationBuilderExtensions.cs` (registration)
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Storage/Extensions/ServiceCollectionExtensions.cs` (registration)

Options classes pattern: `sealed` class, `public const string SectionName`, `[Required, MinLength(...)]` data-annotations, default values inline, properties mutable so the options binder can write them. Classes with no validation (pure defaults) omit annotations entirely but still define `SectionName`.

## `src/ResumeChat.Api/Options/ApiKeyOptions.cs`

```csharp
using System.ComponentModel.DataAnnotations;

namespace ResumeChat.Api.Options;

public sealed class ApiKeyOptions
{
    public const string SectionName = "ApiKey";

    [Required]
    [MinLength(1)]
    public string Key { get; set; } = string.Empty;
}
```

## `src/ResumeChat.Api/Options/RateLimitOptions.cs`

```csharp
namespace ResumeChat.Api.Options;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    public int PermitLimit { get; set; } = 10;
    public int WindowSeconds { get; set; } = 60;
}
```

## `src/ResumeChat.Api/Options/CorpusOptions.cs`

```csharp
using System.ComponentModel.DataAnnotations;

namespace ResumeChat.Api.Options;

public sealed class CorpusOptions
{
    public const string SectionName = "Corpus";

    [Required, MinLength(1)]
    public string Directory { get; set; } = string.Empty;
}
```

## `src/ResumeChat.Rag/Embedding/OllamaEmbeddingOptions.cs`

```csharp
using System.ComponentModel.DataAnnotations;

namespace ResumeChat.Rag.Embedding;

public sealed class OllamaEmbeddingOptions
{
    public const string SectionName = "Ollama:Embedding";

    [Required, MinLength(1)]
    public string BaseUrl { get; set; } = string.Empty;

    [Required, MinLength(1)]
    public string Model { get; set; } = "nomic-embed-text";
}
```

## `src/ResumeChat.Rag/VectorStore/QdrantOptions.cs`

```csharp
using System.ComponentModel.DataAnnotations;

namespace ResumeChat.Rag.VectorStore;

public sealed class QdrantOptions
{
    public const string SectionName = "Qdrant";

    [Required, MinLength(1)]
    public string BaseUrl { get; set; } = string.Empty;

    [Required, MinLength(1)]
    public string CollectionName { get; set; } = "resume-chunks";
}
```

## `src/ResumeChat.Rag/Completion/CompletionSecurityOptions.cs`

```csharp
using System.ComponentModel.DataAnnotations;

namespace ResumeChat.Rag.Completion;

public sealed class CompletionSecurityOptions
{
    public const string SectionName = "Security";

    [Required]
    [MinLength(16)]
    public string Canary { get; set; } = string.Empty;
}
```

## `src/ResumeChat.Storage/Options/PostgresOptions.cs`

```csharp
using System.ComponentModel.DataAnnotations;

namespace ResumeChat.Storage.Options;

public sealed class PostgresOptions
{
    public const string SectionName = "Postgres";

    [Required, MinLength(1)]
    public string ConnectionString { get; set; } = string.Empty;
}
```

## `src/ResumeChat.Storage/Options/CacheOptions.cs`

```csharp
namespace ResumeChat.Storage.Options;

public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    public bool Enabled { get; set; } = true;
    public int TtlMinutes { get; set; } = 1440; // 24 hours
}
```

## Registration call sites

From `src/ResumeChat.Api/Extensions/WebApplicationBuilderExtensions.cs`:

```csharp
builder.Services.AddOptions<ApiKeyOptions>()
    .BindConfiguration(ApiKeyOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ...

builder.Services.AddOptions<OllamaEmbeddingOptions>()
    .BindConfiguration(OllamaEmbeddingOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient<IEmbeddingProvider, OllamaEmbeddingProvider>();

builder.Services.AddOptions<QdrantOptions>()
    .BindConfiguration(QdrantOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient<IVectorStore, QdrantVectorStore>();

builder.Services.AddOptions<Api.Options.CorpusOptions>()
    .BindConfiguration(Api.Options.CorpusOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ...

builder.Services.AddOptions<CompletionSecurityOptions>()
    .BindConfiguration(CompletionSecurityOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<RetrievalOptions>()
    .BindConfiguration(RetrievalOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

From `src/ResumeChat.Storage/Extensions/ServiceCollectionExtensions.cs`:

```csharp
services.AddOptions<PostgresOptions>()
    .BindConfiguration(PostgresOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

services.AddOptions<CacheOptions>()
    .BindConfiguration(CacheOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

Note: `RateLimitOptions` is an exception — it is not registered via `AddOptions<T>().BindConfiguration(...)`. Instead it is read directly at startup inside `AddResumeChatRateLimiting`: `configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();` — because its values must be materialized synchronously to configure `AddFixedWindowLimiter` at DI-registration time.
