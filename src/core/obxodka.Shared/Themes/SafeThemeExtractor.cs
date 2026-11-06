namespace obxodka.Shared.Themes;

//безопасный экстрактор архивов тем с защитой от zip slip, zip-bomb, polyglot и исполняемых файлов
public static class SafeThemeExtractor
{
    //максимальный суммарный размер распакованной темы оформления (35 мб)
    private const long MaxTotalExtractionSizeBytes = 35 * 1024 * 1024;

    //разрешенные расширения файлов в теме оформления
    private static readonly FrozenSet<string> s_allowedExtensions =
        ((string[])[".json", ".png", ".jpg", ".jpeg", ".webp", ".mp4", ".webm", ".wav", ".mp3", ".ogg", ".ttf", ".otf"])
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    //сигнатуры внедренных веб-скриптов и командных оболочек для защиты от polyglot атак
    private static readonly byte[][] s_forbiddenPolyglotSignatures =
    [
        "<!doctype html"u8.ToArray(),
        "<html"u8.ToArray(),
        "<script"u8.ToArray(),
        "javascript:"u8.ToArray(),
        "vbscript:"u8.ToArray(),
        "<?php"u8.ToArray(),
        "powershell"u8.ToArray()
    ];

    //лимит записей в архиве для защиты от архивов с миллионами мелких файлов
    public const int MaxArchiveEntries = 100;

    //максимальный размер графического изображения для предотвращения декомпрессионных атак на память
    public const int MaxImageDimension = 4096;

    //проверка разрешенного расширения файла
    public static bool IsAllowedExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && s_allowedExtensions.Contains(extension);

    //получение лимита размера для конкретного типа файла
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

