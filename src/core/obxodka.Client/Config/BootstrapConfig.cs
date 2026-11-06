namespace obxodka.Config;

//загрузка и кэширование списка серверов из servers.json
public static class BootstrapConfig
{
    private static readonly Lazy<(List<VpnServerDto> Servers, string[] Mirrors)> s_bootstrapData = new(LoadBootstrapServersFromJson);

    //список встроенных серверов
    public static List<VpnServerDto> Servers => s_bootstrapData.Value.Servers;

    //список доменов зеркал из файла
    public static string[] Mirrors => s_bootstrapData.Value.Mirrors;

    //парсинг и расшифровка адресов серверов
    private static (List<VpnServerDto> Servers, string[] Mirrors) LoadBootstrapServersFromJson()
    {
        try
        {
            string? json = null;
            var localPath = Path.Combine(AppContext.BaseDirectory, "servers.json");
            if (File.Exists(localPath))
            {
                json = File.ReadAllText(localPath);
            }
            else
            {
                using var stream = typeof(BootstrapConfig).Assembly.GetManifestResourceStream("obxodka.servers.json");
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    json = reader.ReadToEnd();
                }
            }

            if (!string.IsNullOrWhiteSpace(json))
            {
                var parsed = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListVpnServerDto);
                if (parsed is { Count: > 0 })
                {
                    List<string> mirrorList = [];
                    for (var i = 0; i < parsed.Count; i++)
                    {
                        var item = parsed[i];
                        var decIp = AppSecrets.DecodeObfuscatedEndpoint(item.Ip);
                        string[]? decItemMirrors = null;
                        if (item.Mirrors is { Length: > 0 })
                        {
                            decItemMirrors = new string[item.Mirrors.Length];
                            for (var m = 0; m < item.Mirrors.Length; m++)
                            {
                                var dm = AppSecrets.DecodeObfuscatedEndpoint(item.Mirrors[m]);
                                decItemMirrors[m] = dm;
                                if (!mirrorList.Contains(dm, StringComparer.OrdinalIgnoreCase))
                                {
                                    mirrorList.Add(dm);
                                }
                            }
                        }

                        parsed[i] = item with { Ip = decIp, Mirrors = decItemMirrors };
                    }

                    return (parsed, [.. mirrorList]);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[BOOTSTRAP] Failed to load servers.json: {ex.Message}");
        }

        return ([], []);
    }
}
