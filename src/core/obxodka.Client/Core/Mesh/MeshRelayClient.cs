namespace obxodka.Core.Mesh;

public static class MeshRelayClient
{
    private const uint ObxmMagic = 0x4D58424F;
    private const byte ProtocolVersion = 0x01;
    public const byte StatusOk = 0x01;
    public const byte StatusFail = 0x00;

    private static readonly Lazy<HttpClient> t_discoveryClient = new(() => new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(5)
    });

    public static async Task<Stream> ConnectThroughRelayAsync(
        string relayIp,
        int relayPort,
        string targetHost,
        int targetPort,
        string? jwtToken,
        bool isFriend,
        CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            SendBufferSize = 8388608,
            ReceiveBufferSize = 8388608
        };

        NetworkStream? stream = null;
        try
        {
            GrpcTransport.OnSocketCreated?.Invoke(socket);
            await socket.ConnectAsync(relayIp, relayPort, ct).ConfigureAwait(false);

            stream = new NetworkStream(socket, ownsSocket: true);

            var jwtByteCount = Encoding.UTF8.GetByteCount(jwtToken ?? string.Empty);
            var targetStr = $"{targetHost}:{targetPort}";
            var targetByteCount = Encoding.UTF8.GetByteCount(targetStr);

            var totalHeaderLen = 4 + 1 + 2 + jwtByteCount + 2 + targetByteCount + 1;
            var header = ArrayPool<byte>.Shared.Rent(totalHeaderLen);
            try
            {
                var span = header.AsSpan();
                BinaryPrimitives.WriteUInt32LittleEndian(span[..4], ObxmMagic);
                span[4] = ProtocolVersion;

                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(5, 2), (ushort)jwtByteCount);
                if (jwtByteCount > 0)
                {
                    _ = Encoding.UTF8.GetBytes(jwtToken, span.Slice(7, jwtByteCount));
                }
                var offset = 7 + jwtByteCount;

                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), (ushort)targetByteCount);
                offset += 2;
                _ = Encoding.UTF8.GetBytes(targetStr, span.Slice(offset, targetByteCount));
                offset += targetByteCount;

                span[offset] = (byte)(isFriend ? 0x01 : 0x00);

                await stream.WriteAsync(header.AsMemory(0, totalHeaderLen), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header);
            }

            var respBuffer = ArrayPool<byte>.Shared.Rent(3);
            byte status;
            byte errorCode;
            try
            {
                await stream.ReadExactlyAsync(respBuffer.AsMemory(0, 3), ct).ConfigureAwait(false);
                status = respBuffer[0];
                errorCode = respBuffer[1];
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(respBuffer);
            }

            if (status != StatusOk)
            {
                var errorMsg = errorCode switch
                {
                    0x01 => "Месячный лимит релея исчерпан.",
                    0x02 => "Релей перегружен (максимум одновременных клиентов).",
                    0x03 => "Невалидный токен авторизации на релее.",
                    0x04 => "Релей не смог подключиться к целевому серверу.",
                    _ => $"Ошибка подключения через Mesh релей (Код: {errorCode})."
                };
                throw new InvalidOperationException(errorMsg);
            }

            return stream;
        }
        catch
        {
            if (stream is not null)
            {
                stream.Dispose();
            }
            else
            {
                socket.Dispose();
            }
            throw;
        }
    }

    public static async Task<MeshRelayInfo?> GetBestRelayAsync(string? jwtToken = null, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AppConfig.ApiUrl("api/relay/available"));
            if (!string.IsNullOrWhiteSpace(jwtToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken);
            }

            var response = await t_discoveryClient.Value.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var relays = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.ListMeshRelayInfo, ct).ConfigureAwait(false);
                if (relays is { Count: > 0 })
                {
                    return relays
                        .OrderByDescending(r => r.IsFriend)
                        .ThenBy(r => r.PingMs)
                        .ThenBy(r => r.LoadPercent)
                        .FirstOrDefault();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MESH CLIENT] Discovery failed: {ex.Message}");
        }

        return null;
    }
}

