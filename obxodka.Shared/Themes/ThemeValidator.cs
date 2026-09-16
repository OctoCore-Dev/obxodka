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

    public static bool ValidateManifest(ThemeManifest manifest, [NotNullWhen(false)] out string? errorMessage)
    {
        if (manifest == null)
        {
            errorMessage = "Theme Manifest cannot be null.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Id) || !IsSafeId(manifest.Id))
        {
            errorMessage = $"Theme ID '{manifest.Id}' is invalid. Must contain only lowercase letters, digits, dashes, and underscores.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errorMessage = "Theme Name cannot be empty.";
            return false;
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
            manifest.Core.Colors.Error
        ];

        foreach (var color in optionalColors)
        {
            if (!string.IsNullOrEmpty(color) && !IsValidHexColor(color))
            {
                errorMessage = $"Invalid color HEX token in theme manifest: '{color}'";
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
            manifest.Core.Sounds?.Notification,
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
