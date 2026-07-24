using System.Buffers.Binary;

namespace X402.Cryptography;

/// <summary>
/// Keccak-256 — the hash function Ethereum uses for addresses, EIP-55 checksums, and everywhere
/// else labelled "keccak256". It has no key and no secret: it is a public, fixed algorithm with
/// published test vectors, a hash function to transcribe rather than cryptography to invent.
/// </summary>
/// <remarks>
/// This is deliberately NOT <see cref="System.Security.Cryptography.SHA3_256"/>. NIST changed the
/// padding byte when it standardized SHA-3 (<c>0x06</c> instead of the original submission's
/// <c>0x01</c>), so the BCL's SHA3-256 silently produces a different digest for the same input.
/// Lives in <c>X402.Core</c>, not in a signing package, because a hash function with no key does
/// not touch the non-custodial constraint that keeps signing libraries out of the server package —
/// and both the server's EIP-55 validation and the client's address handling need it.
/// </remarks>
public static class Keccak256
{
    private const int StateBytes = 200; // 1600-bit state.
    private const int DigestBytes = 32; // 256-bit digest.
    private const int RateBytes = StateBytes - (2 * DigestBytes); // 136 bytes absorbed per block.
    private const int Rounds = 24; // Keccak-f[1600] runs 24 rounds.
    private const byte Pad = 0x01; // Keccak's multi-rate padding byte; SHA-3 uses 0x06 instead.

    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001, 0x0000000000008082, 0x800000000000808A, 0x8000000080008000,
        0x000000000000808B, 0x0000000080000001, 0x8000000080008081, 0x8000000000008009,
        0x000000000000008A, 0x0000000000000088, 0x0000000080008009, 0x000000008000000A,
        0x000000008000808B, 0x800000000000008B, 0x8000000000008089, 0x8000000000008003,
        0x8000000000008002, 0x8000000000000080, 0x000000000000800A, 0x800000008000000A,
        0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008,
    ];

    // Rho: per-lane left-rotation amounts, indexed in the same traversal order as PiLaneIndices.
    private static readonly int[] RotationOffsets =
    [
        1, 3, 6, 10, 15, 21, 28, 36, 45, 55, 2, 14,
        27, 41, 56, 8, 25, 43, 62, 18, 39, 61, 20, 44,
    ];

    // Pi: destination lane index for each step of the combined rho/pi traversal.
    private static readonly int[] PiLaneIndices =
    [
        10, 7, 11, 17, 18, 3, 5, 16, 8, 21, 24, 4,
        15, 23, 19, 13, 12, 2, 20, 14, 22, 9, 6, 1,
    ];

    /// <summary>Computes the 32-byte Keccak-256 digest of <paramref name="input"/>.</summary>
    public static byte[] ComputeHash(ReadOnlySpan<byte> input)
    {
        var state = new ulong[25];

        var offset = 0;
        while (input.Length - offset >= RateBytes)
        {
            Absorb(state, input.Slice(offset, RateBytes));
            offset += RateBytes;
        }

        Span<byte> block = stackalloc byte[RateBytes];
        block.Clear();
        input[offset..].CopyTo(block);
        block[input.Length - offset] ^= Pad;
        block[RateBytes - 1] ^= 0x80;
        Absorb(state, block);

        var digest = new byte[DigestBytes];
        for (var i = 0; i < DigestBytes / 8; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(digest.AsSpan(i * 8, 8), state[i]);
        }

        return digest;
    }

    private static void Absorb(ulong[] state, ReadOnlySpan<byte> block)
    {
        for (var i = 0; i < RateBytes / 8; i++)
        {
            state[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(i * 8, 8));
        }

        Permute(state);
    }

    private static void Permute(ulong[] state)
    {
        Span<ulong> c = stackalloc ulong[5];

        for (var round = 0; round < Rounds; round++)
        {
            // Theta: XOR each column's parity into every lane of the two neighbouring columns.
            for (var x = 0; x < 5; x++)
            {
                c[x] = state[x] ^ state[x + 5] ^ state[x + 10] ^ state[x + 15] ^ state[x + 20];
            }

            for (var x = 0; x < 5; x++)
            {
                var t = c[(x + 4) % 5] ^ RotateLeft(c[(x + 1) % 5], 1);
                for (var y = 0; y < 25; y += 5)
                {
                    state[y + x] ^= t;
                }
            }

            // Rho and pi, combined into a single traversal: rotate each lane, then move it.
            var current = state[1];
            for (var i = 0; i < 24; i++)
            {
                var j = PiLaneIndices[i];
                var temp = state[j];
                state[j] = RotateLeft(current, RotationOffsets[i]);
                current = temp;
            }

            // Chi: non-linear mixing within each row.
            for (var y = 0; y < 25; y += 5)
            {
                for (var x = 0; x < 5; x++)
                {
                    c[x] = state[y + x];
                }

                for (var x = 0; x < 5; x++)
                {
                    state[y + x] ^= ~c[(x + 1) % 5] & c[(x + 2) % 5];
                }
            }

            // Iota: break the round's symmetry with a round-specific constant.
            state[0] ^= RoundConstants[round];
        }
    }

    private static ulong RotateLeft(ulong value, int offset) =>
        (value << offset) | (value >> (64 - offset));
}
