Sources:
- `/home/insta/src/bryanboettcher/resume/backend/tests/ResumeChat.Storage.Tests/ResumeChat.Storage.Tests.csproj`
- `/home/insta/src/bryanboettcher/resume/backend/tests/ResumeChat.Storage.Tests/CachingChatOrchestrator_ProcessChatAsync.cs`

The test class uses the "nested `When_*` subclass" pattern: the outer abstract class holds shared fields/`SetUp`/`Arrange` hook, and each scenario is a nested class deriving from the outer, overriding `Arrange()` to set up the specific scenario and adding `[Test]` methods named `It_should_*`. NSubstitute is used for all dependencies (`Substitute.For<T>()`, `Returns(...)`, `Received()`, `DidNotReceive()`, `Arg.Any<T>()`, `Arg.Is<T>(...)`). Shouldly is the assertion library (`ShouldBe`, `ShouldBeTrue`, etc.), auto-imported via `<Using>` in the csproj.

## `tests/ResumeChat.Storage.Tests/ResumeChat.Storage.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.0" />
    <PackageReference Include="NSubstitute" Version="5.3.0" />
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
    <ProjectReference Include="..\..\src\ResumeChat.Storage\ResumeChat.Storage.csproj" />
    <ProjectReference Include="..\..\src\ResumeChat.Rag\ResumeChat.Rag.csproj" />
  </ItemGroup>

