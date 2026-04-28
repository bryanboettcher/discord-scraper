using DiscordScraper.Write.Pins;

namespace DiscordScraper.Write.Tests.Pins;

[TestFixture]
public sealed class PinSetCanonicalizerTests
{
    private static readonly DateTimeOffset T1 = new(2024, 3, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2024, 3, 2, 15, 30, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------------------
    // Empty pin set produces a stable (non-null, non-empty) hash
    // ---------------------------------------------------------------------------

    [Test]
    public void EmptyPinSet_ProducesStableHash()
    {
        var hash1 = PinSetCanonicalizer.Canonicalize([]);
        var hash2 = PinSetCanonicalizer.Canonicalize([]);

        hash1.ShouldNotBeNullOrEmpty();
        hash1.ShouldBe(hash2);
    }

    // ---------------------------------------------------------------------------
    // Single pin produces a stable hash
    // ---------------------------------------------------------------------------

    [Test]
    public void SinglePin_ProducesStableHash()
    {
        var pin = new PinSnapshot(100L, null);
        var hash1 = PinSetCanonicalizer.Canonicalize([pin]);
        var hash2 = PinSetCanonicalizer.Canonicalize([pin]);

        hash1.ShouldNotBeNullOrEmpty();
        hash1.ShouldBe(hash2);
    }

    // ---------------------------------------------------------------------------
    // Order independence: two pins in different orderings produce the same hash
    // ---------------------------------------------------------------------------

    [Test]
    public void TwoPins_DifferentOrderings_ProduceSameHash()
    {
        var pinA = new PinSnapshot(100L, null);
        var pinB = new PinSnapshot(200L, T1);

        var hash1 = PinSetCanonicalizer.Canonicalize([pinA, pinB]);
        var hash2 = PinSetCanonicalizer.Canonicalize([pinB, pinA]);

        hash1.ShouldBe(hash2);
    }

    // ---------------------------------------------------------------------------
    // EditedAt sensitivity: changing one pin's EditedAt changes the hash
    // ---------------------------------------------------------------------------

    [Test]
    public void DifferentEditedAt_ProducesDifferentHash()
    {
        var setA = new[] { new PinSnapshot(100L, T1), new PinSnapshot(200L, null) };
        var setB = new[] { new PinSnapshot(100L, T2), new PinSnapshot(200L, null) };

        PinSetCanonicalizer.Canonicalize(setA)
            .ShouldNotBe(PinSetCanonicalizer.Canonicalize(setB));
    }

    // ---------------------------------------------------------------------------
    // Pin removal: removing a pin changes the hash
    // ---------------------------------------------------------------------------

    [Test]
    public void PinRemoved_ProducesDifferentHash()
    {
        var full = new[] { new PinSnapshot(100L, null), new PinSnapshot(200L, T1) };
        var shrunk = new[] { new PinSnapshot(100L, null) };

        PinSetCanonicalizer.Canonicalize(full)
            .ShouldNotBe(PinSetCanonicalizer.Canonicalize(shrunk));
    }

    // ---------------------------------------------------------------------------
    // Pin addition: adding a pin changes the hash
    // ---------------------------------------------------------------------------

    [Test]
    public void PinAdded_ProducesDifferentHash()
    {
        var before = new[] { new PinSnapshot(100L, null) };
        var after = new[] { new PinSnapshot(100L, null), new PinSnapshot(300L, null) };

        PinSetCanonicalizer.Canonicalize(before)
            .ShouldNotBe(PinSetCanonicalizer.Canonicalize(after));
    }

    // ---------------------------------------------------------------------------
    // Timezone normalization: same instant in different offset representations
    // produces the same hash
    // ---------------------------------------------------------------------------

    [Test]
    public void SameInstantDifferentOffsets_ProduceSameHash()
    {
        // 2024-03-01 10:00:00+00:00 == 2024-03-01 11:00:00+01:00
        var utc = new DateTimeOffset(2024, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var plus1 = new DateTimeOffset(2024, 3, 1, 11, 0, 0, TimeSpan.FromHours(1));

        var hashUtc = PinSetCanonicalizer.Canonicalize([new PinSnapshot(100L, utc)]);
        var hashPlus1 = PinSetCanonicalizer.Canonicalize([new PinSnapshot(100L, plus1)]);

        hashUtc.ShouldBe(hashPlus1);
    }

    // ---------------------------------------------------------------------------
    // Null vs non-null EditedAt always differ
    // ---------------------------------------------------------------------------

    [Test]
    public void NullVsNonNullEditedAt_ProducesDifferentHash()
    {
        var withEdit = new[] { new PinSnapshot(100L, T1) };
        var withoutEdit = new[] { new PinSnapshot(100L, null) };

        PinSetCanonicalizer.Canonicalize(withEdit)
            .ShouldNotBe(PinSetCanonicalizer.Canonicalize(withoutEdit));
    }
}
