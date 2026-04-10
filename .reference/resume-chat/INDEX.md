Verbatim references from `/home/insta/src/bryanboettcher/resume/backend`. Do not edit; regenerate by re-reading source.

- `01-solution-layout.md` — `.slnx`, project graph, all `src/` `.csproj` files, one test `.csproj`, root config file presence.
- `02-service-defaults.md` — `ResumeChat.ServiceDefaults` project and `Extensions.cs` (OTel, health checks, resilience, service discovery).
- `03-api-host-wireup.md` — API host `Program.cs`, `WebApplicationBuilderExtensions`, `ServiceCollectionExtensions`, `Endpoints/WebApplicationExtensions`.
- `04-options-pattern.md` — options classes from API/Rag/Storage plus registration call sites using `BindConfiguration` + `ValidateDataAnnotations` + `ValidateOnStart`.
- `05-endpoints-pattern.md` — `ChatEndpoints`, `IngestionEndpoints`, and the `MapTo` mount call site.
- `06-storage-efcore.md` — `ResumeChatDbContext`, `MigrationHostedService`, `InteractionEntity`, `InteractionRepository`, Storage DI registration.
- `07-ollama-http-client.md` — `OllamaEmbeddingProvider` and `OllamaResponseProvider` with request/response DTOs.
- `08-qdrant-vector-store.md` — `QdrantVectorStore` with `EnsureCollectionAsync` bootstrap and search/upsert.
- `09-dockerfile-and-compose.md` — backend `Dockerfile` (tests run in build stage) and repo-root `docker-compose.yml`.
- `10-test-conventions.md` — `ResumeChat.Storage.Tests.csproj` and `CachingChatOrchestrator_ProcessChatAsync.cs` showing NUnit + Shouldly + NSubstitute with nested `When_*` classes.
- `11-diagnostics-and-logging.md` — `RagDiagnostics.cs` ActivitySource/Meter and the OTel registration call site (`ConfigureRagTelemetry`).
- `12-claude-md.md` — repo-root `CLAUDE.md`.
