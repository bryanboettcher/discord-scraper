using DiscordScraper.Discord.Extensions;
using DiscordScraper.Ingestion.Extensions;
using DiscordScraper.Storage.Extensions;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddDiscordScraperStorage();
builder.Services.AddDiscordScraperMigrations();
builder.Services.AddDiscordClient();
builder.Services.AddDiscordIngestion();

var host = builder.Build();
host.Run();
