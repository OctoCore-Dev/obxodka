using obxodka.Cli;

PlatformServices.Init(
    new CliPreferencesService(),
    new CliSecureStorageService(),
    new CliConnectivityService(),
    new CliMainThreadService(),
    new CliDeviceInfoService());

using var app = new CliApp();
return await app.RunAsync(args).ConfigureAwait(false);
