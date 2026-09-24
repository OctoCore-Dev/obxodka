using System.Buffers.Binary;

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
        if (output.Length < maxNeeded)
        {
            return false;
        }

        BinaryPrimitives.WriteInt32LittleEndian(output[..4], input.Length);
        var outIdx = 4;

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
        if (input.Length < 4)
        {
            return false;
        }

        var expectedLen = BinaryPrimitives.ReadInt32LittleEndian(input[..4]);
        if (expectedLen < 0 || output.Length < expectedLen)
        {
            return false;
        }

        var bitBuffer = 0;
        var bitCount = 0;
        var outIdx = 0;

        for (var i = 4; i < input.Length && outIdx < expectedLen; i++)
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
