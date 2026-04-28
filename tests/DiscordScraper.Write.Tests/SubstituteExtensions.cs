namespace DiscordScraper.Write.Tests;

internal static class SubstituteExtensions
{
    /// <summary>Fluent helper: configure a substitute inline without a temp variable.</summary>
    public static T With<T>(this T substitute, Action<T> configure) where T : class
    {
        configure(substitute);
        return substitute;
    }
}
