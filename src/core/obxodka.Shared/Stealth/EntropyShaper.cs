using System.Security.Cryptography;

namespace obxodka.Shared.Stealth;

public static class EntropyShaper
{
    private static readonly byte[] t_symbolMap =
    [
        0x20, 0x61, 0x65, 0x69, 0x6F, 0x75, 0x6E, 0x74,
        0x73, 0x72, 0x68, 0x64, 0x6C, 0x63, 0x6D, 0x66,
        0x2E, 0x2C, 0x67, 0x70, 0x77, 0x62, 0x76, 0x6B,
        0x79, 0x78, 0x6A, 0x71, 0x7A, 0x30, 0x31, 0x32
    ];

    private static readonly byte[] t_reverseMap = new byte[256];

    static EntropyShaper()
    {
        Array.Fill(t_reverseMap, (byte)0xFF);
        for (var i = 0; i < t_symbolMap.Length; i++)
        {
            t_reverseMap[t_symbolMap[i]] = (byte)i;
        }
    }

    public static int GetMaxEncodedLength(int inputLength) =>
        (((inputLength * 8) + 4) / 5) + 8;

    public static int GetMaxDecodedLength(int encodedLength) =>
        Math.Max(0, encodedLength * 5 / 8);

    public static bool TryEncode(ReadOnlySpan<byte> input, Span<byte> output, out int bytesWritten)
    {
        bytesWritten = 0;
        var maxNeeded = GetMaxEncodedLength(input.Length);
        if (output.Length < maxNeeded || input.Length > ushort.MaxValue)
        {
            return false;
        }

        var len = (ushort)input.Length;
        var salt = (byte)Random.Shared.Next(0, 32);

        output[0] = t_symbolMap[len & 0x1F];
        output[1] = t_symbolMap[(len >> 5) & 0x1F];
        output[2] = t_symbolMap[(len >> 10) & 0x1F];
        output[3] = t_symbolMap[(len >> 15) & 0x1F];
        output[4] = t_symbolMap[salt & 0x1F];
        var outIdx = 5;

        var bitBuffer = 0;
        var bitCount = 0;

        foreach (var b in input)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;

            while (bitCount >= 5)
            {
                bitCount -= 5;
                var val = (bitBuffer >> bitCount) & 0x1F;
                output[outIdx++] = t_symbolMap[val];
            }
        }

        if (bitCount > 0)
        {
            var val = (bitBuffer << (5 - bitCount)) & 0x1F;
            output[outIdx++] = t_symbolMap[val];
        }

        bytesWritten = outIdx;
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> input, Span<byte> output, out int bytesWritten)
    {
        bytesWritten = 0;
        if (input.Length < 5)
        {
            return false;
        }

        var s0 = t_reverseMap[input[0]];
        var s1 = t_reverseMap[input[1]];
        var s2 = t_reverseMap[input[2]];
        var s3 = t_reverseMap[input[3]];
        var s4 = t_reverseMap[input[4]];

        if (s0 == 0xFF || s1 == 0xFF || s2 == 0xFF || s3 == 0xFF || s4 == 0xFF)
        {
            return false;
        }

        var expectedLen = s0 | (s1 << 5) | (s2 << 10) | (s3 << 15);
        if (output.Length < expectedLen)
        {
            return false;
        }

        var bitBuffer = 0;
        var bitCount = 0;
        var outIdx = 0;

        for (var i = 5; i < input.Length && outIdx < expectedLen; i++)
        {
            var symbol = input[i];
            var val = t_reverseMap[symbol];
            if (val == 0xFF)
            {
                return false;
            }

            bitBuffer = (bitBuffer << 5) | val;
            bitCount += 5;

            if (bitCount >= 8)
            {
                bitCount -= 8;
                output[outIdx++] = (byte)((bitBuffer >> bitCount) & 0xFF);
            }
        }

        if (outIdx != expectedLen)
        {
            return false;
        }

        bytesWritten = outIdx;
        return true;
    }
}
