Sources:
- `/home/insta/src/bryanboettcher/resume/backend/Dockerfile`
- `/home/insta/src/bryanboettcher/resume/docker-compose.yml`

## `backend/Dockerfile`

Tests run inside the `build` stage (via `dotnet test ... --no-restore`) before `dotnet publish`. If any test project fails, the image build fails — this is the CI gate.

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/ResumeChat.Api/ResumeChat.Api.csproj src/ResumeChat.Api/
COPY src/ResumeChat.Rag/ResumeChat.Rag.csproj src/ResumeChat.Rag/
COPY src/ResumeChat.ServiceDefaults/ResumeChat.ServiceDefaults.csproj src/ResumeChat.ServiceDefaults/
COPY src/ResumeChat.Storage/ResumeChat.Storage.csproj src/ResumeChat.Storage/
COPY tests/ResumeChat.Api.Tests/ResumeChat.Api.Tests.csproj tests/ResumeChat.Api.Tests/
COPY tests/ResumeChat.Rag.Tests/ResumeChat.Rag.Tests.csproj tests/ResumeChat.Rag.Tests/
COPY tests/ResumeChat.Rag.Pipeline.Tests/ResumeChat.Rag.Pipeline.Tests.csproj tests/ResumeChat.Rag.Pipeline.Tests/
COPY tests/ResumeChat.Storage.Tests/ResumeChat.Storage.Tests.csproj tests/ResumeChat.Storage.Tests/
RUN dotnet restore src/ResumeChat.Api/ResumeChat.Api.csproj
RUN dotnet restore tests/ResumeChat.Api.Tests/ResumeChat.Api.Tests.csproj
RUN dotnet restore tests/ResumeChat.Rag.Tests/ResumeChat.Rag.Tests.csproj
RUN dotnet restore tests/ResumeChat.Rag.Pipeline.Tests/ResumeChat.Rag.Pipeline.Tests.csproj
RUN dotnet restore tests/ResumeChat.Storage.Tests/ResumeChat.Storage.Tests.csproj

COPY src/ResumeChat.Api/ src/ResumeChat.Api/
COPY src/ResumeChat.Rag/ src/ResumeChat.Rag/
COPY src/ResumeChat.ServiceDefaults/ src/ResumeChat.ServiceDefaults/
COPY src/ResumeChat.Storage/ src/ResumeChat.Storage/
COPY tests/ tests/
RUN dotnet test tests/ResumeChat.Api.Tests/ --no-restore --verbosity quiet
RUN dotnet test tests/ResumeChat.Rag.Tests/ --no-restore --verbosity quiet
RUN dotnet test tests/ResumeChat.Rag.Pipeline.Tests/ --no-restore --verbosity quiet
RUN dotnet test tests/ResumeChat.Storage.Tests/ --no-restore --verbosity quiet
RUN dotnet publish src/ResumeChat.Api -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["dotnet", "ResumeChat.Api.dll"]
```

## `docker-compose.yml` (repo root)

```yaml
services:
  aspire-dashboard:
    image: mcr.microsoft.com/dotnet/aspire-dashboard:9.2
    ports:
      - "18888:18888"
      - "18889:18889"
    environment:
      DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS: "true"

  qdrant:
    image: qdrant/qdrant:latest
    ports:
      - "6333:6333"
    volumes:
      - qdrant-data:/qdrant/storage

  postgres:
    image: postgres:17-alpine
    ports:
      - "5432:5432"
    environment:
      POSTGRES_DB: resumechat
      POSTGRES_USER: resumechat
      POSTGRES_PASSWORD: resumechat
    volumes:
      - pgdata:/var/lib/postgresql/data
      - ./postgres-init:/docker-entrypoint-initdb.d:ro

  api:
    build:
      context: backend
      dockerfile: Dockerfile
    ports:
      - "5000:5000"
    environment:
      ApiKey__Key: abc123
      Ollama__Embedding__BaseUrl: http://host.docker.internal:11434
      Ollama__Embedding__Model: nomic-embed-text
      Ollama__Response__BaseUrl: http://host.docker.internal:11434
      Ollama__Response__Model: qwen2.5-coder:7b
      Qdrant__BaseUrl: http://qdrant:6333
      Qdrant__CollectionName: resume-chunks
      Completion__Provider: ${COMPLETION_PROVIDER:-Ollama}
      Claude__ApiKey: ${CLAUDE_API_KEY:-}
      Claude__Model: ${CLAUDE_MODEL:-claude-sonnet-4-20250514}
      Security__Canary: local-dev-canary-token
      OTEL_EXPORTER_OTLP_ENDPOINT: http://aspire-dashboard:18889
      Corpus__Directory: /app/corpus
      Postgres__ConnectionString: Host=postgres;Port=5432;Database=resumechat;Username=resumechat;Password=resumechat
    volumes:
      - ./corpus:/app/corpus:ro
    extra_hosts:
      - "host.docker.internal:host-gateway"
    depends_on:
      - qdrant
      - postgres
      - aspire-dashboard

  frontend:
    image: caddy:2-alpine
    ports:
      - "8080:80"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - ./frontend:/srv/frontend:ro
    depends_on:
      - api

volumes:
  qdrant-data:
  pgdata:
```
