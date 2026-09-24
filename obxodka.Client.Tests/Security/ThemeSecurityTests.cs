#pragma warning disable CA1707, IDE0065
using System.IO.Compression;
using System.Security;

namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class ThemeSecurityTests
{
    [Theory]
    [InlineData(".png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    [InlineData(".jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]
    [InlineData(".jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xDB })]
    [InlineData(".webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 })]
    [InlineData(".mp4", new byte[] { 0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70 })]
    [InlineData(".webm", new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })]
    [InlineData(".wav", new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45 })]
    [InlineData(".mp3", new byte[] { 0x49, 0x44, 0x33, 0x04 })]
    [InlineData(".mp3", new byte[] { 0xFF, 0xFB, 0x90, 0x44 })]
    [InlineData(".ttf", new byte[] { 0x00, 0x01, 0x00, 0x00 })]
    [InlineData(".ttf", new byte[] { 0x74, 0x72, 0x75, 0x65 })]
    [InlineData(".otf", new byte[] { 0x4F, 0x54, 0x54, 0x4F })]
    public void VerifyMagicBytes_ValidSignatures_ReturnsTrue(string extension, byte[] header)
    {
        var isValid = SafeThemeExtractor.VerifyMagicBytes(extension, header, header.Length);
        Assert.True(isValid);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".webp")]
    [InlineData(".mp4")]
    [InlineData(".webm")]
    [InlineData(".wav")]
    [InlineData(".mp3")]
    public void VerifyMagicBytes_DosPeExecutableHeader_AlwaysRejected(string extension)
    {
        var fakeExe = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00 };
        var isValid = SafeThemeExtractor.VerifyMagicBytes(extension, fakeExe, fakeExe.Length);
        Assert.False(isValid);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".mp4")]
    [InlineData(".webm")]
    public void VerifyMagicBytes_ElfExecutableHeader_AlwaysRejected(string extension)
    {
        var elfBinary = new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00 };
        var isValid = SafeThemeExtractor.VerifyMagicBytes(extension, elfBinary, elfBinary.Length);
        Assert.False(isValid);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".mp4")]
    public void VerifyMagicBytes_ShebangScript_AlwaysRejected(string extension)
    {
        var script = Encoding.UTF8.GetBytes("#!/bin/bash\nrm -rf /\n");
        var isValid = SafeThemeExtractor.VerifyMagicBytes(extension, script, script.Length);
        Assert.False(isValid);
    }

    [Fact]
    public void VerifyMagicBytes_ZeroLengthBuffer_ReturnsFalse()
    {
        Assert.False(SafeThemeExtractor.VerifyMagicBytes(".png", [], 0));
        Assert.False(SafeThemeExtractor.VerifyMagicBytes(".mp4", [], 0));
        Assert.False(SafeThemeExtractor.VerifyMagicBytes(".json", [], 0));
    }

    [Fact]
    public void VerifyMagicBytes_JsonValidation()
    {
        var validJson = "{\n  \"name\": \"Test Theme\"\n}"u8.ToArray();
        Assert.True(SafeThemeExtractor.VerifyMagicBytes(".json", validJson, validJson.Length));

        var invalidJson = "MZ\x90\x00SomeBinaryContent"u8.ToArray();
        Assert.False(SafeThemeExtractor.VerifyMagicBytes(".json", invalidJson, invalidJson.Length));
    }

    [Theory]
    [InlineData("assets/bg.jpg", true)]
    [InlineData("assets/video.mp4", true)]
    [InlineData("assets/btn.webp", true)]
    [InlineData("assets/sound.wav", true)]
    [InlineData("assets/anim.webm", true)]
    [InlineData("../evil.mp4", false)]
    [InlineData("assets/../../evil.png", false)]
    [InlineData("..\\windows\\system32\\cmd.exe", false)]
    [InlineData("C:/Windows/System32/calc.exe", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("script.sh", false)]
    [InlineData("app.exe", false)]
    [InlineData("payload.dll", false)]
    [InlineData("vector.svg", false)]
    public void IsSafeRelativePath_ValidatesCorrectly(string path, bool expectedSafe)
    {
        var isSafe = ThemeValidator.IsSafeRelativePath(path);
        Assert.Equal(expectedSafe, isSafe);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.0", "1.1.0", true)]
    [InlineData("1.0.0", "2.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("2.0.0", "1.9.9", false)]
    [InlineData("v1.0.0", "v1.0.2", true)]
    [InlineData("1.0", "1.1", true)]
    [InlineData("1.0.0-beta", "1.0.1", true)]
    [InlineData(null, "1.0.0", true)]
    [InlineData("1.0.0", null, false)]
    [InlineData("", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0.0", false)]
    public void IsVersionNewer_EvaluatesCorrectly(string? current, string? remote, bool expectedNewer)
    {
        var isNewer = ThemeValidator.IsVersionNewer(current, remote);
        Assert.Equal(expectedNewer, isNewer);
    }

    [Fact]
    public void ValidateManifest_PathTraversalInVideo_FailsValidation()
    {
        var manifest = new ThemeManifest
        {
            Id = "valid-theme",
            Name = "Valid Theme",
            Background = new ThemeBackground
            {
                Type = "video",
                VideoSource = "../../../windows/system32/calc.exe"
            }
        };
        manifest.Core.Colors.Primary = "#FF0000";
        manifest.Core.Colors.BgBase = "#000000";

        var isValid = ThemeValidator.ValidateManifest(manifest, out var error);
        Assert.False(isValid);
        Assert.NotNull(error);
        Assert.Contains("Unsafe or prohibited asset path", error);
    }

    [Fact]
    public void ValidateManifest_DangerousId_FailsValidation()
    {
        var manifest = new ThemeManifest
        {
            Id = "../evil_id",
            Name = "Evil Theme"
        };
        manifest.Core.Colors.Primary = "#FF0000";
        manifest.Core.Colors.BgBase = "#000000";

        var isValid = ThemeValidator.ValidateManifest(manifest, out var error);
        Assert.False(isValid);
        Assert.NotNull(error);
        Assert.Contains("Theme ID", error);
    }

    [Theory]
    [InlineData("CON.png")]
    [InlineData("nul.json")]
    [InlineData("assets/aux.mp4")]
    [InlineData("com1.webp")]
    [InlineData("lpt1.ttf")]
    public void IsSafeRelativePath_DosDeviceNames_AlwaysRejected(string dosPath) =>
        Assert.False(ThemeValidator.IsSafeRelativePath(dosPath));

    [Fact]
    public void ValidateManifest_NullManifestOrNullColors_ReturnsFalseWithoutThrowing()
    {
        Assert.False(ThemeValidator.ValidateManifest(null!, out var err1));
        Assert.NotNull(err1);

        var manifestWithoutColors = new ThemeManifest
        {
            Id = "valid-theme",
            Name = "Valid Name",
            Core = new CoreThemeTokens { Colors = null! }
        };
        Assert.False(ThemeValidator.ValidateManifest(manifestWithoutColors, out var err2));
        Assert.NotNull(err2);
    }

    [Fact]
    public void ValidateManifest_PathTraversalInMissingFields_FailsValidation()
    {
        var m1 = new ThemeManifest
        {
            Id = "test-theme",
            Name = "Test",
            Decorations = new ThemeDecorations { ScreenFrameMobile = "../../../windows/system32/calc.exe" }
        };
        Assert.False(ThemeValidator.ValidateManifest(m1, out var err1));
        Assert.Contains("Unsafe or prohibited asset path", err1);

        var m2 = new ThemeManifest
        {
            Id = "test-theme",
            Name = "Test",
            Decorations = new ThemeDecorations { ButtonRing = "../evil.png" }
        };
        Assert.False(ThemeValidator.ValidateManifest(m2, out var err2));
        Assert.Contains("Unsafe or prohibited asset path", err2);

        var m3 = new ThemeManifest
        {
            Id = "test-theme",
            Name = "Test",
            Core = new CoreThemeTokens { Sounds = new ThemeSounds { Notification = "../evil.wav" } }
        };
        Assert.False(ThemeValidator.ValidateManifest(m3, out var err3));
        Assert.Contains("Unsafe or prohibited asset path", err3);

        var m4 = new ThemeManifest
        {
            Id = "test-theme",
            Name = "Test",
            Core = new CoreThemeTokens { Sounds = new ThemeSounds { Disconnect = "../evil.wav" } }
        };
        Assert.False(ThemeValidator.ValidateManifest(m4, out var err4));
        Assert.Contains("Unsafe or prohibited asset path", err4);
    }

    [Fact]
    public async Task ExtractZipSafelyAsync_TooManyEntries_ThrowsSecurityExceptionAsync()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < SafeThemeExtractor.MaxArchiveEntries + 5; i++)
            {
                var entry = zip.CreateEntry($"file_{i}.json");
                using var s = entry.Open();
                s.Write("{}"u8);
            }
        }
        ms.Position = 0;

        var tempDir = Path.Combine(Path.GetTempPath(), "theme_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            _ = await Assert.ThrowsAsync<SecurityException>(() => SafeThemeExtractor.ExtractZipSafelyAsync(ms, tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task ExtractZipSafelyAsync_ZipSlipTraversal_ThrowsSecurityExceptionAsync()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("../../../evil.json");
            using var s = entry.Open();
            s.Write("{}"u8);
        }
        ms.Position = 0;

        var tempDir = Path.Combine(Path.GetTempPath(), "theme_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            _ = await Assert.ThrowsAsync<SecurityException>(() => SafeThemeExtractor.ExtractZipSafelyAsync(ms, tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task ExtractZipSafelyAsync_ProhibitedExtension_ThrowsSecurityExceptionAsync()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("payload.exe");
            using var s = entry.Open();
            s.Write([0x4D, 0x5A, 0x90, 0x00]);
        }
        ms.Position = 0;

        var tempDir = Path.Combine(Path.GetTempPath(), "theme_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            _ = await Assert.ThrowsAsync<SecurityException>(() => SafeThemeExtractor.ExtractZipSafelyAsync(ms, tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void ValidateManifest_FrameSlice_ValidatesBounds()
    {
        var validManifest = new ThemeManifest
        {
            Id = "valid-slice-theme",
            Name = "Valid Slice",
            Decorations = new ThemeDecorations
            {
                FrameSlice = new FrameSlice { Top = 40, Right = 40, Bottom = 40, Left = 40 },
                FrameSliceMobile = new FrameSlice { Top = 20, Right = 20, Bottom = 20, Left = 20 }
            }
        };
        Assert.True(ThemeValidator.ValidateManifest(validManifest, out var err1));
        Assert.Null(err1);

        var invalidManifest = new ThemeManifest
        {
            Id = "invalid-slice-theme",
            Name = "Invalid Slice",
            Decorations = new ThemeDecorations
            {
                FrameSlice = new FrameSlice { Top = -5, Right = 40, Bottom = 40, Left = 40 }
            }
        };
        Assert.False(ThemeValidator.ValidateManifest(invalidManifest, out var err2));
        Assert.Contains("Invalid frameSlice values", err2);

        var hugeManifest = new ThemeManifest
        {
            Id = "huge-slice-theme",
            Name = "Huge Slice",
            Decorations = new ThemeDecorations
            {
                FrameSliceMobile = new FrameSlice { Top = 10, Right = 5000, Bottom = 10, Left = 10 }
            }
        };
        Assert.False(ThemeValidator.ValidateManifest(hugeManifest, out var err3));
        Assert.Contains("Invalid frameSliceMobile values", err3);
    }

    [Fact]
    public void FrameSlice_JsonSerialization_WorksCorrectly()
    {
        var manifest = new ThemeManifest
        {
            Id = "slice-test",
            Name = "Slice Test",
            Decorations = new ThemeDecorations
            {
                FrameSlice = new FrameSlice { Top = 50, Right = 60, Bottom = 70, Left = 80 }
            }
        };

        var json = JsonSerializer.Serialize(manifest, ThemeJsonContext.Default.ThemeManifest);
        var deserialized = JsonSerializer.Deserialize(json, ThemeJsonContext.Default.ThemeManifest);

        Assert.NotNull(deserialized?.Decorations?.FrameSlice);
        Assert.Equal(50, deserialized.Decorations.FrameSlice.Top);
        Assert.Equal(60, deserialized.Decorations.FrameSlice.Right);
        Assert.Equal(70, deserialized.Decorations.FrameSlice.Bottom);
        Assert.Equal(80, deserialized.Decorations.FrameSlice.Left);
        Assert.True(deserialized.Decorations.FrameSlice.IsValid);
    }
}

