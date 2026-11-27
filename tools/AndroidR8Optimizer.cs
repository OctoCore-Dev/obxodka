#:property TargetFramework=net11.0
#:property ImplicitUsings=enable
#:property Nullable=enable
#:property PublishAot=false
#:property RestorePackagesWithLockFile=false
#:property RestoreLockedMode=false
#:property TreatWarningsAsErrors=false

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

//автономный .NET 11 file-based оптимизатор и аудитор R8 для Android (без .csproj и ps1)
return AndroidR8OptimizerApp.Run(args);

static partial class AndroidR8OptimizerApp
{
    private static readonly string[] s_ignoredPrefixes =
    [
        "java.", "javax.", "android.R", "dalvik.", "sun.", "com.sun."
    ];

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Использование: dotnet run tools/AndroidR8Optimizer.cs -- --generate ... | --audit <aab>");
            return 1;
        }

        return args[0] switch
        {
            "--generate" => RunGenerate(args[1..]),
            "--audit" => RunAudit(args[1..]),
            _ => Fail($"неизвестная команда: {args[0]}")
        };
    }

    //генерация точных proguard-правил из слинкованных .dll и нативных .so
    private static int RunGenerate(string[] args)
    {
        var opts = ParseArgs(args);
        if (!opts.TryGetValue("--dll-dirs", out var dllDirsRaw) ||
            !opts.TryGetValue("--output-jni", out var outputJni))
        {
            return Fail("обязательны параметры --dll-dirs и --output-jni");
        }

        opts.TryGetValue("--so-dirs", out var soDirsRaw);
        opts.TryGetValue("--output-xamarin-strict", out var outputXamarinStrict);
        opts.TryGetValue("--mapping-file", out var mappingFile);
        opts.TryGetValue("--acw-map", out var acwMapFile);
        opts.TryGetValue("--project-refs", out var projectRefsFile);

        var jniClasses = new SortedSet<string>(StringComparer.Ordinal);
        var dllCount = 0;
        var registerAttrHits = 0;
        var userStringHits = 0;
        var soHits = 0;
        var acwHits = 0;
        var projectRefHits = 0;

        //сканируем все слинкованные .dll через System.Reflection.Metadata
        foreach (var root in SplitPaths(dllDirsRaw))
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var dll in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
            {
                dllCount++;
                ScanAssemblyMetadata(dll, jniClasses, ref registerAttrHits, ref userStringHits);
            }
        }

        //сканируем нативные .so библиотеки на FindClass-вызовы в Java
        if (!string.IsNullOrWhiteSpace(soDirsRaw))
        {
            foreach (var root in SplitPaths(soDirsRaw))
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (var so in Directory.EnumerateFiles(root, "*.so", SearchOption.AllDirectories))
                {
                    if (so.EndsWith(".dbg.so", StringComparison.OrdinalIgnoreCase))
                        continue;
                    ScanNativeSo(so, jniClasses, ref soHits);
                }
            }
        }

        //добавляем все java-классы из acw-map.txt (формат: ManagedType;JavaType)
        if (!string.IsNullOrWhiteSpace(acwMapFile) && File.Exists(acwMapFile))
        {
            foreach (var line in File.ReadLines(acwMapFile))
            {
                var sep = line.IndexOf(';');
                if (sep <= 0 || sep >= line.Length - 1)
                    continue;

                var javaName = line[(sep + 1)..].Trim().Replace('/', '.');
                if (IsValidJavaClassName(javaName) && !IsFrameworkClass(javaName) && jniClasses.Add(javaName))
                    acwHits++;
            }
        }

        //подхватываем классы из всех proguard_project_references.cfg (если сгенерированы ILLink)
        if (!string.IsNullOrWhiteSpace(projectRefsFile))
        {
            foreach (var candidate in SplitPaths(projectRefsFile))
            {
                var files = Directory.Exists(candidate)
                    ? Directory.EnumerateFiles(candidate, "proguard_project_references.cfg", SearchOption.AllDirectories)
                    : File.Exists(candidate) ? [candidate] : [];

                foreach (var f in files)
                {
                    foreach (var line in File.ReadLines(f))
                    {
                        var trimmed = line.Trim();
                        if (!trimmed.StartsWith("-keep class ", StringComparison.Ordinal))
                            continue;

                        var rest = trimmed["-keep class ".Length..].Trim();
                        var spaceIdx = rest.IndexOf(' ');
                        var javaName = spaceIdx > 0 ? rest[..spaceIdx] : rest;
                        if (IsValidJavaClassName(javaName) && !IsFrameworkClass(javaName) && jniClasses.Add(javaName))
                            projectRefHits++;
                    }
                }
            }
        }

        WriteJniRulesFile(outputJni, jniClasses);

        if (!string.IsNullOrWhiteSpace(outputXamarinStrict))
        {
            WriteStrictXamarinConfig(outputXamarinStrict, mappingFile);
        }

        Console.WriteLine(
            $"[ANDROID-R8-OPT] Scanned {dllCount} DLLs | Exact JNI classes={jniClasses.Count} " +
            $"(Register={registerAttrHits}, UserStrings={userStringHits}, NativeSo={soHits}, ACW={acwHits}, ProjRefs={projectRefHits}) -> {outputJni}");

        return 0;
    }

    //чтение метаданных .net сборки без загрузки в память рантайма
    private static void ScanAssemblyMetadata(
        string dllPath,
        SortedSet<string> classes,
        ref int registerAttrHits,
        ref int userStringHits)
    {
        try
        {
            using var fs = File.OpenRead(dllPath);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata)
                return;

            var reader = pe.GetMetadataReader();

            //читаем все атрибуты [Register(...)] и [JniTypeSignature(...)]
            foreach (var caHandle in reader.CustomAttributes)
            {
                var ca = reader.GetCustomAttribute(caHandle);
                var attrName = GetAttributeTypeName(reader, ca.Constructor);
                if (attrName is not ("RegisterAttribute" or "JniTypeSignatureAttribute" or "JniSignatureAttribute"))
                    continue;

                var blob = reader.GetBlobReader(ca.Value);
                if (blob.Length < 4 || blob.ReadUInt16() != 0x0001)
                    continue;

                //первый строковый аргумент — jni-имя класса или метода
                var first = TryReadSerString(ref blob);
                if (!string.IsNullOrEmpty(first))
                {
                    if (TryAddJniNameOrDescriptor(first, classes))
                        registerAttrHits++;
                }

                //второй строковый аргумент — jni-сигнатура метода с типами параметров и возврата
                var second = TryReadSerString(ref blob);
                if (!string.IsNullOrEmpty(second))
                {
                    ExtractDescriptorsFromSignature(second, classes, ref registerAttrHits);
                }
            }

            //читаем кучу пользовательских строк (JniPeerMembers, FindClass, Class.ForName)
            var usHandle = MetadataTokens.UserStringHandle(1);
            while (!usHandle.IsNil)
            {
                var str = reader.GetUserString(usHandle);
                if (str.Length is >= 8 and <= 320 && str.Contains('/'))
                {
                    if (str.Contains('(') || str.Contains(';'))
                    {
                        ExtractDescriptorsFromSignature(str, classes, ref userStringHits);
                    }
                    else if (TryAddJniSlashName(str, classes))
                    {
                        userStringHits++;
                    }
                }

                usHandle = reader.GetNextHandle(usHandle);
            }
        }
        catch (BadImageFormatException)
        {
            //нативная или ресурсная сборка без cil-метаданных
        }
    }

    //поиск нуль-терминированных jni-имен классов в нативных .so
    private static void ScanNativeSo(string soPath, SortedSet<string> classes, ref int soHits)
    {
        var bytes = File.ReadAllBytes(soPath);
        var ascii = Encoding.ASCII.GetString(bytes);
        foreach (Match m in NativeFindClassRegex().Matches(ascii))
        {
            var raw = m.Groups[1].Value;
            if (TryAddJniSlashName(raw, classes))
                soHits++;
        }
    }

    private static string? GetAttributeTypeName(MetadataReader reader, EntityHandle ctorHandle)
    {
        if (ctorHandle.Kind == HandleKind.MemberReference)
        {
            var mr = reader.GetMemberReference((MemberReferenceHandle)ctorHandle);
            return GetTypeName(reader, mr.Parent);
        }

        if (ctorHandle.Kind == HandleKind.MethodDefinition)
        {
            var md = reader.GetMethodDefinition((MethodDefinitionHandle)ctorHandle);
            return GetTypeName(reader, md.GetDeclaringType());
        }

        return null;
    }

    private static string? GetTypeName(MetadataReader reader, EntityHandle typeHandle)
    {
        return typeHandle.Kind switch
        {
            HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)typeHandle).Name),
            HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)typeHandle).Name),
            _ => null
        };
    }

    private static string? TryReadSerString(ref BlobReader blob)
    {
        if (blob.RemainingBytes <= 0)
            return null;

        var peek = blob.ReadByte();
        if (peek == 0xFF)
            return null;

        blob.Offset--;
        if (!blob.TryReadCompressedInteger(out var byteLen) || byteLen <= 0 || byteLen > blob.RemainingBytes)
            return null;

        return blob.ReadUTF8(byteLen);
    }

    private static void ExtractDescriptorsFromSignature(string sig, SortedSet<string> classes, ref int hits)
    {
        foreach (Match m in JniDescriptorRegex().Matches(sig))
        {
            if (TryAddJniSlashName(m.Groups[1].Value, classes))
                hits++;
        }
    }

    private static bool TryAddJniNameOrDescriptor(string value, SortedSet<string> classes)
    {
        var added = false;
        if (value.Contains('(') || value.Contains(';'))
        {
            var tmp = 0;
            ExtractDescriptorsFromSignature(value, classes, ref tmp);
            return tmp > 0;
        }

        if (value.Contains('/'))
        {
            added = TryAddJniSlashName(value, classes);
        }

        return added;
    }

    private static bool TryAddJniSlashName(string rawSlash, SortedSet<string> classes)
    {
        var raw = rawSlash.Trim();
        if (raw.StartsWith('L') && raw.EndsWith(';'))
            raw = raw[1..^1];
        if (raw.StartsWith('['))
            raw = raw.TrimStart('[');
        if (raw.StartsWith('L') && raw.EndsWith(';'))
            raw = raw[1..^1];

        if (raw.Length is < 8 or > 220 || raw.Contains("//", StringComparison.Ordinal) || raw.EndsWith('$'))
            return false;

        //отсекаем пути к файлам, url и строковые шаблоны
        if (raw.Contains(' ') || raw.Contains(':') || raw.Contains('.') || raw.Contains('\\'))
            return false;

        var slashCount = 0;
        foreach (var ch in raw)
        {
            if (ch == '/')
                slashCount++;
            else if (!char.IsLetterOrDigit(ch) && ch is not ('_' or '$'))
                return false;
        }

        if (slashCount < 2)
            return false;

        var cls = raw.Replace('/', '.');
        if (!IsValidJavaClassName(cls) || IsFrameworkClass(cls))
            return false;

        var added = classes.Add(cls);

        //для вложенных классов защищаем также внешний класс-контейнер
        var dollarIdx = cls.IndexOf('$');
        if (dollarIdx > 0)
        {
            var outer = cls[..dollarIdx];
            if (IsValidJavaClassName(outer) && !IsFrameworkClass(outer))
                classes.Add(outer);
        }

        return added;
    }

    private static bool IsValidJavaClassName(string cls)
    {
        if (cls.Length is < 5 or > 220)
            return false;

        var lastDot = cls.LastIndexOf('.');
        if (lastDot <= 0 || lastDot >= cls.Length - 1)
            return false;

        var simple = cls[(lastDot + 1)..];
        if (simple.Length == 0)
            return false;

        //пропускаем чисто нижний регистр в конце (обычно это имя пакета, а не класса)
        if (!char.IsUpper(simple[0]) && !cls.StartsWith("crc64", StringComparison.Ordinal))
            return false;

        //пропускаем системные android.* кроме android.support.*
        if (cls.StartsWith("android.", StringComparison.Ordinal) &&
            !cls.StartsWith("android.support.", StringComparison.Ordinal) &&
            !cls.StartsWith("android.runtime.", StringComparison.Ordinal))
            return false;

        return true;
    }

    private static bool IsFrameworkClass(string cls)
    {
        foreach (var prefix in s_ignoredPrefixes)
        {
            if (cls.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    //запись точных правил jni без allowshrinking, чтобы r8 не инлайнил static compat классы
    private static void WriteJniRulesFile(string outputPath, SortedSet<string> classes)
    {
        EnsureDir(outputPath);
        var sb = new StringBuilder(classes.Count * 88 + 512);
        sb.AppendLine("# Auto-generated by tools/AndroidR8Optimizer.cs (.NET 11 System.Reflection.Metadata + native .so scanner)");
        sb.AppendLine($"# Exact protected JNI/native classes: {classes.Count}");
        foreach (var cls in classes)
        {
            sb.Append("-keep,allowoptimization class ").Append(cls).AppendLine(" { public protected *; }");
        }

        File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
    }

    //генерация чистого proguard_xamarin_strict.cfg без глобальных правил class **
    private static void WriteStrictXamarinConfig(string outputPath, string? mappingFile)
    {
        EnsureDir(outputPath);
        var sb = new StringBuilder(2048);
        sb.AppendLine("# Strict .NET Android runtime ProGuard config generated by tools/AndroidR8Optimizer.cs");
        sb.AppendLine("# Removes global '-keep class **' so R8 can obfuscate unused internal library classes while preserving .NET runtime & JNI contracts.");
        sb.AppendLine();
        sb.AppendLine("-keep class android.support.multidex.MultiDexApplication { <init>(); }");
        sb.AppendLine("-keep class net.dot.jni.** { *; <init>(); }");
        sb.AppendLine("-keep class mono.MonoRuntimeProvider* { *; <init>(...); }");
        sb.AppendLine("-keep class mono.MonoPackageManager { *; <init>(...); }");
        sb.AppendLine("-keep class mono.MonoPackageManager_Resources { *; <init>(...); }");
        sb.AppendLine("-keep class mono.android.** { *; <init>(...); }");
        sb.AppendLine("-keep class mono.java.** { *; <init>(...); }");
        sb.AppendLine("-keep class mono.javax.** { *; <init>(...); }");
        sb.AppendLine("-keep class net.dot.jni.ManagedPeer { *; <init>(...); }");
        sb.AppendLine("-keep class xamarin.android.net.ServerCertificateCustomValidator_TrustManager { *; <init>(...); }");
        sb.AppendLine("-keep class xamarin.android.net.ServerCertificateCustomValidator_TrustManager_FakeSSLSession { *; <init>(...); }");
        sb.AppendLine("-keep class xamarin.android.net.ServerCertificateCustomValidator_AlwaysAcceptingHostnameVerifier { *; <init>(...); }");
        sb.AppendLine("-keep class android.runtime.** { <init>(...); }");
        sb.AppendLine("-keep class assembly_mono_android.android.runtime.** { <init>(...); }");
        sb.AppendLine("-keep class md52ce486a14f4bcd95899665e9d932190b.** { *; <init>(...); }");
        sb.AppendLine("-keepclassmembers class md52ce486a14f4bcd95899665e9d932190b.** { *; <init>(...); }");
        sb.AppendLine("-keep class net.dot.android.ApplicationRegistration { *; }");
        sb.AppendLine("-keep class net.dot.android.crypto.** { *; <init>(...); }");
        sb.AppendLine();
        sb.AppendLine("# сохранение сеттеров и xml-конструкторов для Android View");
        sb.AppendLine("-keepclassmembers class * extends android.view.View {");
        sb.AppendLine("   *** set*(...);");
        sb.AppendLine("   <init>(android.content.Context,android.util.AttributeSet);");
        sb.AppendLine("   <init>(android.content.Context,android.util.AttributeSet,int);");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("-ignorewarnings");
        sb.AppendLine("-keepattributes SourceFile");
        sb.AppendLine("-keepattributes LineNumberTable");
        if (!string.IsNullOrWhiteSpace(mappingFile))
        {
            var normMap = mappingFile.Replace('\\', '/');
            sb.Append("-printmapping \"").Append(normMap).AppendLine("\"");
        }

        File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
    }

    //комплексный аудит готового .aab: r8 >= 25%, 16kb elf, debug symbols и проверка jni в proguard.map
    private static int RunAudit(string[] args)
    {
        if (args.Length == 0 || !File.Exists(args[0]))
            return Fail("укажите путь к существующему .aab файлу после --audit");

        var aabPath = args[0];
        var opts = ParseArgs(args[1..]);
        var minPct = opts.TryGetValue("--min-r8", out var minRaw) && double.TryParse(minRaw, CultureInfo.InvariantCulture, out var parsed) ? parsed : 25.0;

        using var zip = ZipFile.OpenRead(aabPath);

        //1. проверка r8.json
        var r8Entry = zip.GetEntry("BUNDLE-METADATA/com.android.tools/r8.json");
        if (r8Entry is null)
            return Fail("в AAB отсутствует BUNDLE-METADATA/com.android.tools/r8.json");

        using var r8Doc = JsonDocument.Parse(r8Entry.Open());
        var stats = r8Doc.RootElement.GetProperty("stats");
        var optPct = 100.0 - stats.GetProperty("noOptimizationPercentage").GetDouble();
        var shrinkPct = 100.0 - stats.GetProperty("noShrinkingPercentage").GetDouble();
        var obfPct = 100.0 - stats.GetProperty("noObfuscationPercentage").GetDouble();

        long dexBytes = 0;
        foreach (var df in r8Doc.RootElement.GetProperty("dexFiles").EnumerateArray())
            dexBytes += df.GetProperty("sizeInBytes").GetInt64();

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"[R8] DEX={dexBytes / 1048576.0:F2}MB | Optimization={optPct:F2}% | Shrinking={shrinkPct:F2}% | Obfuscation={obfPct:F2}%"));
        if (optPct < minPct || shrinkPct < minPct || obfPct < minPct)
            return Fail($"метрика R8 ниже порога {minPct}%");

        //2. проверка 16kb elf alignment всех 64-bit .so
        var okElf = 0;
        var badElf = new List<string>();
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!entry.FullName.Contains("/arm64-v8a/", StringComparison.Ordinal) &&
                !entry.FullName.Contains("/x86_64/", StringComparison.Ordinal))
                continue;

            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var data = ms.ToArray();
            if (data.Length < 64 || data[0] != 0x7F || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'F' || data[4] != 2)
                continue;

            var phoff = (int)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(32, 8));
            var phentsize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(54, 2));
            var phnum = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(56, 2));
            ulong? minAlign = null;
            for (var i = 0; i < phnum; i++)
            {
                var off = phoff + i * phentsize;
                if (off + 56 > data.Length)
                    break;
                var pType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off, 4));
                if (pType == 1) //PT_LOAD
                {
                    var align = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off + 48, 8));
                    if (minAlign is null || align < minAlign.Value)
                        minAlign = align;
                }
            }

            if (minAlign is < 16384)
                badElf.Add($"{entry.FullName} ({minAlign}B)");
            else
                okElf++;
        }

        Console.WriteLine($"[16KB] 64-bit .so ELF check: OK={okElf}, BAD={badElf.Count}");
        if (badElf.Count > 0)
            return Fail("обнаружены невыровненные .so: " + string.Join(", ", badElf));

        //3. проверка proguard.map и нативных отладочных символов
        var mapEntry = zip.GetEntry("BUNDLE-METADATA/com.android.tools.build.obfuscation/proguard.map");
        var dbgSymbols = zip.Entries
            .Where(e => e.FullName.StartsWith("BUNDLE-METADATA/com.android.tools.build.debugsymbols/", StringComparison.Ordinal))
            .Select(e => e.FullName)
            .ToArray();
        Console.WriteLine($"[SYMBOLS] proguard.map={mapEntry is not null}, native debug symbols={dbgSymbols.Length}");
        if (mapEntry is null || dbgSymbols.Length == 0)
            return Fail("отсутствует proguard.map или нативные отладочные символы");

        //4. сверка статистики классов в proguard.map и проверка jni-классов из сгенерированного списка
        var clsMap = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var sr = new StreamReader(mapEntry.Open(), Encoding.UTF8))
        {
            while (sr.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] == ' ' || line[0] == '#' || !line.EndsWith(':'))
                    continue;
                var arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow <= 0)
                    continue;
                var orig = line[..arrow];
                var obf = line[(arrow + 4)..^1];
                clsMap[orig] = obf;
            }
        }

        var keptCls = clsMap.Count(kv => kv.Key == kv.Value);
        var obfCls = clsMap.Count(kv => kv.Key != kv.Value && !kv.Value.Contains("R8$$REMOVED", StringComparison.Ordinal));
        var remCls = clsMap.Count(kv => kv.Value.Contains("R8$$REMOVED", StringComparison.Ordinal));
        Console.WriteLine($"[MAP] Total={clsMap.Count} | Kept={keptCls} | Obfuscated={obfCls} | InlinedRemoved={remCls}");

        if (opts.TryGetValue("--jni-rules", out var jniRulesFile) && File.Exists(jniRulesFile))
        {
            var checkedRules = 0;
            var renamedOrRemoved = new List<string>();
            foreach (var line in File.ReadLines(jniRulesFile))
            {
                if (!line.StartsWith("-keep,allowoptimization class ", StringComparison.Ordinal))
                    continue;
                var rest = line["-keep,allowoptimization class ".Length..];
                var sp = rest.IndexOf(' ');
                var cls = sp > 0 ? rest[..sp] : rest;
                if (clsMap.TryGetValue(cls, out var mapped))
                {
                    checkedRules++;
                    if (mapped != cls)
                        renamedOrRemoved.Add($"{cls} -> {mapped}");
                }
            }

            Console.WriteLine($"[JNI-VERIFY] Verified={checkedRules} | RenamedOrRemoved={renamedOrRemoved.Count}");
            if (renamedOrRemoved.Count > 0)
            {
                foreach (var bad in renamedOrRemoved.Take(15))
                    Console.Error.WriteLine($"  BROKEN JNI: {bad}");
                return Fail("обнаружены переименованные или удаленные JNI-классы!");
            }
        }

        return 0;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    dict[args[i]] = args[++i];
                else
                    dict[args[i]] = "true";
            }
        }

        return dict;
    }

    private static IEnumerable<string> SplitPaths(string raw) =>
        raw.Split([';', '|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void EnsureDir(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[ANDROID-R8-OPT ERROR] {message}");
        return 1;
    }

    [GeneratedRegex(@"L([a-zA-Z0-9_]+(?:/[a-zA-Z0-9_$]+)+);", RegexOptions.CultureInvariant)]
    private static partial Regex JniDescriptorRegex();

    [GeneratedRegex(@"\x00((?:com|org|net|androidx|crc64|mono|obxodka|skiasharp|plugin|communitytoolkit)/[a-zA-Z0-9_/$]{4,120})\x00", RegexOptions.CultureInvariant)]
    private static partial Regex NativeFindClassRegex();
}
