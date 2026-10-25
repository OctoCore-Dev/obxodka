namespace obxodka.Shared.Themes;

public static partial class ThemeValidator
{
    [GeneratedRegex("^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColorRegex();

    public static bool IsValidHexColor(string? hex) => !string.IsNullOrWhiteSpace(hex) && HexColorRegex().IsMatch(hex.Trim());

    [GeneratedRegex("^[a-z0-9\\-_]+$")]
    private static partial Regex SafeIdRegex();

    public static bool IsSafeId(string? id) => !string.IsNullOrWhiteSpace(id) && SafeIdRegex().IsMatch(id.Trim());

    private static readonly HashSet<string> t_reservedDeviceNames = new(
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ], StringComparer.OrdinalIgnoreCase);

    public static bool IsSafeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return true;
        }

        var normalized = relativePath.Replace('\\', '/').Trim();
        if (normalized.StartsWith('/') ||
            normalized.Contains("..", StringComparison.Ordinal) ||
            normalized.Contains(':', StringComparison.Ordinal) ||
            normalized.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(segment);
            if (t_reservedDeviceNames.Contains(nameWithoutExt) || t_reservedDeviceNames.Contains(segment))
            {
                return false;
            }
        }

        var ext = Path.GetExtension(normalized);
        return SafeThemeExtractor.IsAllowedExtension(ext);
    }

    public static bool ValidateAssetFile(string fullPath, [NotNullWhen(false)] out string? errorMessage)
    {
        if (!File.Exists(fullPath))
        {
            errorMessage = $"Asset file not found: '{fullPath}'";
            return false;
        }

        var ext = Path.GetExtension(fullPath);
        if (!SafeThemeExtractor.IsAllowedExtension(ext))
        {
            errorMessage = $"Asset has prohibited extension '{ext}': '{fullPath}'";
            return false;
        }

        try
        {
            var fileInfo = new FileInfo(fullPath);
            var maxAllowedSize = SafeThemeExtractor.GetMaxFileSize(ext);
            if (fileInfo.Length > maxAllowedSize)
            {
                errorMessage = $"Asset file '{fullPath}' ({fileInfo.Length} bytes) exceeds limit of {maxAllowedSize} bytes.";
                return false;
            }

            if (fileInfo.Length == 0)
            {
                errorMessage = $"Asset file '{fullPath}' is empty (0 bytes).";
                return false;
            }

            var readLength = (int)Math.Min(fileInfo.Length, 64);
            var buffer = new byte[readLength];
            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var bytesRead = fs.Read(buffer, 0, readLength);
                if (bytesRead < readLength)
                {
                    errorMessage = $"Unable to read asset header for '{fullPath}'.";
                    return false;
                }
            }

            if (!SafeThemeExtractor.VerifyMagicBytes(ext, buffer, readLength))
            {
                errorMessage = $"Asset file signature verification failed for '{fullPath}' (format '{ext}').";
                return false;
            }

            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Error inspecting asset file '{fullPath}': {ex.Message}";
            return false;
        }
    }

    public static bool ValidateAssetData(string extension, byte[] data, [NotNullWhen(false)] out string? errorMessage)
    {
        if (!SafeThemeExtractor.IsAllowedExtension(extension))
        {
            errorMessage = $"Prohibited asset extension: '{extension}'";
            return false;
        }

        if (data == null || data.Length == 0)
        {
            errorMessage = "Asset data is empty (0 bytes).";
            return false;
        }

        var maxAllowedSize = SafeThemeExtractor.GetMaxFileSize(extension);
        if (data.Length > maxAllowedSize)
        {
            errorMessage = $"Asset size ({data.Length} bytes) exceeds limit of {maxAllowedSize} bytes for '{extension}'.";
            return false;
        }

        if (!SafeThemeExtractor.VerifyMagicBytes(extension, data, data.Length))
        {
            errorMessage = $"Invalid file signature / magic bytes for '{extension}'.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    public static bool IsVersionNewer(string? currentVersion, string? remoteVersion)
    {
        if (string.IsNullOrWhiteSpace(remoteVersion))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            return true;
        }

        if (string.Equals(currentVersion.Trim(), remoteVersion.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var curClean = currentVersion.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        var remClean = remoteVersion.Trim().TrimStart('v', 'V').Split('-', '+')[0];

        var curParts = curClean.Split('.');
        var remParts = remClean.Split('.');

        var maxLen = Math.Max(curParts.Length, remParts.Length);
        for (var i = 0; i < maxLen; i++)
        {
            var curNum = i < curParts.Length && int.TryParse(curParts[i], out var cn) ? cn : 0;
            var remNum = i < remParts.Length && int.TryParse(remParts[i], out var rn) ? rn : 0;

            if (remNum > curNum)
            {
                return true;
            }

            if (remNum < curNum)
            {
                return false;
            }
        }

        return false;
    }

    public static bool ContainsDangerousText(string? value, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.Length > maxLength)
        {
            return true;
        }

        foreach (var ch in value)
        {
            if (char.IsControl(ch) && ch is not ('\r' or '\n' or '\t'))
            {
                return true;
            }
        }

        return value.Contains('<', StringComparison.Ordinal) ||
               value.Contains('>', StringComparison.Ordinal) ||
               value.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("vbscript:", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("data:text/html", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ValidateManifest(ThemeManifest manifest, [NotNullWhen(false)] out string? errorMessage)
    {
        if (manifest == null)
        {
            errorMessage = "Theme Manifest cannot be null.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.Length > 64 || !IsSafeId(manifest.Id))
        {
            errorMessage = $"Theme ID '{manifest.Id}' is invalid. Must contain only lowercase letters, digits, dashes, and underscores (max 64 chars).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Name) || ContainsDangerousText(manifest.Name, 80))
        {
            errorMessage = "Theme Name is empty, too long, or contains prohibited characters.";
            return false;
        }

        if (ContainsDangerousText(manifest.Author, 80) || ContainsDangerousText(manifest.Description, 600))
        {
            errorMessage = "Theme Author or Description contains prohibited characters or exceeds length limits.";
            return false;
        }

        if (manifest.Tags is { Count: > 20 })
        {
            errorMessage = "Theme cannot have more than 20 tags.";
            return false;
        }

        if (manifest.Tags is not null)
        {
            foreach (var tag in manifest.Tags)
            {
                if (ContainsDangerousText(tag, 32))
                {
                    errorMessage = $"Invalid tag '{tag}'.";
                    return false;
                }
            }
        }

        if (manifest.Core?.Colors == null)
        {
            errorMessage = "Theme Core Colors cannot be null.";
            return false;
        }

        if (!IsValidHexColor(manifest.Core.Colors.Primary))
        {
            errorMessage = $"Invalid Primary color HEX: '{manifest.Core.Colors.Primary}'";
            return false;
        }

        if (!IsValidHexColor(manifest.Core.Colors.BgBase))
        {
            errorMessage = $"Invalid BgBase color HEX: '{manifest.Core.Colors.BgBase}'";
            return false;
        }

        string?[] optionalColors =
        [
            manifest.Core.Colors.BgSurface,
            manifest.Core.Colors.BgElevated,
            manifest.Core.Colors.BgInput,
            manifest.Core.Colors.PrimaryBright,
            manifest.Core.Colors.PrimaryDim,
            manifest.Core.Colors.Accent,
            manifest.Core.Colors.TextPrimary,
            manifest.Core.Colors.TextSecondary,
            manifest.Core.Colors.TextMuted,
            manifest.Core.Colors.SolidBorderDark,
            manifest.Core.Colors.BorderSubtle,
            manifest.Core.Colors.BorderMedium,
            manifest.Core.Colors.Success,
            manifest.Core.Colors.Warning,
            manifest.Core.Colors.Error,
            manifest.Vfx?.Color,
            manifest.Vfx?.Glow?.CoreColor,
            manifest.Vfx?.Glow?.BloomColor,
            manifest.Vfx?.ActiveOverrides?.AccentColor,
            manifest.Vfx?.ActiveOverrides?.GlowColor
        ];

        foreach (var color in optionalColors)
        {
            if (!string.IsNullOrEmpty(color) && !IsValidHexColor(color))
            {
                errorMessage = $"Invalid color HEX token in theme manifest: '{color}'";
                return false;
            }
        }

        if (manifest.Vfx?.StatusText is { } st)
        {
            if (ContainsDangerousText(st.Disconnected, 64) ||
                ContainsDangerousText(st.Connecting, 64) ||
                ContainsDangerousText(st.Connected, 64) ||
                ContainsDangerousText(st.Error, 64) ||
                ContainsDangerousText(st.ButtonIdle, 32) ||
                ContainsDangerousText(st.ButtonConnecting, 32) ||
                ContainsDangerousText(st.ButtonConnected, 32))
            {
                errorMessage = "Invalid or prohibited text in vfx.statusText.";
                return false;
            }
        }

        string?[] assetPaths =
        [
            manifest.Background?.ImageSource,
            manifest.Background?.VideoSource,
            manifest.Background?.FallbackImage,
            manifest.Decorations?.ScreenFrame,
            manifest.Decorations?.ScreenFrameMobile,
            manifest.Decorations?.CardFrame,
            manifest.Decorations?.ButtonRing,
            manifest.Decorations?.ButtonImageIdle,
            manifest.Decorations?.ButtonImageConnecting,
            manifest.Decorations?.ButtonImageActive,
            manifest.Decorations?.ButtonImageError,
            manifest.Decorations?.ButtonVideoIdle,
            manifest.Decorations?.ButtonVideoConnecting,
            manifest.Decorations?.ButtonVideoActive,
            manifest.Decorations?.ButtonVideoError,
            manifest.Vfx?.ParticleSprite,
            manifest.Core.Sounds?.Connect,
            manifest.Core.Sounds?.Disconnect,
            manifest.Core.Sounds?.Click,
            manifest.Core.Sounds?.Hover,
            manifest.Core.Sounds?.Notification,
            manifest.Core.Sounds?.Ambient,
            manifest.Core.Ui?.FontFile
        ];

        foreach (var p in assetPaths)
        {
            if (!IsSafeRelativePath(p))
            {
                errorMessage = $"Unsafe or prohibited asset path in theme manifest: '{p}'";
                return false;
            }
        }

        if (manifest.Vfx?.ParticleSprites is { Count: > 0 } sprites)
        {
            if (sprites.Count > 12)
            {
                errorMessage = "Too many particleSprites in theme manifest (max 12).";
                return false;
            }

            foreach (var sp in sprites)
            {
                if (!IsSafeRelativePath(sp))
                {
                    errorMessage = $"Unsafe or prohibited particleSprite path in theme manifest: '{sp}'";
                    return false;
                }
            }
        }

        if (manifest.Decorations?.CornerStickers is { Count: > 0 } stickers)
        {
            foreach (var p in stickers.Values)
            {
                if (!IsSafeRelativePath(p))
                {
                    errorMessage = $"Unsafe or prohibited sticker path in theme manifest: '{p}'";
                    return false;
                }
            }
        }

        if (manifest.Apps is not null && manifest.Apps.TryGetValue("obxodka", out var obxApp) && obxApp.ValueKind == JsonValueKind.Object)
        {
            if (obxApp.TryGetProperty("sounds", out var soundsObj) && soundsObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in soundsObj.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String && !IsSafeRelativePath(prop.Value.GetString()))
                    {
                        errorMessage = $"Unsafe sound path in apps.obxodka.sounds.{prop.Name}: '{prop.Value.GetString()}'";
                        return false;
                    }
                }
            }

            if (obxApp.TryGetProperty("vpnButton", out var btnObj) && btnObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in btnObj.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String && !IsSafeRelativePath(prop.Value.GetString()))
                    {
                        errorMessage = $"Unsafe button path in apps.obxodka.vpnButton.{prop.Name}: '{prop.Value.GetString()}'";
                        return false;
                    }
                }
            }
        }

        if (manifest.Decorations?.FrameSlice is { } slice)
        {
            if (slice.Top < 0 || slice.Right < 0 || slice.Bottom < 0 || slice.Left < 0 ||
                slice.Top > 4096 || slice.Right > 4096 || slice.Bottom > 4096 || slice.Left > 4096)
            {
                errorMessage = "Invalid frameSlice values in theme manifest (must be between 0 and 4096).";
                return false;
            }
        }

        if (manifest.Decorations?.FrameSliceMobile is { } sliceMobile)
        {
            if (sliceMobile.Top < 0 || sliceMobile.Right < 0 || sliceMobile.Bottom < 0 || sliceMobile.Left < 0 ||
                sliceMobile.Top > 4096 || sliceMobile.Right > 4096 || sliceMobile.Bottom > 4096 || sliceMobile.Left > 4096)
            {
                errorMessage = "Invalid frameSliceMobile values in theme manifest (must be between 0 and 4096).";
                return false;
            }
        }

        errorMessage = null;
        return true;
    }

    public static (string startHex, string endHex) SynthesizeGradient(string baseHex)
    {
        if (!IsValidHexColor(baseHex))
        {
            return ("#0078D4", "#004578");
        }

        try
        {
            var hex = baseHex.TrimStart('#');
            if (hex.Length == 8)
            {
                hex = hex[2..];
            }

            var r = Convert.ToInt32(hex[..2], 16);
            var g = Convert.ToInt32(hex.Substring(2, 2), 16);
            var b = Convert.ToInt32(hex.Substring(4, 2), 16);

            var r2 = Math.Clamp((int)(r * 0.75), 0, 255);
            var g2 = Math.Clamp((int)(g * 0.70), 0, 255);
            var b2 = Math.Clamp((int)(b * 0.85), 0, 255);

            var start = $"#{r:X2}{g:X2}{b:X2}";
            var end = $"#{r2:X2}{g2:X2}{b2:X2}";
            return (start, end);
        }
        catch
        {
            return (baseHex, "#004578");
        }
    }
}
