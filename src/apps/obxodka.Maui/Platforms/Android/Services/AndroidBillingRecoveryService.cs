namespace obxodka;

//единый ssot-координатор google play billing для восстановления незавершенных покупок и надежного consume
internal static class AndroidBillingRecoveryService
{
    private const long MinRecoveryIntervalMs = 15_000;
    private static long s_lastRecoveryTicks;
    private static int s_activeRecovery;

    //общий шлюз доступа к CrossInAppBilling чтобы не было параллельных ConnectAsync/DisconnectAsync
    public static SemaphoreSlim Gate { get; } = new(1, 1);

    //надежное потребление разового товара google play с повторными попытками и проверкой результата
    public static async Task<bool> ConsumeWithRetryAsync(
        IInAppBilling billing,
        string productId,
        string purchaseToken,
        CancellationToken ct = default)
    {
        int[] delaysMs = [0, 700, 1800];
        foreach (var delay in delaysMs)
        {
            if (delay > 0)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            try
            {
                var consumed = await billing.ConsumePurchaseAsync(productId, purchaseToken, ct).ConfigureAwait(false);
                if (consumed)
                {
                    AppLogger.Log($"[BILLING CONSUME OK] Product={productId}");
                    return true;
                }

                AppLogger.LogWarning($"[BILLING CONSUME FALSE] Product={productId} — повторная попытка...");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[BILLING CONSUME RETRY] Product={productId}: {ex.Message}");
            }
        }

        AppLogger.LogWarning($"[BILLING CONSUME DEFERRED] Product={productId} — покупка будет повторно проверена при следующем запуске");
        return false;
    }

    //до-верификация и до-потребление зависших или отложенных покупок google play при старте и возврате сети
    public static async Task<int> RecoverPendingPurchasesAsync(
        ApiService apiService,
        Func<Task>? onRecovered = null,
        bool force = false,
        CancellationToken ct = default)
    {
        if (ApiService.IsUnauthorizedPending || !OctopusEngine.IsUplinkAvailable)
        {
            return 0;
        }

        var now = Environment.TickCount64;
        var prev = Volatile.Read(ref s_lastRecoveryTicks);
        if (!force && prev > 0 && now - prev < MinRecoveryIntervalMs)
        {
            return 0;
        }

        if (Interlocked.CompareExchange(ref s_activeRecovery, 1, 0) != 0)
        {
            return 0;
        }

        var recoveredCount = 0;
        try
        {
            if (await AuthManager.GetAuthHeaderAsync().ConfigureAwait(false) is null)
            {
                return 0;
            }

            if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return 0;
            }

            try
            {
                Volatile.Write(ref s_lastRecoveryTicks, Environment.TickCount64);
                var billing = CrossInAppBilling.Current;
                if (!await billing.ConnectAsync(cancellationToken: ct).ConfigureAwait(false))
                {
                    return 0;
                }

                try
                {
                    var purchases = await billing.GetPurchasesAsync(ItemType.InAppPurchase, cancellationToken: ct).ConfigureAwait(false);
                    if (purchases is null)
                    {
                        return 0;
                    }

                    foreach (var purchase in purchases)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (purchase.State != PurchaseState.Purchased ||
                            string.IsNullOrWhiteSpace(purchase.ProductId) ||
                            !TariffCalculator.TryGetGooglePlayProduct(purchase.ProductId, out _))
                        {
                            continue;
                        }

                        var token = purchase.PurchaseToken ?? purchase.Id;
                        if (string.IsNullOrWhiteSpace(token))
                        {
                            continue;
                        }

                        AppLogger.Log($"[BILLING RECOVERY] Найдена незавершенная покупка: Product={purchase.ProductId}, Order={purchase.Id}");

                        //идемпотентное подтверждение на сервере до вызова consume на клиенте
                        var (success, error) = await apiService
                            .VerifyGooglePurchaseAsync(purchase.ProductId, token, purchase.Id, ct)
                            .ConfigureAwait(false);

                        if (!success)
                        {
                            AppLogger.LogWarning($"[BILLING RECOVERY VERIFY WARN] Product={purchase.ProductId}: {error}");
                            continue;
                        }

                        _ = await ConsumeWithRetryAsync(billing, purchase.ProductId, token, ct).ConfigureAwait(false);
                        recoveredCount++;
                    }
                }
                finally
                {
                    await billing.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _ = Gate.Release();
            }

            if (recoveredCount > 0 && onRecovered is not null)
            {
                await onRecovered().ConfigureAwait(false);
            }

            return recoveredCount;
        }
        catch (OperationCanceledException)
        {
            return recoveredCount;
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[BILLING RECOVERY WARN] {ex.Message}");
            return recoveredCount;
        }
        finally
        {
            Volatile.Write(ref s_activeRecovery, 0);
        }
    }
}
