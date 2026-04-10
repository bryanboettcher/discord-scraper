FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first for layer caching
COPY src/DiscordScraper.Core/DiscordScraper.Core.csproj src/DiscordScraper.Core/
COPY src/DiscordScraper.Discord/DiscordScraper.Discord.csproj src/DiscordScraper.Discord/
COPY src/DiscordScraper.Storage/DiscordScraper.Storage.csproj src/DiscordScraper.Storage/
COPY src/DiscordScraper.Ingestion/DiscordScraper.Ingestion.csproj src/DiscordScraper.Ingestion/
COPY src/DiscordScraper.ServiceDefaults/DiscordScraper.ServiceDefaults.csproj src/DiscordScraper.ServiceDefaults/
COPY src/DiscordScraper.Ingester/DiscordScraper.Ingester.csproj src/DiscordScraper.Ingester/
COPY src/DiscordScraper.Api/DiscordScraper.Api.csproj src/DiscordScraper.Api/
COPY src/DiscordScraper.Cli/DiscordScraper.Cli.csproj src/DiscordScraper.Cli/
COPY tests/DiscordScraper.Discord.Tests/DiscordScraper.Discord.Tests.csproj tests/DiscordScraper.Discord.Tests/
COPY tests/DiscordScraper.Storage.Tests/DiscordScraper.Storage.Tests.csproj tests/DiscordScraper.Storage.Tests/
COPY tests/DiscordScraper.Ingestion.Tests/DiscordScraper.Ingestion.Tests.csproj tests/DiscordScraper.Ingestion.Tests/
COPY tests/DiscordScraper.Ingester.Tests/DiscordScraper.Ingester.Tests.csproj tests/DiscordScraper.Ingester.Tests/

RUN dotnet restore src/DiscordScraper.Api/DiscordScraper.Api.csproj
RUN dotnet restore src/DiscordScraper.Ingester/DiscordScraper.Ingester.csproj
RUN dotnet restore src/DiscordScraper.Cli/DiscordScraper.Cli.csproj
RUN dotnet restore tests/DiscordScraper.Discord.Tests/DiscordScraper.Discord.Tests.csproj
RUN dotnet restore tests/DiscordScraper.Storage.Tests/DiscordScraper.Storage.Tests.csproj
RUN dotnet restore tests/DiscordScraper.Ingestion.Tests/DiscordScraper.Ingestion.Tests.csproj
RUN dotnet restore tests/DiscordScraper.Ingester.Tests/DiscordScraper.Ingester.Tests.csproj

# Copy remaining source
COPY src/ src/
COPY tests/ tests/

# Tests run during build — failure fails the image
RUN dotnet test tests/DiscordScraper.Discord.Tests/    --no-restore --verbosity quiet
RUN dotnet test tests/DiscordScraper.Storage.Tests/    --no-restore --verbosity quiet
RUN dotnet test tests/DiscordScraper.Ingestion.Tests/  --no-restore --verbosity quiet
RUN dotnet test tests/DiscordScraper.Ingester.Tests/   --no-restore --verbosity quiet

# Publish both runnable hosts to distinct output dirs
RUN dotnet publish src/DiscordScraper.Api      -c Release -o /app/api      --no-restore
RUN dotnet publish src/DiscordScraper.Ingester -c Release -o /app/ingester --no-restore


FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /app/api .
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
ENTRYPOINT ["dotnet", "DiscordScraper.Api.dll"]


FROM mcr.microsoft.com/dotnet/runtime:10.0 AS ingester
WORKDIR /app
COPY --from=build /app/ingester .
ENTRYPOINT ["dotnet", "DiscordScraper.Ingester.dll"]
