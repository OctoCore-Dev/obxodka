namespace obxodka.Client.Tests.Services;

public class StoreVersionParserTests
{
    [Fact]
    public void ParseGooglePlayHtmlExtractsValidVersionAndWhatsNew()
    {
        var sampleHtml = @"
            <html><body>
                <script>AF_initDataCallback({key: 'ds:5', hash: '2', data:[null,[[[""5.0.1""]]],[[[36]],[[[29,""10""]]]]],""145"":[null,[null,""- Bug fixes\n- Performance improvement""]]});</script>
            </body></html>";

        var (version, whatsNew) = StoreVersionParser.ParseGooglePlayHtml(sampleHtml);

        Assert.Equal("5.0.1", version);
        Assert.NotNull(whatsNew);
        Assert.Contains("Bug fixes", whatsNew);
    }

    [Fact]
    public void ParseGooglePlayHtmlReturnsNullOnInvalidOrEmptyInput()
    {
        var (version1, whatsNew1) = StoreVersionParser.ParseGooglePlayHtml(null);
        var (version2, whatsNew2) = StoreVersionParser.ParseGooglePlayHtml("<html><body>No version info</body></html>");

        Assert.Null(version1);
        Assert.Null(whatsNew1);
        Assert.Null(version2);
        Assert.Null(whatsNew2);
    }

    [Fact]
    public void ParseMicrosoftStoreCatalogJsonDecodesMsixVersionCorrectly()
    {
        var rawVersionNumber = ((5UL << 48) | (0UL << 32) | (1UL << 16)).ToString(CultureInfo.InvariantCulture);

        var sampleJson = $@"{{
            ""Product"": {{
                ""DisplaySkuAvailabilities"": [
                    {{
                        ""Sku"": {{
                            ""Properties"": {{
                                ""Packages"": [
                                    {{
                                        ""Version"": ""{rawVersionNumber}""
                                    }}
                                ]
                            }}
                        }}
                    }}
                ]
            }}
        }}";

        var version = StoreVersionParser.ParseMicrosoftStoreCatalogJson(sampleJson);

        Assert.Equal("5.0.1", version);
    }

    [Fact]
    public void ParseMicrosoftStoreCatalogJsonReturnsNullOnMalformedInput()
    {
        var v1 = StoreVersionParser.ParseMicrosoftStoreCatalogJson(null);
        var v2 = StoreVersionParser.ParseMicrosoftStoreCatalogJson("{}");
        var v3 = StoreVersionParser.ParseMicrosoftStoreCatalogJson("{ invalid json }");

        Assert.Null(v1);
        Assert.Null(v2);
        Assert.Null(v3);
    }

    [Theory]
    [InlineData("5.0.0", "5.0.1", true)]
    [InlineData("4.8.6", "5.0.0", true)]
    [InlineData("4.9.0", "5.0.0", true)]
    [InlineData("4.9.0.0", "5.0.0", true)]
    [InlineData("4.9.0.0", "5.0.0.0", true)]
    [InlineData("5.0.0", "5.0.0.0", false)]
    [InlineData("5.0.0.0", "5.0.0", false)]
    [InlineData("5.0.0.0", "5.0.0.0", false)]
    [InlineData("5.0.0.0", "5.0.0.1", true)]
    [InlineData("5.0.0.1", "5.0.0.0", false)]
    [InlineData("v5.0.0", "v5.0.1", true)]
    [InlineData("5.0.0-rc1", "5.0.0", false)]
    [InlineData("5.0.0", "5.0.0", false)]
    [InlineData("5.1.0", "5.0.9", false)]
    [InlineData("5.0.0", null, false)]
    [InlineData(null, "5.0.0", true)]
    public void IsNewerVersionCorrectlyComparesSemVerStrings(string? current, string? latest, bool expectedResult)
    {
        var result = StoreVersionParser.IsNewerVersion(current, latest);
        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public void CleanReleaseNotesStripsChecksumBlockAndExtractsChangelog()
    {
        var rawBody = """
            ### [CHECKSUMS] SHA256 Distribution Verification
            ```
            5e1a48ecb3db521185962678cf4fed5f1a6d42a4bda1388219b4c68f283fe907  Obxodka-Android.apk
            f90f7988aef115bf54c596908d72cf944470de9dbf27ad398293da2fb30aaa70  Obxodka-Release.aab
            f38572da9f3f88b3ce9e75ce279c43fb3e448c7f855520e0c9bfa9a868696326  Obxodka-Setup.exe
            95302bf80af4e50f6cb12069a0f10d9dafe5c573ddc976ddb76c300fec0a44ee  obxodka.Maui_6.1.2.0_x64.msix
            ```

            ### [CHANGELOG] Release Notes
            - Новое ядро AdBlock: динамическое обновление фильтров
            - Оптимизация сетевого стека
            """;

        var cleaned = StoreVersionParser.CleanReleaseNotes(rawBody);

        Assert.NotNull(cleaned);
        Assert.DoesNotContain("CHECKSUMS", cleaned);
        Assert.DoesNotContain("5e1a48ec", cleaned);
        Assert.DoesNotContain("f38572da", cleaned);
        Assert.Contains("Новое ядро AdBlock", cleaned);
        Assert.Contains("Оптимизация сетевого стека", cleaned);
    }

    [Fact]
    public void ParseGitHubReleaseJsonExtractsVersionInstallerUrlExpectedShaAndCleanNotes()
    {
        var sampleJson = """
            {
              "tag_name": "v6.1.3",
              "body": "### [CHECKSUMS] SHA256 Distribution Verification\r\n```\r\nf38572da9f3f88b3ce9e75ce279c43fb3e448c7f855520e0c9bfa9a868696326  Obxodka-Setup.exe\r\n```\r\n\r\n### [CHANGELOG] Release Notes\r\n- Uplink Guard защита от блэкхола сети",
              "assets": [
                {
                  "name": "Obxodka-Setup.exe",
                  "browser_download_url": "https://github.com/OctoCore-Dev/obxodka/releases/download/v6.1.3/Obxodka-Setup.exe"
                }
              ]
            }
            """;

        var (version, whatsNew, downloadUrl, expectedSha256) = StoreVersionParser.ParseGitHubReleaseJson(sampleJson);

        Assert.Equal("6.1.3", version);
        Assert.Equal("https://github.com/OctoCore-Dev/obxodka/releases/download/v6.1.3/Obxodka-Setup.exe", downloadUrl);
        Assert.Equal("f38572da9f3f88b3ce9e75ce279c43fb3e448c7f855520e0c9bfa9a868696326", expectedSha256);
        Assert.NotNull(whatsNew);
        Assert.DoesNotContain("f38572da", whatsNew);
        Assert.Contains("Uplink Guard защита от блэкхола сети", whatsNew);
    }
}
