using System.Net;
using System.Text.Json;

namespace DiscordScraper.Api.Tests;

[TestFixture]
public sealed class OpenApiTests
{
    private ApiTestFactory _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new ApiTestFactory();
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task OpenApi_document_returns_200()
    {
        var response = await _client.GetAsync("/openapi/v1.json");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task OpenApi_document_is_valid_json()
    {
        var response = await _client.GetAsync("/openapi/v1.json");
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Test]
    public async Task OpenApi_document_contains_api_paths()
    {
        var response = await _client.GetAsync("/openapi/v1.json");
        var json = await response.Content.ReadAsStringAsync();
        json.ShouldContain("/api/messages/search");
        json.ShouldContain("/api/messages/");
    }
}
