namespace DiscordScraper.Write.Consumers;

internal static class TaskExt
{
    /// <summary>
    /// Awaits two heterogeneous tasks concurrently and returns their results as a tuple.
    /// Equivalent to Task.WhenAll but preserves strong typing without allocating an array.
    /// </summary>
    public static async Task<(T1, T2)> WhenAll<T1, T2>(Task<T1> a, Task<T2> b)
    {
        await Task.WhenAll(a, b);
        return (a.Result, b.Result);
    }
}
