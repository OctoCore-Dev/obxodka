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
}
