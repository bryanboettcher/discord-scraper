namespace DiscordScraper.TestSupport.Stubs;

using Bogus;
using DiscordScraper.Enrichment.Ollama;

/// <summary>
/// Abstract base for output generators that produce values when a call succeeds.
/// Generators are the "what" — the output type and how to compute it.
/// </summary>
public abstract record OutputGenerator<TIn, TOut>
{
    public abstract TOut Generate(TIn input);

    /// <summary>
    /// Fixed output for every call, independent of input.
    /// </summary>
    public sealed record Fixed(TOut Value) : OutputGenerator<TIn, TOut>
    {
        public override TOut Generate(TIn _) => Value;
    }

    /// <summary>
    /// Output computed from the input. Canonical default for fixture-driven tests:
    /// same input produces the same output, run after run, enabling bit-identical
    /// regression testing of downstream projections.
    /// </summary>
    public sealed record FromInput(Func<TIn, TOut> Selector) : OutputGenerator<TIn, TOut>
    {
        public override TOut Generate(TIn input) => Selector(input);
    }

    /// <summary>
    /// Output cycled from a fixed sequence of values, in order.
    /// Resets to the start when the sequence is exhausted.
    /// </summary>
    public sealed record Sequence(IReadOnlyList<TOut> Values) : OutputGenerator<TIn, TOut>
    {
        private int _index;

        public override TOut Generate(TIn _)
        {
            if (Values.Count == 0)
                throw new InvalidOperationException("Sequence is empty");
            var result = Values[_index % Values.Count];
            Interlocked.Increment(ref _index);
            return result;
        }
    }

    /// <summary>
    /// Output generated via Bogus library with a fixed seed and builder function.
    /// Determinism requires a fixed seed; randomness is controlled by the builder.
    /// </summary>
    public sealed record Bogus(int Seed, Func<Faker, TIn, TOut> Builder) : OutputGenerator<TIn, TOut>
    {
        public override TOut Generate(TIn input) => Builder(new Faker { Random = new Randomizer(Seed) }, input);
    }
}

/// <summary>
/// Helpers for common deterministic output patterns.
/// </summary>
public static class OutputGeneratorHelpers
{
    /// <summary>
    /// Create a deterministic 768-dimensional embedding from a string input.
    /// Same input string produces bit-identical output across multiple calls.
    /// Uses stable hash-based distribution.
    /// </summary>
    public static OutputGenerator<string, ReadOnlyMemory<float>> DeterministicEmbedding(int dim = 768)
    {
        return new OutputGenerator<string, ReadOnlyMemory<float>>.FromInput(text =>
        {
            var hash = GetStableHash(text);
            var rng = new System.Random(hash);
            var floats = new float[dim];
            for (int i = 0; i < dim; i++)
            {
                floats[i] = (float)rng.NextDouble();
            }
            return new ReadOnlyMemory<float>(floats);
        });
    }

    /// <summary>
    /// Create a deterministic tag list from a string input.
    /// Same input string produces the same tag list across multiple calls.
    /// Generates stable tags derived from the input hash.
    /// </summary>
    public static OutputGenerator<string, TagResult> DeterministicTags(int count = 3)
    {
        if (count < 1)
            throw new ArgumentException($"count must be at least 1; got {count}");
        if (count > 10)
            throw new ArgumentException($"count must be at most 10; got {count}");

        return new OutputGenerator<string, TagResult>.FromInput(text =>
        {
            var hash = GetStableHash(text);
            var rng = new System.Random(hash);

            // Generate stable tag names based on hash
            var tagPool = new[]
            {
                "general", "discussion", "feedback", "question", "idea", "bug", "feature",
                "documentation", "help", "announcement", "reminder", "summary", "review"
            };

            var selectedTags = new List<string>();
            var used = new HashSet<int>();

            for (int i = 0; i < count; i++)
            {
                int idx;
                do
                {
                    idx = rng.Next(tagPool.Length);
                } while (used.Contains(idx) && selectedTags.Count < tagPool.Length);

                if (!used.Contains(idx))
                {
                    used.Add(idx);
                    selectedTags.Add(tagPool[idx]);
                }
            }

            var isSubstantive = selectedTags.Count > 0;
            return new TagResult(selectedTags, isSubstantive);
        });
    }

    /// <summary>
    /// Compute a stable 32-bit hash from a string. Uses the same logic
    /// across calls to ensure determinism.
    /// </summary>
    private static int GetStableHash(string text)
    {
        unchecked
        {
            int hash = 17;
            foreach (char c in text)
            {
                hash = hash * 31 + c.GetHashCode();
            }
            return hash;
        }
    }
}
