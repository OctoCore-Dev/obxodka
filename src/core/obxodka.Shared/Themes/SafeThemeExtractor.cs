namespace obxodka.Shared.Themes;

public static class SafeThemeExtractor
{
    private const long MaxTotalExtractionSizeBytes = 35 * 1024 * 1024;

    private static readonly HashSet<string> t_allowedExtensions =
    [
        ".json", ".png", ".jpg", ".jpeg", ".webp", ".mp4", ".webm", ".wav", ".mp3", ".ogg", ".ttf", ".otf"
    ];

    private static readonly byte[][] t_forbiddenPolyglotSignatures =
    [
        "<!doctype html"u8.ToArray(),
        "<html"u8.ToArray(),
        "<script"u8.ToArray(),
        "javascript:"u8.ToArray(),
        "vbscript:"u8.ToArray(),
        "<?php"u8.ToArray(),
        "powershell"u8.ToArray()
    ];

    public static bool IsAllowedExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && t_allowedExtensions.Contains(extension.ToLowerInvariant());

    public static long GetMaxFileSize(string extension)
    {
        var ext = extension.ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".webm" => 20 * 1024 * 1024,
            ".wav" or ".mp3" or ".ogg" => 5 * 1024 * 1024,
            ".png" or ".jpg" or ".jpeg" or ".webp" => 5 * 1024 * 1024,
            ".ttf" or ".otf" => 5 * 1024 * 1024,
            ".json" => 512 * 1024,
            _ => 5 * 1024 * 1024
        };
    }

    public const int MaxArchiveEntries = 100;
    public const int MaxImageDimension = 4096;

    public static async Task ExtractZipSafelyAsync(Stream zipStream, string destinationDirectory, CancellationToken ct = default)
    {
        var targetFullPath = Path.GetFullPath(destinationDirectory);
        if (!targetFullPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            targetFullPath += Path.DirectorySeparatorChar;
        }

        _ = Directory.CreateDirectory(targetFullPath);

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaxArchiveEntries)
        {
            throw new SecurityException($"[ThemeSecurity] Блокировка: архив содержит слишком много файлов ({archive.Entries.Count} > {MaxArchiveEntries}).");
        }

        long totalBytesExtracted = 0;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name) && (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')))
            {
                continue;
            }

            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000)
            {
                throw new SecurityException($"[ThemeSecurity] Блокировка символической ссылки в архиве: '{entry.FullName}'");
            }

            if (!ThemeValidator.IsSafeRelativePath(entry.FullName))
            {
                throw new SecurityException($"[ThemeSecurity] Блокировка небезопасного пути или расширения в архиве: '{entry.FullName}'");
            }

            var extension = Path.GetExtension(entry.FullName);
            if (!IsAllowedExtension(extension))
            {
                throw new SecurityException($"[ThemeSecurity] Блокировка: запрещенное расширение файла '{extension}' в теме!");
            }

            var destinationFilePath = Path.GetFullPath(Path.Combine(targetFullPath, entry.FullName));
            if (!destinationFilePath.StartsWith(targetFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException($"[ThemeSecurity] Блокировка Zip Slip: попытка записи вне директории темы: '{entry.FullName}'");
            }

            var maxAllowedSize = GetMaxFileSize(extension);
            var parentDir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                _ = Directory.CreateDirectory(parentDir);
            }

            using var entryStream = entry.Open();

            var headerBuffer = new byte[64];
            var headerBytesRead = 0;
            while (headerBytesRead < headerBuffer.Length)
            {
                var read = await entryStream.ReadAsync(headerBuffer.AsMemory(headerBytesRead, headerBuffer.Length - headerBytesRead), ct);
                if (read == 0)
                {
                    break;
                }
                headerBytesRead += read;
            }

            if (headerBytesRead == 0)
            {
                throw new SecurityException($"[ThemeSecurity] Блокировка: пустой файл '{entry.Name}' (0 байт)!");
            }

            if (!VerifyMagicBytes(extension, headerBuffer, headerBytesRead))
            {
                throw new SecurityException($"[ThemeSecurity] Фальсификация файла: сигнатура файла '{entry.Name}' не соответствует формату {extension}!");
            }

            long fileBytesWritten = headerBytesRead;
            totalBytesExtracted += headerBytesRead;

            if (fileBytesWritten > maxAllowedSize)
            {
                throw new SecurityException($"[ThemeSecurity] Блокировка: файл '{entry.Name}' превышает лимит {maxAllowedSize / 1024 / 1024} МБ.");
            }

            if (totalBytesExtracted > MaxTotalExtractionSizeBytes)
            {
                throw new SecurityException($"[ThemeSecurity] Блокировка Zip-бомбы: общий размер распаковки превысил лимит {MaxTotalExtractionSizeBytes / 1024 / 1024} МБ.");
            }

            try
            {
                using var fileStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await fileStream.WriteAsync(headerBuffer.AsMemory(0, headerBytesRead), ct);

                var copyBuffer = ArrayPool<byte>.Shared.Rent(16384);
                try
                {
                    int bytesRead;
                    while ((bytesRead = await entryStream.ReadAsync(copyBuffer.AsMemory(0, copyBuffer.Length), ct)) > 0)
                    {
                        fileBytesWritten += bytesRead;
                        totalBytesExtracted += bytesRead;

                        if (fileBytesWritten > maxAllowedSize)
                        {
                            throw new SecurityException($"[ThemeSecurity] Блокировка: файл '{entry.Name}' превышает лимит {maxAllowedSize / 1024 / 1024} МБ.");
                        }

                        if (totalBytesExtracted > MaxTotalExtractionSizeBytes)
                        {
                            throw new SecurityException($"[ThemeSecurity] Блокировка Zip-бомбы: общий размер распаковки превысил лимит {MaxTotalExtractionSizeBytes / 1024 / 1024} МБ.");
                        }

                        if (entry.CompressedLength > 0 && fileBytesWritten > 256 * 1024 && (fileBytesWritten / entry.CompressedLength) > 100)
                        {
                            throw new SecurityException($"[ThemeSecurity] Блокировка Zip-бомбы: аномальный коэффициент сжатия файла '{entry.Name}'.");
                        }

                        if (ContainsPolyglotPayload(copyBuffer.AsSpan(0, bytesRead)))
                        {
                            throw new SecurityException($"[ThemeSecurity] Блокировка Полиглот-файла: обнаружен внедренный скрипт в '{entry.Name}'.");
                        }

                        await fileStream.WriteAsync(copyBuffer.AsMemory(0, bytesRead), ct);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(copyBuffer);
                }
            }
            catch
            {
                if (File.Exists(destinationFilePath))
                {
                    try
                    {
                        File.Delete(destinationFilePath);
                    }
                    catch
                    {
                    }
                }
                throw;
            }
        }
    }

    public static bool ContainsPolyglotPayload(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
        {
            return false;
        }

        foreach (var sig in t_forbiddenPolyglotSignatures)
        {
            if (IndexOfIgnoreCaseAscii(data, sig) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexOfIgnoreCaseAscii(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needleLower)
    {
        var limit = haystack.Length - needleLower.Length;
        for (var i = 0; i <= limit; i++)
        {
            var match = true;
            for (var j = 0; j < needleLower.Length; j++)
            {
                var b = haystack[i + j];
                if (b is >= (byte)'A' and <= (byte)'Z')
                {
                    b = (byte)(b + 32);
                }
                if (b != needleLower[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return i;
            }
        }
        return -1;
    }

    public static bool VerifyMagicBytes(string extension, byte[] buffer, int length)
    {
        if (length <= 0 || buffer == null || buffer.Length < length)
        {
            return false;
        }

        if (ContainsPolyglotPayload(buffer.AsSpan(0, length)))
        {
            return false;
        }

        if (length >= 2 && buffer[0] == 0x4D && buffer[1] == 0x5A)
        {
            return false;
        }

        if (length >= 4 && buffer[0] == 0x7F && buffer[1] == 0x45 && buffer[2] == 0x4C && buffer[3] == 0x46)
        {
            return false;
        }

        if (length >= 4)
        {
            if ((buffer[0] == 0xFE && buffer[1] == 0xED && buffer[2] == 0xFA && buffer[3] == 0xCE) ||
                (buffer[0] == 0xFE && buffer[1] == 0xED && buffer[2] == 0xFA && buffer[3] == 0xCF) ||
                (buffer[0] == 0xCE && buffer[1] == 0xFA && buffer[2] == 0xED && buffer[3] == 0xFE) ||
                (buffer[0] == 0xCF && buffer[1] == 0xFA && buffer[2] == 0xED && buffer[3] == 0xFE))
            {
                return false;
            }
        }

        if (length >= 2 && buffer[0] == 0x23 && buffer[1] == 0x21)
        {
            return false;
        }

        if (length >= 4 && buffer[0] == 0xCA && buffer[1] == 0xFE && buffer[2] == 0xBA && buffer[3] == 0xBE)
        {
            return false;
        }

        if (length >= 4 && buffer[0] == 0x50 && buffer[1] == 0x4B && buffer[2] == 0x03 && buffer[3] == 0x04)
        {
            return false;
        }

        if (length >= 4 && buffer[0] == 0x4C && buffer[1] == 0x00 && buffer[2] == 0x00 && buffer[3] == 0x00)
        {
            return false;
        }

        var ext = extension.ToLowerInvariant();
        return ext switch
        {
            ".png" => VerifyPngHeaderAndDimensions(buffer, length),

            ".jpg" or ".jpeg" => length >= 3 &&
                                 buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF,

            ".wav" => length >= 12 &&
                      buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46 &&
                      buffer[8] == 0x57 && buffer[9] == 0x41 && buffer[10] == 0x56 && buffer[11] == 0x45,

            ".ogg" => length >= 4 &&
                      buffer[0] == 0x4F && buffer[1] == 0x67 && buffer[2] == 0x67 && buffer[3] == 0x53,

            ".mp4" => length >= 8 &&
                      buffer[4] == 0x66 && buffer[5] == 0x74 && buffer[6] == 0x79 && buffer[7] == 0x70,

            ".webm" => length >= 4 &&
                       buffer[0] == 0x1A && buffer[1] == 0x45 && buffer[2] == 0xDF && buffer[3] == 0xA3,

            ".webp" => length >= 12 &&
                       buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46 &&
                       buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50,

            ".mp3" => (length >= 3 && buffer[0] == 0x49 && buffer[1] == 0x44 && buffer[2] == 0x33) ||
                      (length >= 2 && buffer[0] == 0xFF && (buffer[1] & 0xE0) == 0xE0),

            ".ttf" => length >= 4 && (
                      (buffer[0] == 0x00 && buffer[1] == 0x01 && buffer[2] == 0x00 && buffer[3] == 0x00) ||
                      (buffer[0] == 0x74 && buffer[1] == 0x72 && buffer[2] == 0x75 && buffer[3] == 0x65)),

            ".otf" => length >= 4 &&
                      buffer[0] == 0x4F && buffer[1] == 0x54 && buffer[2] == 0x54 && buffer[3] == 0x4F,

            ".json" => IsValidJsonHeader(buffer, length),

            _ => false
        };
    }

    private static bool VerifyPngHeaderAndDimensions(byte[] buffer, int length)
    {
        if (length < 8 ||
            buffer[0] != 0x89 || buffer[1] != 0x50 || buffer[2] != 0x4E || buffer[3] != 0x47 ||
            buffer[4] != 0x0D || buffer[5] != 0x0A || buffer[6] != 0x1A || buffer[7] != 0x0A)
        {
            return false;
        }

        if (length >= 24 &&
            buffer[12] == (byte)'I' && buffer[13] == (byte)'H' && buffer[14] == (byte)'D' && buffer[15] == (byte)'R')
        {
            var width = (uint)((buffer[16] << 24) | (buffer[17] << 16) | (buffer[18] << 8) | buffer[19]);
            var height = (uint)((buffer[20] << 24) | (buffer[21] << 16) | (buffer[22] << 8) | buffer[23]);
            if (width == 0 || height == 0 || width > MaxImageDimension || height > MaxImageDimension)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidJsonHeader(byte[] buffer, int length)
    {
        var offset = 0;
        if (length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            offset = 3;
        }

        while (offset < length && char.IsWhiteSpace((char)buffer[offset]))
        {
            offset++;
        }

        if (offset >= length)
        {
            return false;
        }

        var firstChar = (char)buffer[offset];
        return firstChar is '{' or '[';
    }
}
