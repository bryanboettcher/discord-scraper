FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first for layer caching
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY DiscordScraper.slnx ./

COPY src/DiscordScraper.Contracts/DiscordScraper.Contracts.csproj         src/DiscordScraper.Contracts/
COPY src/DiscordScraper.Core/DiscordScraper.Core.csproj                   src/DiscordScraper.Core/
COPY src/DiscordScraper.Discord/DiscordScraper.Discord.csproj             src/DiscordScraper.Discord/
COPY src/DiscordScraper.Rendering/DiscordScraper.Rendering.csproj         src/DiscordScraper.Rendering/
COPY src/DiscordScraper.Write/DiscordScraper.Write.csproj                 src/DiscordScraper.Write/
COPY src/DiscordScraper.MessageEnhancement/DiscordScraper.MessageEnhancement.csproj src/DiscordScraper.MessageEnhancement/
COPY src/DiscordScraper.Read/DiscordScraper.Read.csproj                   src/DiscordScraper.Read/
COPY src/DiscordScraper.ServiceDefaults/DiscordScraper.ServiceDefaults.csproj src/DiscordScraper.ServiceDefaults/
COPY src/DiscordScraper.Ingester/DiscordScraper.Ingester.csproj           src/DiscordScraper.Ingester/
COPY src/DiscordScraper.Api/DiscordScraper.Api.csproj                     src/DiscordScraper.Api/

COPY tests/DiscordScraper.Contracts.Tests/DiscordScraper.Contracts.Tests.csproj                 tests/DiscordScraper.Contracts.Tests/
COPY tests/DiscordScraper.Rendering.Tests/DiscordScraper.Rendering.Tests.csproj                 tests/DiscordScraper.Rendering.Tests/
COPY tests/DiscordScraper.Write.Tests/DiscordScraper.Write.Tests.csproj                         tests/DiscordScraper.Write.Tests/
COPY tests/DiscordScraper.MessageEnhancement.Tests/DiscordScraper.MessageEnhancement.Tests.csproj tests/DiscordScraper.MessageEnhancement.Tests/
COPY tests/DiscordScraper.Read.Tests/DiscordScraper.Read.Tests.csproj                           tests/DiscordScraper.Read.Tests/
COPY tests/DiscordScraper.Api.Tests/DiscordScraper.Api.Tests.csproj                             tests/DiscordScraper.Api.Tests/

# Single restore via the solution file pulls everything.
RUN dotnet restore DiscordScraper.slnx

# Copy remaining source
COPY src/ src/
COPY tests/ tests/

# Tests run during build — failure fails the image. Solution-file run picks up all 6 test projects.
RUN dotnet test DiscordScraper.slnx --no-restore --verbosity quiet

# Publish both runnable hosts to distinct output dirs
RUN dotnet publish src/DiscordScraper.Api      -c Release -o /app/api      --no-restore
RUN dotnet publish src/DiscordScraper.Ingester -c Release -o /app/ingester --no-restore


FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /app/api .
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
ENTRYPOINT ["dotnet", "DiscordScraper.Api.dll"]


FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS ingester
# ASP.NET Core runtime — Ingester transitively pulls AspNetCore via ServiceDefaults
# (OpenTelemetry.Instrumentation.AspNetCore, Microsoft.Extensions.Http.Resilience).
WORKDIR /app
COPY --from=build /app/ingester .
ENTRYPOINT ["dotnet", "DiscordScraper.Ingester.dll"]
