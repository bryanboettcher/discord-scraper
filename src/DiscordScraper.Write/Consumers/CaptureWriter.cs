using System.Text.Json;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Configuration;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Write.Consumers;

/// <summary>
/// Singleton that owns the JSONL capture file and serializes all concurrent writes through a
/// single <see cref="SemaphoreSlim"/>. Registered as a singleton so that multiple
/// <see cref="MessageCaptureConsumer"/> instances (one per <c>IConsumer&lt;T&gt;</c> binding
/// that MT creates per receive) share the same file handle and write exactly one header.
/// </summary>
public sealed class CaptureWriter(IOptions<CaptureOptions> options, ISystemClock clock)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly CaptureOptions _options = options.Value;
    private readonly string _scraperSha = ResolveScraperSha();

    private string? _filePath;
    private bool _headerWritten;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Appends a single body line (preceded by the header on first call) to the capture file.
    /// Must only be called when capture is enabled; callers are responsible for the guard.
    /// </summary>
    public async Task AppendAsync(string kind, object body, long guildId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = EnsureFile(guildId);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            await using var writer = new StreamWriter(stream);

            if (!_headerWritten)
            {
                var header = new
                {
                    kind = "header",
                    schemaVersion = CaptureSchemaVersion.Current,
                    capturedAt = clock.UtcNow,
                    sourceGuild = guildId.ToString(),
                    scraperSha = _scraperSha,
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(header, SerializerOptions));
                _headerWritten = true;
            }

            var record = new { kind, data = body };
            await writer.WriteLineAsync(JsonSerializer.Serialize(record, SerializerOptions));
        }
        finally
        {
            _gate.Release();
        }
    }

    private string EnsureFile(long guildId)
    {
        if (_filePath is not null)
            return _filePath;

        Directory.CreateDirectory(_options.OutputPath);
        var timestamp = clock.UtcNow.ToString("yyyyMMddTHHmmss");
        _filePath = Path.Combine(_options.OutputPath, $"{guildId}_{timestamp}.jsonl");
        return _filePath;
    }

    private static string ResolveScraperSha()
        => Environment.GetEnvironmentVariable("SCRAPER_SHA")
           ?? Environment.GetEnvironmentVariable("GIT_COMMIT_SHA")
           ?? "dev";
}
