namespace DiscordScraper.Ingester.SmokeTest;

/// <summary>
/// Singleton TCS bridge between PingConsumer (sets result) and BusSmokeTestService (awaits it).
/// Registered as singleton so both sides share the same instance.
/// </summary>
internal sealed class BusSmokeTestCompletionSource : TaskCompletionSource<bool>
{
    public BusSmokeTestCompletionSource() : base(TaskCreationOptions.RunContinuationsAsynchronously) { }
}
