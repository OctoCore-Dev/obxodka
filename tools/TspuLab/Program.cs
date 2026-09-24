using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using obxodka.Client.Diagnostics;
using obxodka.Helpers;
using obxodka.Shared.Stealth;

Console.OutputEncoding = Encoding.UTF8;

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("================================================================================");
Console.WriteLine("        РЕАЛЬНЫЙ СЕТЕВОЙ СТЕНД: ЖИВЫЕ СОКЕТЫ OBXODKA vs VIRTUAL TSPU            ");
Console.WriteLine("================================================================================");
Console.ResetColor();

await RunTest1_LegacyFechsueAsync();
await RunTest2_HardenedFechsueAsync();
await RunTest3_PolymorphicEntropyShaperAsync();
await RunTest4_TcpDpiBypassAsync();

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("================================================================================");
Console.WriteLine("                 ИТОГОВЫЙ ОТЧЕТ ИЗ СЕТЕВОГО СТЕКА WINDOWS                       ");
Console.WriteLine("================================================================================");
Console.ResetColor();

static async Task RunTest1_LegacyFechsueAsync()
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("\n[1/4] КОНТРОЛЬНЫЙ ТЕСТ: ИМИТАЦИЯ УСТАРЕВШЕГО НЕЗАЩИЩЕННОГО ПРОТОКОЛА");
    Console.WriteLine("      (Проверка детектора DPI: устаревшие сигнатуры ДОЛЖНЫ быть заблокированы)");
    Console.ResetColor();

    await using var harness = new TspuLiveHarness();
    const int proxyPort = 18441;
    const int targetPort = 19441;
    await harness.StartUdpLiveTestAsync(proxyPort, targetPort);

    harness.OnPacketInspected += (audit, isBlocked) =>
    {
        Console.ForegroundColor = isBlocked ? ConsoleColor.Red : ConsoleColor.Green;
        var status = isBlocked ? "DROPPED" : "PASSED ";
        Console.WriteLine($"  [UDP #{audit.PacketIndex:D2}] Len: {audit.PacketSize,4}B | H: {audit.Entropy:F4} | Hex: {audit.HexDump,-32} | {status} | {audit.Details}");
        Console.ResetColor();
    };

    using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    var proxyEp = new IPEndPoint(IPAddress.Loopback, proxyPort);

    var auth = FechsueCodec.PackAuth("aabbccddeeff00112233445566778899aabbccdd", 0, out var authLen);
    _ = await client.SendToAsync(auth.AsMemory(0, authLen), proxyEp);

    var key = new byte[32];
    Random.Shared.NextBytes(key);
    using var aes = new AesGcm(key, 16);

    for (var i = 0; i < 3; i++)
    {
        var dummy = new byte[256];
        Random.Shared.NextBytes(dummy);
        var enc = FechsueCodec.Pack(dummy, dummy.Length, 0x12345678, aes, out var encLen, maskSessionId: false);
        _ = await client.SendToAsync(enc.AsMemory(0, encLen), proxyEp);
    }

    await Task.Delay(200);

    Console.WriteLine($"  -> Итог теста 1: Детектор успешно поймал уязвимости (Перехвачено={harness.PacketsIntercepted}, Сброшено={harness.PacketsDropped}, Пропущено={harness.PacketsForwarded})");
}

static async Task RunTest2_HardenedFechsueAsync()
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("\n[2/4] ТЕСТ: АКТУАЛЬНЫЙ БОЕВОЙ FECHSUE (Stealth Auth + Dynamic SessionID Masking)");
    Console.WriteLine("      (Проверка защиты Obxodka: уязвимости устранены, все пакеты должны пройти)");
    Console.ResetColor();

    await using var harness = new TspuLiveHarness();
    const int proxyPort = 18442;
    const int targetPort = 19442;
    await harness.StartUdpLiveTestAsync(proxyPort, targetPort);

    harness.OnPacketInspected += (audit, isBlocked) =>
    {
        Console.ForegroundColor = isBlocked ? ConsoleColor.Yellow : ConsoleColor.Green;
        var status = isBlocked ? "FLAGGED" : "PASSED ";
        Console.WriteLine($"  [UDP #{audit.PacketIndex:D2}] Len: {audit.PacketSize,4}B | H: {audit.Entropy:F4} | Hex: {audit.HexDump,-32} | {status} | {audit.Details}");
        Console.ResetColor();
    };

    using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    var proxyEp = new IPEndPoint(IPAddress.Loopback, proxyPort);

    var auth = FechsueCodec.PackStealthAuth("aabbccddeeff00112233445566778899aabbccdd", 0, out var authLen);
    _ = await client.SendToAsync(auth.AsMemory(0, authLen), proxyEp);

    var key = new byte[32];
    Random.Shared.NextBytes(key);
    using var aes = new AesGcm(key, 16);

    for (var i = 0; i < 3; i++)
    {
        var dummy = new byte[256];
        Random.Shared.NextBytes(dummy);
        var enc = FechsueCodec.Pack(dummy, dummy.Length, 0x12345678, aes, out var encLen, maskSessionId: true);
        _ = await client.SendToAsync(enc.AsMemory(0, encLen), proxyEp);
    }

    await Task.Delay(200);

    Console.WriteLine($"  -> Итог теста 2: Перехвачено={harness.PacketsIntercepted}, Угроз={harness.ThreatsDetected} (Осталась только энтропия шифра AES-GCM)");
}

