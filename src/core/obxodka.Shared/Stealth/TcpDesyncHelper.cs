using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace obxodka.Shared.Stealth;

public static class TcpDesyncHelper
{
    public const int DefaultDesyncTtl = 3;
    public const int DefaultRealTtl = 64;

    private static readonly byte[] t_fakeHttpPayload = "GET / HTTP/1.1\r\nHost: ya.ru\r\nUser-Agent: Mozilla/5.0\r\nAccept: text/html\r\n\r\n"u8.ToArray();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TrySetSocketTtl(Socket socket, int ttl)
    {
        try
        {
            socket.Ttl = (short)Math.Clamp(ttl, 1, 255);
            return true;
        }
        catch
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, Math.Clamp(ttl, 1, 255));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSocketTtl(Socket socket)
    {
        try
        {
            return socket.Ttl;
        }
        catch
        {
            try
            {
                var opt = socket.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive);
                return opt is int val ? val : DefaultRealTtl;
            }
            catch
            {
                return DefaultRealTtl;
            }
        }
    }

    public static bool PerformTtlDesync(
        Socket socket,
        int desyncTtl = DefaultDesyncTtl,
        int realTtl = DefaultRealTtl,
        ReadOnlySpan<byte> fakePayload = default)
    {
        if (socket == null || !socket.Connected)
        {
            return false;
        }

        var payload = fakePayload.IsEmpty ? t_fakeHttpPayload : fakePayload;
        var originalTtl = GetSocketTtl(socket);

        if (!TrySetSocketTtl(socket, desyncTtl))
        {
            return false;
        }

        try
        {
            _ = socket.Send(payload, SocketFlags.None);
        }
        catch
        {
            return false;
        }
        finally
        {
            _ = TrySetSocketTtl(socket, originalTtl > 0 ? originalTtl : realTtl);
        }

        return true;
    }

    public static async ValueTask<bool> SendWithTtlDesyncAsync(
        Socket socket,
        ReadOnlyMemory<byte> payload,
        int splitPosition = 2,
        int desyncTtl = DefaultDesyncTtl,
        int realTtl = DefaultRealTtl,
        int delayMs = 25,
        CancellationToken ct = default)
    {
        if (socket == null || !socket.Connected || payload.Length == 0)
        {
            return false;
        }

        var split = Math.Clamp(splitPosition, 1, payload.Length);
        var originalTtl = GetSocketTtl(socket);

        _ = TrySetSocketTtl(socket, desyncTtl);
        try
        {
            _ = await socket.SendAsync(payload[..split], SocketFlags.None, ct).ConfigureAwait(false);
        }
        catch
        {
            _ = TrySetSocketTtl(socket, originalTtl > 0 ? originalTtl : realTtl);
            return false;
        }

        if (delayMs > 0)
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }

        _ = TrySetSocketTtl(socket, originalTtl > 0 ? originalTtl : realTtl);

        if (split < payload.Length)
        {
            _ = await socket.SendAsync(payload[split..], SocketFlags.None, ct).ConfigureAwait(false);
        }

        return true;
    }
}
