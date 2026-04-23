using System.Net.Http.Headers;
using DiscordScraper.Discord.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Discord.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Discord REST client, its options, and the typed
    /// <see cref="HttpClient"/> with Bot authentication and the Discord-
    /// required User-Agent. Must be called on a host that has already bound
    /// <see cref="DiscordOptions.SectionName"/> to valid configuration.
    /// </summary>
    public static IServiceCollection AddDiscordClient(this IServiceCollection services)
    {
        services.AddOptions<DiscordOptions>()
            .BindConfiguration(DiscordOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<IDiscordClient, DiscordClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<DiscordOptions>>().Value;
            http.BaseAddress = new Uri("https://discord.com/api/v10/");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", options.BotToken);
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        return services;
    }
}
