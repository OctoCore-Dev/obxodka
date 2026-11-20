namespace obxodka.Pages.Settings;

public sealed partial class ConfigurationView
{
    private bool _isTestingSpeed;
    private CancellationTokenSource? _speedTestCts;

    //запуск или остановка замера скорости сети
    private async void OnRunSpeedTestClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            if (sender is VisualElement btn)
            {
                _ = btn.BounceClickAsync();
            }

            if (_isTestingSpeed)
            {
                if (_speedTestCts is not null)
                {
                    await _speedTestCts.CancelAsync();
                }

                return;
            }

            _isTestingSpeed = true;
            _speedTestCts = new CancellationTokenSource();
            var token = _speedTestCts.Token;

            SpeedTestBtnText.Text = SettingsMessages.SpeedTestBtnStop;
            SpeedTestResultLabel.Text = SettingsMessages.SpeedTestConnecting;
            SpeedTestResultLabel.TextColor = CyanColor;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (SpeedGaugeIcon is null)
                {
                    return;
                }

                _ = SpeedGaugeIcon.AbortAnimation("SpeedGaugeSpin");
                var spinAnim = new Animation(v => SpeedGaugeIcon.Rotation = v, 0, 360);
                spinAnim.Commit(SpeedGaugeIcon, "SpeedGaugeSpin", 16, 900, Easing.Linear, repeat: () => _isTestingSpeed);
            });

            try
            {
                await Task.Run(async () =>
                {
                    using HttpMessageHandler handler = OperatingSystem.IsAndroid()
                        ? new HttpClientHandler
                        {
                            UseProxy = false
                        }
                        : new SocketsHttpHandler
                        {
                            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                            EnableMultipleHttp2Connections = true
                        };
                    using var client = new HttpClient(handler);
                    client.Timeout = TimeSpan.FromSeconds(25);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");

                    long pingMs = 0;
                    foreach (var endpoint in DiagnosticEndpoints.PingEndpoints)
                    {
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        try
                        {
                            var pingSw = Stopwatch.StartNew();
                            using var req = new HttpRequestMessage(HttpMethod.Head, endpoint);
                            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
                            pingSw.Stop();
                            if (resp.IsSuccessStatusCode || (int)resp.StatusCode < 500)
                            {
                                pingMs = pingMs == 0 ? pingSw.ElapsedMilliseconds : Math.Min(pingMs, pingSw.ElapsedMilliseconds);
                            }
                        }
                        catch when (!token.IsCancellationRequested) { }
                    }

                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        var pingText = pingMs > 0 ? $"{pingMs} ms • " : "";
                        SpeedTestResultLabel.Text = $"{pingText}{SettingsMessages.SpeedTestStarting}";
                    });

                    string? activeUrl = null;
                    foreach (var candidate in DiagnosticEndpoints.SpeedTestDownloadCandidates)
                    {
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        try
                        {
                            using var probeReq = new HttpRequestMessage(HttpMethod.Head, candidate);
                            using var probeResp = await client.SendAsync(probeReq, HttpCompletionOption.ResponseHeadersRead, token);
                            if (!probeResp.IsSuccessStatusCode)
                            {
                                continue;
                            }

                            activeUrl = candidate;
                            break;
                        }
                        catch when (!token.IsCancellationRequested) { }
                    }

                    activeUrl ??= "https://speedtest.selectel.ru/100MB";

                    const double targetDurationSeconds = 10.0;
                    var overallSw = Stopwatch.StartNew();
                    var windowSw = Stopwatch.StartNew();

                    var counters = new SpeedTestCounters();
                    double peakSpeedMBs = 0;

                    const int workerCount = 2;
                    List<Task> workerTasks = [];

                    for (var i = 0; i < workerCount; i++)
                    {
                        workerTasks.Add(RunDownloadWorkerAsync(
                            client,
                            activeUrl,
                            overallSw,
                            targetDurationSeconds,
                            counters,
                            token));
                    }

                    try
                    {
                        while (!token.IsCancellationRequested && overallSw.Elapsed.TotalSeconds < targetDurationSeconds)
                        {
                            await Task.Delay(200, token);

                            if (windowSw.ElapsedMilliseconds < 300)
                            {
                                continue;
                            }

                            var windowSec = Math.Max(windowSw.Elapsed.TotalSeconds, 0.01);
                            var currentWBytes = Interlocked.Exchange(ref counters.WindowBytes, 0);
                            var currentInstantMBs = currentWBytes / (1024.0 * 1024.0) / windowSec;
                            var currentMbps = currentInstantMBs * 8.0;

                            peakSpeedMBs = Math.Max(peakSpeedMBs, currentInstantMBs);

                            var remaining = (int)Math.Max(targetDurationSeconds - overallSw.Elapsed.TotalSeconds, 0);
                            windowSw.Restart();

                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                SpeedTestResultLabel.Text = $"{currentMbps:F1} Мбит/с ({currentInstantMBs:F1} МБ/с) [{remaining}с]";
                                SpeedTestResultLabel.TextColor = CyanColor;
                            });
                        }
                    }
                    finally
                    {
                        await Task.WhenAll(workerTasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    }

                    overallSw.Stop();
                    var totalSec = Math.Max(overallSw.Elapsed.TotalSeconds, 0.1);
                    var avgSpeedMBs = Interlocked.Read(ref counters.TotalBytes) / (1024.0 * 1024.0) / totalSec;
                    if (peakSpeedMBs < avgSpeedMBs)
                    {
                        peakSpeedMBs = avgSpeedMBs;
                    }

                    var avgMbps = avgSpeedMBs * 8.0;
                    var peakMbps = peakSpeedMBs * 8.0;

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        var pingText = pingMs > 0 ? $" • {pingMs} ms" : "";
                        SpeedTestResultLabel.Text = $"{avgMbps:F1} Мбит/с ({avgSpeedMBs:F1} МБ/с){pingText} (Пик: {peakMbps:F1} Мбит/с)";
                        SpeedTestResultLabel.TextColor = EmeraldColor;
                    });
                }, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[SPEED TEST ERROR] {ex.Message}", ex);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    SpeedTestResultLabel.Text = SettingsMessages.SpeedTestFailed;
                    SpeedTestResultLabel.TextColor = ErrorRedColor;
                });
            }
            finally
            {
                _isTestingSpeed = false;
                _speedTestCts?.Dispose();
                _speedTestCts = null;

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    SpeedTestBtnText.Text = SettingsMessages.SpeedTestBtnStart;
                    if (SpeedGaugeIcon is null)
                    {
                        return;
                    }

                    _ = SpeedGaugeIcon.AbortAnimation("SpeedGaugeSpin");
                    _ = SpeedGaugeIcon.RotateToAsync(0, 300, Easing.CubicOut);
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[SPEED TEST ERROR] {ex.Message}", ex);
        }
    }

    //рабочий поток загрузки данных для замера скорости
    private static async Task RunDownloadWorkerAsync(
        HttpClient client,
        string activeUrl,
        Stopwatch overallSw,
        double targetDurationSeconds,
        SpeedTestCounters counters,
        CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(131072);
        try
        {
            while (!token.IsCancellationRequested && overallSw.Elapsed.TotalSeconds < targetDurationSeconds)
            {
                try
                {
                    using var streamReq = new HttpRequestMessage(HttpMethod.Get, activeUrl);
                    using var resp = await client.SendAsync(streamReq, HttpCompletionOption.ResponseHeadersRead, token);
                    if (!resp.IsSuccessStatusCode)
                    {
                        break;
                    }

                    await using var stream = await resp.Content.ReadAsStreamAsync(token);
                    while (!token.IsCancellationRequested && overallSw.Elapsed.TotalSeconds < targetDurationSeconds)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(0, 131072), token);
                        if (read <= 0)
                        {
                            break;
                        }

                        _ = Interlocked.Add(ref counters.TotalBytes, read);
                        _ = Interlocked.Add(ref counters.WindowBytes, read);
                    }
                }
                catch when (!token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    //отмена и освобождение ресурсов замера скорости
    private void DisposeSpeedTest()
    {
        _speedTestCts?.Cancel();
        _speedTestCts?.Dispose();
        _speedTestCts = null;
    }


    private sealed class SpeedTestCounters
    {
        public long TotalBytes;
        public long WindowBytes;
    }
}