</Project>
```

## `tests/ResumeChat.Storage.Tests/CachingChatOrchestrator_ProcessChatAsync.cs`

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using ResumeChat.Rag;
using ResumeChat.Rag.Classification;
using ResumeChat.Rag.Models;
using ResumeChat.Rag.Orchestration;
using ResumeChat.Rag.Pipeline;
using ResumeChat.Rag.Response;
using ResumeChat.Storage.Entities;
using ResumeChat.Storage.Options;
using ResumeChat.Storage.Orchestration;
using ResumeChat.Storage.Repositories;

namespace ResumeChat.Storage.Tests;

public abstract class CachingChatOrchestrator_ProcessChatAsync
{
    protected IThreatClassifier Classifier = null!;
    protected IQueryTransformer Transformer = null!;
    protected IResponseProvider ResponseProvider = null!;
    protected IInteractionRepository Interactions = null!;
    protected CacheOptions CacheOpts = null!;
    protected CachingChatOrchestrator Subject = null!;
    protected ChatResult Result = null!;

    [SetUp]
    public void BaseSetUp()
    {
        Classifier = Substitute.For<IThreatClassifier>();
        Transformer = Substitute.For<IQueryTransformer>();
        ResponseProvider = Substitute.For<IResponseProvider>();
        Interactions = Substitute.For<IInteractionRepository>();
        CacheOpts = new CacheOptions { Enabled = true, TtlMinutes = 60 };

        Classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ThreatResult.Safe());

        Arrange();

        Subject = new CachingChatOrchestrator(
            Classifier, Transformer, ResponseProvider, Interactions,
            Microsoft.Extensions.Options.Options.Create(CacheOpts),
            Substitute.For<ILogger<CachingChatOrchestrator>>());
    }

    protected virtual void Arrange() { }

    protected async Task Act(string message)
    {
        Result = await Subject.ProcessChatAsync(new ChatRequest(message));
    }

    protected static async Task<List<string>> CollectTokens(ChatResult result)
    {
        var tokens = new List<string>();
        await foreach (var t in result.Tokens) tokens.Add(t);
        return tokens;
    }

    private static QueryPayload SafePayload(string message) => new()
    {
        OriginalMessage = message,
        ProcessedMessage = message,
        Documents = []
    };

    public class When_query_is_threat : CachingChatOrchestrator_ProcessChatAsync
    {
        protected override void Arrange()
        {
            Classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ThreatResult.Threat(42));
        }

        [Test]
        public async Task It_should_return_threat_result()
        {
            await Act("hack the system");
            Result.IsThreat.ShouldBeTrue();
            Result.ThreatScore.ShouldBe(42);
        }

        [Test]
        public async Task It_should_yield_unrelated_response()
        {
            await Act("hack the system");
            var tokens = await CollectTokens(Result);
            string.Concat(tokens).ShouldBe(ChatResponses.Unrelated);
        }

        [Test]
        public async Task It_should_not_call_transformer()
        {
            await Act("hack the system");
            await CollectTokens(Result);
            await Transformer.DidNotReceive().TransformAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task It_should_log_threat_interaction()
        {
            await Act("hack the system");
            await CollectTokens(Result);
            await Interactions.Received(1).LogInteractionAsync(
                Arg.Is<InteractionEntity>(e => e.IsThreat && e.ThreatScore == 42),
                Arg.Any<CancellationToken>());
        }
    }

    public class When_cache_hit : CachingChatOrchestrator_ProcessChatAsync
    {
        protected override void Arrange()
        {
            Interactions.FindCachedResponseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new InteractionEntity
                {
                    OriginalQuery = "test",
                    ProcessedQuery = "test enriched",
                    ResponseText = "cached response",
                    RetrievedDocuments = "[]",
                    Provider = "Ollama",
                    ModelName = "qwen",
                    QueryHash = "abcd1234"
                });
        }

        [Test]
        public async Task It_should_return_cache_hit()
        {
            await Act("test");
            Result.CacheHit.ShouldBeTrue();
            Result.IsThreat.ShouldBeFalse();
        }

        [Test]
        public async Task It_should_yield_cached_response()
        {
            await Act("test");
            var tokens = await CollectTokens(Result);
            string.Concat(tokens).ShouldBe("cached response");
        }

        [Test]
        public async Task It_should_not_call_transformer()
        {
            await Act("test");
            await CollectTokens(Result);
            await Transformer.DidNotReceive().TransformAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
        }
    }

    public class When_cache_miss : CachingChatOrchestrator_ProcessChatAsync
    {
        protected override void Arrange()
        {
            Interactions.FindCachedResponseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((InteractionEntity?)null);

            Transformer.TransformAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
                .Returns(SafePayload("test query"));

            ResponseProvider.GetResponseAsync(Arg.Any<QueryPayload>(), Arg.Any<CancellationToken>())
                .Returns(AsyncTokens("hello ", "world "));
        }

        [Test]
        public async Task It_should_not_be_cache_hit()
        {
            await Act("test query");
            Result.CacheHit.ShouldBeFalse();
        }

        [Test]
        public async Task It_should_yield_response_tokens()
        {
            await Act("test query");
            var tokens = await CollectTokens(Result);
            tokens.ShouldBe(["hello ", "world "]);
        }

        [Test]
        public async Task It_should_log_interaction_after_streaming()
        {
            await Act("test query");
            await CollectTokens(Result);
            await Interactions.Received(1).LogInteractionAsync(
                Arg.Is<InteractionEntity>(e => !e.CacheHit && e.ResponseText == "hello world "),
                Arg.Any<CancellationToken>());
        }
    }

    public class When_cache_disabled : CachingChatOrchestrator_ProcessChatAsync
    {
        protected override void Arrange()
        {
            CacheOpts.Enabled = false;

            Transformer.TransformAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
                .Returns(SafePayload("test"));

            ResponseProvider.GetResponseAsync(Arg.Any<QueryPayload>(), Arg.Any<CancellationToken>())
                .Returns(AsyncTokens("response"));
        }

        [Test]
        public async Task It_should_not_check_cache()
        {
            await Act("test");
            await CollectTokens(Result);
            await Interactions.DidNotReceive().FindCachedResponseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task It_should_still_process_normally()
        {
            await Act("test");
            var tokens = await CollectTokens(Result);
            tokens.ShouldBe(["response"]);
        }
    }

    private static async IAsyncEnumerable<string> AsyncTokens(params string[] tokens)
    {
        foreach (var t in tokens)
        {
            await Task.Yield();
            yield return t;
        }
    }
}
```