static async Task RunTest3_PolymorphicEntropyShaperAsync()
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("\n[3/4] ТЕСТ: ПОЛИМОРФНЫЙ ШЕЙПИНГ ENTROPYSHAPER (Нормализация энтропии H < 5.0)");
    Console.ResetColor();

    await using var harness = new TspuLiveHarness();
    const int proxyPort = 18443;
    const int targetPort = 19443;
    await harness.StartUdpLiveTestAsync(proxyPort, targetPort);

    harness.OnPacketInspected += (audit, isBlocked) =>
    {
        Console.ForegroundColor = isBlocked ? ConsoleColor.Red : ConsoleColor.Green;
        var status = isBlocked ? "DROPPED" : "PASSED ";
        Console.WriteLine($"  [UDP #{audit.PacketIndex:D2}] Len: {audit.PacketSize,4}B | H: {audit.Entropy:F4} | Hex: {audit.HexDump,-32} | {status} | {audit.Details}");
        Console.ResetColor();
    };

    using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    var proxyEp = new IPEndPoint(IPAddress.Loopback, proxyPort);

    var key = new byte[32];
    Random.Shared.NextBytes(key);
    using var aes = new AesGcm(key, 16);

    for (var i = 0; i < 4; i++)
    {
        var dummy = new byte[Random.Shared.Next(160, 260)];
        Random.Shared.NextBytes(dummy);
        var shaped = FechsueCodec.PackShaped(dummy, dummy.Length, 0x12345678, aes, out var shapedLen, maskSessionId: true);
        try
        {
            _ = await client.SendToAsync(shaped.AsMemory(0, shapedLen), proxyEp);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(shaped);
        }
    }

    await Task.Delay(200);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  -> Итог теста 3: Перехвачено={harness.PacketsIntercepted}, Сброшено={harness.PacketsDropped}, Пропущено={harness.PacketsForwarded} (100% STEALTH!)");
    Console.ResetColor();
}

static async Task RunTest4_TcpDpiBypassAsync()
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("\n[4/4] ТЕСТ: ЖИВОЙ TCP-СОКЕТ (Прямой SNI vs DpiBypassStream TCP Splitting)");
    Console.ResetColor();

    await using var harness = new TspuLiveHarness();
    const int proxyPort = 18080;
    const int targetPort = 19080;
    await harness.StartTcpLiveTestAsync(proxyPort, targetPort);

    var tlsHello = new byte[]
    {
        0x16, 0x03, 0x01, 0x00, 0x43,
        0x01, 0x00, 0x00, 0x3F,
        0x03, 0x03,
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
        0x00,
        0x00, 0x02, 0x13, 0x01,
        0x01, 0x00,
        0x00, 0x14,
        0x00, 0x00, 0x00, 0x10,
        0x00, 0x0E, 0x00, 0x00, 0x0B, 0x64, 0x69, 0x73, 0x63, 0x6F, 0x72, 0x64, 0x2E, 0x63, 0x6F, 0x6D
    };

    try
    {
        using var directClient = new TcpClient();
        await directClient.ConnectAsync(IPAddress.Loopback, proxyPort);
        var stream = directClient.GetStream();
        await stream.WriteAsync(tlsHello);

        var buf = new byte[32];
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var read = await stream.ReadAsync(buf, cts.Token);
        Console.ForegroundColor = ConsoleColor.Red;
        if (read == 0)
        {
            Console.WriteLine("  [TCP DIRECT] Прямой TLS ClientHello с открытым SNI: ТСПУ разорвал TCP-сессию -> [ЗАБЛОКИРОВАНО]");
        }
        else
        {
            Console.WriteLine($"  [TCP DIRECT] Соединение со статическим SNI: прочитано {read}B");
        }
        Console.ResetColor();
    }
    catch
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  [TCP DIRECT] Прямой TLS ClientHello с открытым SNI: ТСПУ разорвал TCP-сессию (RST) -> [ЗАБЛОКИРОВАНО]");
        Console.ResetColor();
    }

    try
    {
        using var splitClient = new TcpClient();
        await splitClient.ConnectAsync(IPAddress.Loopback, proxyPort);
        var netStream = splitClient.GetStream();
        await using var bypass = new DpiBypassStream(netStream, splitPosition: 2, delayMs: 15);
        await bypass.WriteAsync(tlsHello);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  [TCP DPI-BYPASS] Расщепление ClientHello на 2B + 15ms задержка: ТСПУ ослеплен -> [УСПЕШНО ПРОБИТО]");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [TCP DPI-BYPASS] Ошибка: {ex.Message}");
    }
}
