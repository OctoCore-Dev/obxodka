using obxodka.Cli;
using obxodka.Platforms.Linux;

namespace obxodka.Client.Tests;

//тесты парсера команд cli, автодополнения, хранилища настроек и linux маршрутизации
public sealed class CliAndLinuxRoutingTests
{
    [Theory]
    [InlineData("start", "connect")]
    [InlineData("stop", "disconnect")]
    [InlineData("auth", "login")]
    [InlineData("ls", "servers")]
    [InlineData("st", "status")]
    [InlineData("connect", "connect")]
    public void CommandParser_ResolvesAliasesCorrectly(string input, string expected)
    {
        var parsed = CliCommandParser.Parse([input]);
        Assert.Equal(expected, parsed.Command);
    }

    [Fact]
    public void CommandParser_ParsesConnectFlagsAndPositionalServer()
    {
        var parsed = CliCommandParser.Parse(["start", "nl-ams", "--kill-switch", "--adblock", "--mtu", "1360", "--json"]);
        Assert.Equal("connect", parsed.Command);
        Assert.Single(parsed.PositionalArgs);
        Assert.Equal("nl-ams", parsed.PositionalArgs[0]);
        Assert.True(parsed.HasFlag("kill-switch"));
        Assert.True(parsed.HasFlag("adblock"));
        Assert.True(parsed.IsJson);
        Assert.Equal("1360", parsed.GetOption("mtu"));
    }

    [Theory]
    [InlineData("powershell", "Register-ArgumentCompleter")]
    [InlineData("bash", "_obxodka_completions")]
    [InlineData("zsh", "#compdef obxodka obx")]
    public void CompletionGenerator_ReturnsExpectedScript(string shell, string expectedSnippet)
    {
        var script = CliCompletionGenerator.Generate(shell);
        Assert.NotNull(script);
        Assert.Contains(expectedSnippet, script, StringComparison.Ordinal);
    }

    [Fact]
    public void CliPreferences_PersistsValuesToDisk()
    {
        var tempFile = Path.Join(Path.GetTempPath(), $"obx-prefs-{Guid.NewGuid():N}.json");
        try
        {
            var prefs1 = new CliPreferencesService(tempFile);
            prefs1.Set("KillSwitch", true);
            prefs1.Set("CustomMtu", 1380);

            var prefs2 = new CliPreferencesService(tempFile);
            Assert.True(prefs2.Get("KillSwitch", false));
            Assert.Equal(1380, prefs2.Get("CustomMtu", 0));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Theory]
    [InlineData("default via 192.168.1.1 dev eth0 proto dhcp src 192.168.1.42 metric 100", "192.168.1.1", "eth0")]
    [InlineData("default dev ppp0 scope link", null, "ppp0")]
    [InlineData("default via 10.0.0.1 dev wlan0 metric 600\ndefault via 192.168.0.1 dev eth0 metric 100", "10.0.0.1", "wlan0")]
    public void LinuxRoutingHelper_ParsesDefaultRoute(string output, string? expectedGw, string expectedDev)
    {
        var ok = LinuxNetworkRoutingHelper.TryParseDefaultRoute(output, out var gw, out var dev);
        Assert.True(ok);
        Assert.Equal(expectedGw, gw);
        Assert.Equal(expectedDev, dev);
    }

    [Theory]
    [InlineData("obx0", true)]
    [InlineData("eth0.100", true)]
    [InlineData("enp3s0-vpn", true)]
    [InlineData("", false)]
    [InlineData("eth0; rm -rf /", false)]
    [InlineData("interface_name_too_long", false)]
    public void LinuxRoutingHelper_ValidatesInterfaceNames(string name, bool expected)
    {
        Assert.Equal(expected, LinuxNetworkRoutingHelper.IsValidInterfaceName(name));
    }

    [Fact]
    public void LinuxRoutingHelper_BuildsBypassAndTunnelRoutes()
    {
        var bypass = LinuxNetworkRoutingHelper.BuildBypassRouteArgs("replace", "70.34.201.253", "192.168.1.1", "eth0");
        Assert.Equal(["-4", "route", "replace", "70.34.201.253/32", "via", "192.168.1.1", "dev", "eth0"], bypass);

        var tunnel = LinuxNetworkRoutingHelper.BuildTunnelRouteArgs("replace", "obx0", includeIpv6: true);
        Assert.Equal(4, tunnel.Count);
        Assert.Equal(["-4", "route", "replace", "0.0.0.0/1", "dev", "obx0"], tunnel[0]);
        Assert.Equal(["-4", "route", "replace", "128.0.0.0/1", "dev", "obx0"], tunnel[1]);
        Assert.Equal(["-6", "route", "replace", "::/1", "dev", "obx0"], tunnel[2]);
        Assert.Equal(["-6", "route", "replace", "8000::/1", "dev", "obx0"], tunnel[3]);
    }

    [Fact]
    public void LinuxRoutingHelper_ResolvConfBackupAndRestoreIsAtomicAndIdempotent()
    {
        var dir = Path.Join(Path.GetTempPath(), $"obx-resolv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var resolvPath = Path.Join(dir, "resolv.conf");
        var backupPath = Path.Join(dir, "resolv.conf.bak");
        try
        {
            const string original = "nameserver 192.168.1.1\nsearch home.arpa\n";
            File.WriteAllText(resolvPath, original);

            LinuxNetworkRoutingHelper.ApplyResolvConfOverride(resolvPath, backupPath, ["1.1.1.1", "9.9.9.9"]);
            //повторный вызов не должен затереть оригинальный бэкап
            LinuxNetworkRoutingHelper.ApplyResolvConfOverride(resolvPath, backupPath, ["1.0.0.1"]);

            var overridden = File.ReadAllText(resolvPath);
            Assert.Contains(LinuxNetworkRoutingHelper.ResolvConfMarker, overridden, StringComparison.Ordinal);
            Assert.Contains("nameserver 1.0.0.1", overridden, StringComparison.Ordinal);

            var restored = LinuxNetworkRoutingHelper.RestoreResolvConfBackup(resolvPath, backupPath);
            Assert.True(restored);
            Assert.Equal(original, File.ReadAllText(resolvPath));
            Assert.False(File.Exists(backupPath));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("Швеция, Стокгольм (Cloudflare)", "obxodka.one", "shv-sto-cf")]
    [InlineData("Швеция, Стокгольм", "70.34.201.253", "shv-stok")]
    [InlineData("Netherlands, Amsterdam", "1.2.3.4", "net-amst")]
    public void BuildServerSlug_GeneratesReadableAsciiId(string location, string ip, string expected)
    {
        var dto = new obxodka.Models.VpnServerDto(ip, 443, location, true, 10);
        Assert.Equal(expected, CliApp.BuildServerSlug(dto));
    }

    [Fact]
    public void AnsiUi_CalculatesVisibleLengthWithoutEscapeCodes()
    {
        const string colored = "\x1b[1m\x1b[38;2;96;205;255mOBXODKA\x1b[0m";
        Assert.Equal(7, CliAnsiUi.VisibleLength(colored));
    }
}
