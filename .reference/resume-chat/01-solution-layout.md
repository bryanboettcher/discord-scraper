Sources:
- `/home/insta/src/bryanboettcher/resume/backend/ResumeChat.slnx`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Api/ResumeChat.Api.csproj`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.AppHost/ResumeChat.AppHost.csproj`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Cli/ResumeChat.Cli.csproj`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Corpus.Cli/ResumeChat.Corpus.Cli.csproj`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Rag/ResumeChat.Rag.csproj`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.ServiceDefaults/ResumeChat.ServiceDefaults.csproj`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Storage/ResumeChat.Storage.csproj`
- `/home/insta/src/bryanboettcher/resume/backend/tests/ResumeChat.Rag.Tests/ResumeChat.Rag.Tests.csproj`

## Root config file presence (at `backend/`)

- `Directory.Build.props` — absent
- `Directory.Packages.props` — absent (no central package management; every `.csproj` pins its own versions)
- `global.json` — absent
- `.editorconfig` — absent
- `NuGet.config` — absent

## `backend/ResumeChat.slnx`

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/ResumeChat.Api/ResumeChat.Api.csproj" />
    <Project Path="src/ResumeChat.AppHost/ResumeChat.AppHost.csproj" />
    <Project Path="src/ResumeChat.Cli/ResumeChat.Cli.csproj" />
    <Project Path="src/ResumeChat.Corpus.Cli/ResumeChat.Corpus.Cli.csproj" />

    <Project Path="src/ResumeChat.Rag/ResumeChat.Rag.csproj" />
    <Project Path="src/ResumeChat.ServiceDefaults/ResumeChat.ServiceDefaults.csproj" />
    <Project Path="src/ResumeChat.Storage/ResumeChat.Storage.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/ResumeChat.Api.Tests/ResumeChat.Api.Tests.csproj" />
    <Project Path="tests/ResumeChat.Corpus.Cli.Tests/ResumeChat.Corpus.Cli.Tests.csproj" />
    <Project Path="tests/ResumeChat.Rag.Pipeline.Tests/ResumeChat.Rag.Pipeline.Tests.csproj" />
    <Project Path="tests/ResumeChat.Rag.Tests/ResumeChat.Rag.Tests.csproj" />
    <Project Path="tests/ResumeChat.Storage.Tests/ResumeChat.Storage.Tests.csproj" />
  </Folder>
</Solution>
```

## Project reference graph (src/)

- `ResumeChat.Api` → `ResumeChat.Rag`, `ResumeChat.ServiceDefaults`, `ResumeChat.Storage`
- `ResumeChat.AppHost` → `ResumeChat.Api`
- `ResumeChat.Cli` → `ResumeChat.Rag`
- `ResumeChat.Corpus.Cli` → (no project refs; standalone)
- `ResumeChat.Rag` → (no project refs; leaf)
- `ResumeChat.ServiceDefaults` → (no project refs; leaf)
- `ResumeChat.Storage` → `ResumeChat.Rag`

## `src/ResumeChat.Api/ResumeChat.Api.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ResumeChat.Rag\ResumeChat.Rag.csproj" />
    <ProjectReference Include="..\ResumeChat.ServiceDefaults\ResumeChat.ServiceDefaults.csproj" />
    <ProjectReference Include="..\ResumeChat.Storage\ResumeChat.Storage.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="FluentValidation.DependencyInjectionExtensions" Version="12.1.1" />
  </ItemGroup>

</Project>
```

## `src/ResumeChat.AppHost/ResumeChat.AppHost.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <Sdk Name="Aspire.AppHost.Sdk" Version="9.2.0" />

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsAspireHost>true</IsAspireHost>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Aspire.Hosting.AppHost" Version="9.2.0" />
    <PackageReference Include="Aspire.Hosting.PostgreSQL" Version="13.2.1" />
    <PackageReference Include="Aspire.Hosting.Qdrant" Version="9.2.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\ResumeChat.Api\ResumeChat.Api.csproj" />
  </ItemGroup>

</Project>
```

## `src/ResumeChat.Cli/ResumeChat.Cli.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\ResumeChat.Rag\ResumeChat.Rag.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.5" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.0.5" />
    <PackageReference Include="Microsoft.Extensions.Options.DataAnnotations" Version="10.0.5" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

## `src/ResumeChat.Corpus.Cli/ResumeChat.Corpus.Cli.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.5" />
    <PackageReference Include="Microsoft.Extensions.Options.DataAnnotations" Version="10.0.5" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.0.4" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="ResumeChat.Corpus.Cli.Tests" />
  </ItemGroup>

</Project>
```

## `src/ResumeChat.Rag/ResumeChat.Rag.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Anthropic" Version="12.11.0" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.0.5" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.5" />
    <PackageReference Include="Microsoft.Extensions.Options.DataAnnotations" Version="10.0.5" />
  </ItemGroup>

</Project>
```

## `src/ResumeChat.ServiceDefaults/ResumeChat.ServiceDefaults.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsAspireSharedProject>true</IsAspireSharedProject>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="9.6.0" />
    <PackageReference Include="Microsoft.Extensions.ServiceDiscovery" Version="9.2.0" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.12.0" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.12.0" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.12.0" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.12.0" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Runtime" Version="1.12.0" />
  </ItemGroup>

</Project>
```

## `src/ResumeChat.Storage/ResumeChat.Storage.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.4">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.0">
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.1" />
    <PackageReference Include="System.IO.Hashing" Version="10.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\ResumeChat.Rag\ResumeChat.Rag.csproj" />
  </ItemGroup>

</Project>
```

## Representative test `.csproj`: `tests/ResumeChat.Rag.Tests/ResumeChat.Rag.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="NUnit" Version="4.3.2" />
    <PackageReference Include="NUnit.Analyzers" Version="4.7.0" />
    <PackageReference Include="NUnit3TestAdapter" Version="5.0.0" />
    <PackageReference Include="Shouldly" Version="4.3.0" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="NUnit.Framework" />
    <Using Include="Shouldly" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\ResumeChat.Rag\ResumeChat.Rag.csproj" />
  </ItemGroup>

</Project>
```