    //безопасная распаковка zip-архива темы с проверкой контрольных сумм, путей и сигнатур
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
        var headerBuffer = new byte[64];

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name) && (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')))
            {
                continue;
            }

            //проверка на символические ссылки в unix атрибутах
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

            //защита от zip slip (выход за пределы каталога темы)
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

            await using var entryStream = await entry.OpenAsync(ct).ConfigureAwait(false);

            //чтение заголовка файла для проверки magic-bytes
            var headerBytesRead = 0;
            while (headerBytesRead < headerBuffer.Length)
            {
                var read = await entryStream.ReadAsync(headerBuffer.AsMemory(headerBytesRead, headerBuffer.Length - headerBytesRead), ct).ConfigureAwait(false);
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
                await using var fileStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                await fileStream.WriteAsync(headerBuffer.AsMemory(0, headerBytesRead), ct).ConfigureAwait(false);

                var copyBuffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    int bytesRead;
                    while ((bytesRead = await entryStream.ReadAsync(copyBuffer.AsMemory(0, copyBuffer.Length), ct).ConfigureAwait(false)) > 0)
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

                        //проверка на коэффициент сжатия для защиты от zip-бомбы
                        if (entry.CompressedLength > 0 && fileBytesWritten > 256 * 1024 && (fileBytesWritten / entry.CompressedLength) > 100)
                        {
                            throw new SecurityException($"[ThemeSecurity] Блокировка Zip-бомбы: аномальный коэффициент сжатия файла '{entry.Name}'.");
                        }

                        if (ContainsPolyglotPayload(copyBuffer.AsSpan(0, bytesRead)))
                        {
                            throw new SecurityException($"[ThemeSecurity] Блокировка Полиглот-файла: обнаружен внедренный скрипт в '{entry.Name}'.");
                        }

                        await fileStream.WriteAsync(copyBuffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
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
                        //подавление ошибок очистки
                    }
                }
                throw;
            }
        }
    }

    //проверка наличия опасных внедренных скриптов в потоке данных (polyglot)
    public static bool ContainsPolyglotPayload(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
        {
            return false;
        }

        foreach (var sig in s_forbiddenPolyglotSignatures)
        {
            if (IndexOfIgnoreCaseAscii(data, sig) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    //быстрый регистронезависимый поиск последовательности байт ascii
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

    //проверка сигнатур magic-bytes для подтверждения типа файла и отсева вредоносных бинарников
    public static bool VerifyMagicBytes(string extension, byte[] buffer, int length)
    {
        if (length <= 0 || buffer == null || buffer.Length < length)
        {
            return false;
        }

        var span = buffer.AsSpan(0, length);

        if (ContainsPolyglotPayload(span))
        {
            return false;
        }

        //отсев исполняемых файлов dos / windows pe (mz)
        if (length >= 2 && span[0] == 0x4D && span[1] == 0x5A)
        {
            return false;
        }

        //отсев исполняемых файлов linux elf
        if (length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(span) == 0x7F454C46)
        {
            return false;
        }

        //отсев исполняемых файлов mac-o
        if (length >= 4)
        {
            var u32 = BinaryPrimitives.ReadUInt32BigEndian(span);
            if (u32 is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE)
            {
                return false;
            }
        }

        //отсев командных скриптов shebang (#!)
        if (length >= 2 && span[0] == 0x23 && span[1] == 0x21)
        {
            return false;
        }

        //отсев скомпилированных java-классов
        if (length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(span) == 0xCAFEBABE)
        {
            return false;
        }

        //отсев zip/jar архивов внутри ресурсов
        if (length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(span) == 0x504B0304)
        {
            return false;
        }

        //отсев ярлыков windows lnk
        if (length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(span) == 0x0000004C)
        {
            return false;
        }

        var ext = extension.ToLowerInvariant();
        return ext switch
        {
            ".png" => VerifyPngHeaderAndDimensions(span),

            ".jpg" or ".jpeg" => length >= 3 && span[0] == 0xFF && span[1] == 0xD8 && span[2] == 0xFF,

            ".wav" => length >= 12 &&
                        BinaryPrimitives.ReadUInt32BigEndian(span) == 0x52494646 &&
                        BinaryPrimitives.ReadUInt32BigEndian(span[8..]) == 0x57415645,

            ".ogg" => length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(span) == 0x4F676753,

            ".mp4" => length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(span[4..]) == 0x66747970,

            ".webm" => length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(span) == 0x1A45DFA3,

            ".webp" => length >= 12 &&
                        BinaryPrimitives.ReadUInt32BigEndian(span) == 0x52494646 &&
                        BinaryPrimitives.ReadUInt32BigEndian(span[8..]) == 0x57454250,

            ".mp3" => (length >= 3 && span[0] == 0x49 && span[1] == 0x44 && span[2] == 0x33) ||
                        (length >= 2 && span[0] == 0xFF && (span[1] & 0xE0) == 0xE0),

            ".ttf" => length >= 4 && (
                        BinaryPrimitives.ReadUInt32BigEndian(span) == 0x00010000 ||
                        BinaryPrimitives.ReadUInt32BigEndian(span) == 0x74727565),

            ".otf" => length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(span) == 0x4F54544F,

            ".json" => IsValidJsonHeader(span),

            _ => false
        };
    }

    //проверка структуры png заголовка и лимита габаритов ihdr
    private static bool VerifyPngHeaderAndDimensions(ReadOnlySpan<byte> span)
    {
        if (span.Length < 8 || BinaryPrimitives.ReadUInt64BigEndian(span) != 0x89504E470D0A1A0A)
        {
            return false;
        }

        if (span.Length >= 24 &&
            span[12] == (byte)'I' && span[13] == (byte)'H' && span[14] == (byte)'D' && span[15] == (byte)'R')
        {
            var width = BinaryPrimitives.ReadUInt32BigEndian(span[16..]);
            var height = BinaryPrimitives.ReadUInt32BigEndian(span[20..]);
            if (width == 0 || height == 0 || width > MaxImageDimension || height > MaxImageDimension)
            {
                return false;
            }
        }

        return true;
    }

    //проверка валидности структуры json заголовка
    private static bool IsValidJsonHeader(ReadOnlySpan<byte> span)
    {
        var offset = 0;
        //пропуск utf-8 bom если присутствует
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            offset = 3;
        }

        while (offset < span.Length && char.IsWhiteSpace((char)span[offset]))
        {
            offset++;
        }

        if (offset >= span.Length)
        {
            return false;
        }

        var firstChar = (char)span[offset];
        return firstChar is '{' or '[';
    }
}
