namespace WinForward.E2E.Wire;

internal static class Filler
{
    private const uint Knuth = 2654435761u;
    private const uint ZeroSeedSubstitute = 0x9E3779B9u;

    internal static void Fill(uint connectionId, ulong sequence, Span<byte> destination)
    {
        var state = Seed(connectionId, sequence);
        for (var index = 0; index < destination.Length; index++)
        {
            state = Next(state);
            destination[index] = (byte)state;
        }
    }

    internal static bool Matches(uint connectionId, ulong sequence, ReadOnlySpan<byte> payload)
    {
        var state = Seed(connectionId, sequence);
        for (var index = 0; index < payload.Length; index++)
        {
            state = Next(state);
            if (payload[index] != (byte)state)
            {
                return false;
            }
        }

        return true;
    }

    private static uint Seed(uint connectionId, ulong sequence)
    {
        var state = (connectionId * Knuth) ^ (uint)sequence;
        return state == 0 ? ZeroSeedSubstitute : state;
    }

    private static uint Next(uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }
}
