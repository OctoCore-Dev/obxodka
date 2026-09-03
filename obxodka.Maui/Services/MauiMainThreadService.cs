using obxodka.Client.Platforms;
using MauiMainThread = Microsoft.Maui.ApplicationModel.MainThread;

namespace obxodka.Maui.Services;

public sealed class MauiMainThreadService : IMainThreadService
{
    public bool IsMainThread => MauiMainThread.IsMainThread;

    public void BeginInvokeOnMainThread(Action action) =>
        MauiMainThread.BeginInvokeOnMainThread(action);

    public Task InvokeOnMainThreadAsync(Action action) =>
        MauiMainThread.InvokeOnMainThreadAsync(action);

    public Task<T> InvokeOnMainThreadAsync<T>(Func<Task<T>> func) =>
        MauiMainThread.InvokeOnMainThreadAsync(func);
}
