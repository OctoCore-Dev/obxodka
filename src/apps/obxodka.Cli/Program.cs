using obxodka.Cli;

CliAnsiUi.Initialize();

using var connectivity = new CliConnectivityService();
PlatformServices.Init(
    new CliPreferencesService(),
    new CliSecureStorageService(),
    connectivity,
    new CliMainThreadService(),
    new CliDeviceInfoService());

using var app = new CliApp();
return await app.RunAsync(args).ConfigureAwait(false);
