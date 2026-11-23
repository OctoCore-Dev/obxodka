namespace obxodka.Core.Transports;

//общий классификатор исходящих служебных и пользовательских кадров протокола obx-v7
internal static class TransportFrameControlMapper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ClassifyOutgoingPacket(
        ReadOnlySpan<byte> packet,
        int length,
        out int payloadOffset,
        out int payloadLength)
    {
        var isMtu = length == 4 && packet[0] == 0xCC && packet[1] == 0xDD;
        var isBurstReq = length == 2 && packet[0] == 0xEE && packet[1] == 0x77;
        var isPong = length >= 2 && packet[0] == 0xEE && packet[1] == 0x88;
        var isDisconnect = length == 2 && packet[0] == 0xEE && packet[1] == 0x99;
        var isPingMarker = length >= 10 && packet[0] == 0xAA && packet[1] == 0xBB;
        var isPing = isPingMarker || length == 8;

        if (isMtu)
        {
            payloadOffset = 2;
            payloadLength = 2;
            return ObxodkaFraming.CmdMtuSync;
        }

        if (isBurstReq)
        {
            payloadOffset = 0;
            payloadLength = 0;
            return ObxodkaFraming.CmdMtuBurstRequest;
        }

        if (isDisconnect)
        {
            payloadOffset = 0;
            payloadLength = 0;
            return ObxodkaFraming.CmdDisconnect;
        }

        if (isPong)
        {
            payloadOffset = 2;
            payloadLength = length - 2;
            return ObxodkaFraming.CmdPong;
        }

        if (isPing)
        {
            payloadOffset = isPingMarker ? 2 : 0;
            payloadLength = length - payloadOffset;
            return ObxodkaFraming.CmdPing;
        }

        payloadOffset = 0;
        payloadLength = length;
        return ObxodkaFraming.CmdData;
    }
}
