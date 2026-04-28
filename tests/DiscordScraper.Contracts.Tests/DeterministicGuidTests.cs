using DiscordScraper.Contracts;

namespace DiscordScraper.Contracts.Tests;

[TestFixture]
public sealed class DeterministicGuidTests
{
    [Test]
    public void SameSnowflake_ReturnsSameGuid_AcrossCalls()
    {
        const long snowflake = 1234567890123456789L;

        var first  = DeterministicGuid.FromSnowflake(snowflake);
        var second = DeterministicGuid.FromSnowflake(snowflake);

        first.ShouldBe(second);
    }

    [Test]
    public void DifferentSnowflakes_ReturnDifferentGuids()
    {
        var a = DeterministicGuid.FromSnowflake(100L);
        var b = DeterministicGuid.FromSnowflake(101L);

        a.ShouldNotBe(b);
    }

    [Test]
    public void KnownSnowflake_ReturnsKnownGuid()
    {
        // Regression anchor: changing DeterministicGuid internals (namespace, algorithm) will
        // break saga correlation for any already-stored MessageSaga. This test encodes the
        // expected output so any such change is caught at CI, not at runtime.
        //
        // Expected computed via:
        //   SHA1(namespace_bytes_in_rfc_network_order || int64_big_endian(1))
        //   with version=5, variant=10xx applied per RFC 4122 §4.3
        //   namespace: fdc7d414-2df7-5434-98d5-154d1776e052
        const long snowflake = 1L;
        var expected = new Guid("c226d82f-a360-5ef4-ba7a-55a217168d4a");

        var actual = DeterministicGuid.FromSnowflake(snowflake);

        actual.ShouldBe(expected);
    }

    [Test]
    public void ZeroSnowflake_ReturnsDeterministicGuid()
    {
        var first  = DeterministicGuid.FromSnowflake(0L);
        var second = DeterministicGuid.FromSnowflake(0L);

        first.ShouldBe(second);
        first.ShouldNotBe(Guid.Empty);
    }

    [Test]
    public void NegativeSnowflake_ReturnsDeterministicGuid()
    {
        // Snowflakes are technically always positive, but the method accepts long;
        // verify it doesn't throw and remains deterministic for edge inputs.
        var first  = DeterministicGuid.FromSnowflake(long.MinValue);
        var second = DeterministicGuid.FromSnowflake(long.MinValue);

        first.ShouldBe(second);
    }

    [Test]
    public void Version_IsFive()
    {
        var guid = DeterministicGuid.FromSnowflake(999L);
        // RFC 4122: version is the high nibble of byte 7 (the third group, high nibble)
        var bytes = guid.ToByteArray();
        // .NET Guid.ToByteArray() uses mixed-endian; byte[6] carries time_hi_and_version high nibble
        var version = (bytes[7] >> 4) & 0xF;
        version.ShouldBe(5);
    }
}
