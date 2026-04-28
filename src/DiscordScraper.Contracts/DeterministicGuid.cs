using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DiscordScraper.Contracts;

/// <summary>
/// Produces deterministic GUIDs via UUIDv5 (RFC 4122 §4.3) so that MT saga correlation IDs
/// are stable across nodes and restarts. Any node receiving a snowflake computes the same
/// CorrelationId without coordination.
/// </summary>
public static class DeterministicGuid
{
    // Namespace UUID derived from:
    //   uuid.uuid5(NAMESPACE_URL, "https://github.com/bryanboettcher/discord-scraper/message-snowflake")
    // Stable across all runs. Do not change without a full saga store migration.
    private static readonly Guid Namespace = new("fdc7d414-2df7-5434-98d5-154d1776e052");

    public static Guid FromSnowflake(long snowflake)
    {
        // Write snowflake as big-endian bytes — canonical representation for name hashing.
        Span<byte> name = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(name, snowflake);

        return ComputeV5(Namespace, name);
    }

    private static Guid ComputeV5(Guid namespaceId, ReadOnlySpan<byte> name)
    {
        // RFC 4122 §4.3: SHA-1(namespace-in-network-byte-order || name)
        Span<byte> ns = stackalloc byte[16];
        WriteGuidNetworkByteOrder(namespaceId, ns);

        Span<byte> input = stackalloc byte[16 + name.Length];
        ns.CopyTo(input);
        name.CopyTo(input[16..]);

        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes]; // 20 bytes
        SHA1.HashData(input, hash);

        // Set version = 5 (0101 in high nibble of octet 6)
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        // Set variant = 10xx (RFC 4122 §4.1.1)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        // Guid constructor reads first 3 fields as host-endian; reinterpret bytes correctly.
        int a = BinaryPrimitives.ReadInt32BigEndian(hash[0..4]);
        short b = BinaryPrimitives.ReadInt16BigEndian(hash[4..6]);
        short c = BinaryPrimitives.ReadInt16BigEndian(hash[6..8]);

        return new Guid(a, b, c,
            hash[8], hash[9], hash[10], hash[11],
            hash[12], hash[13], hash[14], hash[15]);
    }

    private static void WriteGuidNetworkByteOrder(Guid g, Span<byte> dest)
    {
        // Guid.ToByteArray() uses host-endian for first 3 fields. We need network byte order.
        Span<byte> raw = stackalloc byte[16];
        g.TryWriteBytes(raw);

        // time_low (4 bytes): host → big-endian
        BinaryPrimitives.WriteInt32BigEndian(dest[0..4], BinaryPrimitives.ReadInt32LittleEndian(raw[0..4]));
        // time_mid (2 bytes)
        BinaryPrimitives.WriteInt16BigEndian(dest[4..6], BinaryPrimitives.ReadInt16LittleEndian(raw[4..6]));
        // time_hi_and_version (2 bytes)
        BinaryPrimitives.WriteInt16BigEndian(dest[6..8], BinaryPrimitives.ReadInt16LittleEndian(raw[6..8]));
        // clock_seq and node (8 bytes): already big-endian in Guid byte layout
        raw[8..16].CopyTo(dest[8..16]);
    }
}
