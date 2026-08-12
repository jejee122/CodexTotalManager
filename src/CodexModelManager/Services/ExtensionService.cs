using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public sealed class ExtensionService
{
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumPackageFiles = 10_000;
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private static readonly Regex IdPattern = new(
        "^[a-z0-9][a-z0-9._-]{1,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedCapabilities = new(StringComparer.Ordinal)
    {
        "network",
        "filesystem-read",
        "filesystem-write",
        "location",
        "microphone",
        "camera",
        "child-process"
    };
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, Process> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _statePath;
    private readonly string _dataRoot;

    public ExtensionService(string dataDirectory, string? extensionRoot = null)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("运行数据目录不能为空。", nameof(dataDirectory));

        RootDirectory = Path.GetFullPath(
            extensionRoot ?? Path.Combine(dataDirectory, "extensions"));
        PackagesDirectory = Path.Combine(RootDirectory, "packages");
        _dataRoot = Path.Combine(RootDirectory, "data");
        _statePath = Path.Combine(RootDirectory, "trusted-extensions.json");

        Directory.CreateDirectory(RootDirectory);
        RejectReparsePoint(RootDirectory, "插件根目录");
        Directory.CreateDirectory(PackagesDirectory);
        Directory.CreateDirectory(_dataRoot);
    }

    public string RootDirectory { get; }
    public string PackagesDirectory { get; }

    public ExtensionDiscoveryResult Discover()
    {
        RejectReparsePoint(RootDirectory, "插件根目录");
        RejectReparsePoint(PackagesDirectory, "插件包目录");

        var trust = ReadTrustStore(out var trustWarning);
        var candidates = new List<ExtensionPackage>();
        var issues = new List<ExtensionDiscoveryIssue>();

        foreach (var directory in Directory.EnumerateDirectories(PackagesDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var folderName = Path.GetFileName(directory);
            try
            {
                var package = ReadPackage(directory, trust);
                candidates.Add(package);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or CryptographicException)
            {
                issues.Add(new ExtensionDiscoveryIssue(folderName, ex.Message));
            }
        }

        var duplicateIds = candidates
            .GroupBy(package => package.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (duplicateIds.Count > 0)
        {
            foreach (var package in candidates.Where(package => duplicateIds.Contains(package.Manifest.Id)))
                issues.Add(new ExtensionDiscoveryIssue(
                    Path.GetFileName(package.PackageDirectory),
                    $"插件 ID“{package.Manifest.Id}”重复，所有同名插件均已拒绝加载。"));
            candidates.RemoveAll(package => duplicateIds.Contains(package.Manifest.Id));
        }

        return new ExtensionDiscoveryResult(
            candidates.OrderBy(package => package.Manifest.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            issues.ToArray(),
            trustWarning);
    }

    public ExtensionPackage Enable(string extensionId, string expectedFingerprint)
    {
        var package = FindPackage(extensionId);
        if (string.IsNullOrWhiteSpace(expectedFingerprint)
            || !package.Fingerprint.Equals(expectedFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("插件在确认期间发生变化，已拒绝启用；请刷新后重新检查。 ");
        var trust = ReadTrustStore(out _);
        trust[package.Manifest.Id] = package.Fingerprint;
        WriteTrustStore(trust);
        return package with { Enabled = true, TrustInvalidated = false };
    }

    public void Disable(string extensionId)
    {
        if (IsRunning(extensionId))
            throw new InvalidOperationException("插件仍在运行，请先停止后再禁用。");
        var trust = ReadTrustStore(out _);
        if (trust.Remove(extensionId)) WriteTrustStore(trust);
    }

    public bool IsRunning(string extensionId)
    {
        lock (_gate)
            return _running.TryGetValue(extensionId, out var process) && !process.HasExited;
    }

    public async Task<ExtensionExecutionResult> RunAsync(
        string extensionId,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var package = FindPackage(extensionId);
        if (!package.Enabled)
            throw new InvalidOperationException(package.TrustInvalidated
                ? "插件文件已经变化，旧授权已失效；请检查后重新启用。"
                : "插件默认禁用，请先确认风险并启用。 ");

        // Re-read and re-hash immediately before starting. A changed manifest or executable
        // no longer matches the trusted fingerprint and therefore fails closed.
        var verified = FindPackage(extensionId);
        if (!verified.Enabled || !string.Equals(verified.Fingerprint, package.Fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("插件在启动前发生变化，已拒绝运行；请刷新并重新确认。 ");

        var extensionDataDirectory = Path.Combine(_dataRoot, verified.Manifest.Id);
        Directory.CreateDirectory(extensionDataDirectory);
        RejectReparsePoint(extensionDataDirectory, "插件独立数据目录");

        var executionPackage = CreateExecutionSnapshot(verified, extensionDataDirectory);
        var startInfo = CreateStartInfo(executionPackage, extensionDataDirectory);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        lock (_gate)
        {
            if (_running.TryGetValue(extensionId, out var existing) && !existing.HasExited)
                throw new InvalidOperationException("插件已经在运行。 ");
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Windows 没有启动插件进程。 ");
                _running[extensionId] = process;
            }
            catch
            {
                process.Dispose();
                SafeDeleteExecutionSnapshot(executionPackage.PackageDirectory, extensionDataDirectory);
                throw;
            }
        }

        try
        {
            var standardOutput = PumpAsync(process.StandardOutput, "输出", onOutput);
            var standardError = PumpAsync(process.StandardError, "错误", onOutput);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                throw;
            }
            await Task.WhenAll(standardOutput, standardError);
            var success = process.ExitCode == 0;
            return new ExtensionExecutionResult(
                extensionId,
                success,
                process.ExitCode,
                success ? "插件已正常退出。" : $"插件异常退出，退出码 {process.ExitCode}。 ");
        }
        finally
        {
            lock (_gate)
            {
                if (_running.TryGetValue(extensionId, out var current) && ReferenceEquals(current, process))
                    _running.Remove(extensionId);
            }
            process.Dispose();
            SafeDeleteExecutionSnapshot(executionPackage.PackageDirectory, extensionDataDirectory);
        }
    }

    public async Task<bool> StopAsync(string extensionId, CancellationToken cancellationToken = default)
    {
        Process? process;
        lock (_gate)
            _running.TryGetValue(extensionId, out process);
        if (process is null || process.HasExited) return false;

        TryKillProcessTree(process);
        await process.WaitForExitAsync(cancellationToken);
        return true;
    }

    public async Task StopAllAsync(TimeSpan timeout)
    {
        string[] ids;
        lock (_gate) ids = _running.Keys.ToArray();
        using var cancellation = new CancellationTokenSource(timeout);
        foreach (var id in ids)
        {
            try { await StopAsync(id, cancellation.Token); }
            catch { }
        }
    }

    private ExtensionPackage FindPackage(string extensionId)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
            throw new ArgumentException("插件 ID 不能为空。", nameof(extensionId));
        var result = Discover();
        return result.Packages.SingleOrDefault(package =>
                   package.Manifest.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException("插件不存在或未通过安全检查。 ");
    }

    private ExtensionPackage ReadPackage(string directory, IReadOnlyDictionary<string, string> trust)
    {
        var packageDirectory = Path.GetFullPath(directory);
        EnsureStrictChild(PackagesDirectory, packageDirectory, "插件目录");
        RejectReparsePoint(packageDirectory, "插件目录");

        var manifestPath = Path.Combine(packageDirectory, "plugin.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("缺少 plugin.json。 ");
        RejectReparsePoint(manifestPath, "plugin.json");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        if (manifestBytes.Length is 0 or > MaximumManifestBytes)
            throw new InvalidDataException($"plugin.json 必须在 1 到 {MaximumManifestBytes} 字节之间。 ");
        var manifest = JsonSerializer.Deserialize<ExtensionManifest>(manifestBytes, ManifestJsonOptions)
                       ?? throw new InvalidDataException("plugin.json 内容为空。 ");
        ValidateManifest(manifest);

        var entryPath = ResolveEntryPath(packageDirectory, manifest.Entry);
        if (!File.Exists(entryPath))
            throw new InvalidDataException("入口程序不存在。 ");
        RejectPathReparsePoints(packageDirectory, entryPath);
        if (!Path.GetExtension(entryPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("入口只允许独立 .exe；脚本和 DLL 不能直接载入总管家。 ");

        string entrySha256;
        using (var entryStream = new FileStream(
                   entryPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 64 * 1024,
                   FileOptions.SequentialScan))
            entrySha256 = Convert.ToHexString(SHA256.HashData(entryStream));
        if (!string.IsNullOrWhiteSpace(manifest.EntrySha256)
            && !entrySha256.Equals(manifest.EntrySha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("入口程序 SHA-256 与 plugin.json 声明不一致。 ");

        var fingerprint = ComputePackageFingerprint(packageDirectory);
        var hasTrust = trust.TryGetValue(manifest.Id, out var trustedFingerprint);
        var enabled = hasTrust && fingerprint.Equals(trustedFingerprint, StringComparison.Ordinal);
        return new ExtensionPackage(
            manifest,
            packageDirectory,
            entryPath,
            entrySha256,
            fingerprint,
            enabled,
            hasTrust && !enabled);
    }

    private static void ValidateManifest(ExtensionManifest manifest)
    {
        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException("只支持 schemaVersion=1。 ");
        if (!IdPattern.IsMatch(manifest.Id))
            throw new InvalidDataException("插件 ID 只能使用 2-64 位小写字母、数字、点、横线或下划线。 ");
        ValidateText(manifest.Name, 1, 80, "插件名称");
        ValidateText(manifest.Version, 1, 64, "版本");
        if (!VersionPattern.IsMatch(manifest.Version))
            throw new InvalidDataException("版本必须类似 1.0.0 或 1.0.0-beta.1。 ");
        ValidateText(manifest.Publisher, 1, 80, "发布者");
        ValidateText(manifest.Description, 1, 300, "说明");
        ValidateText(manifest.Entry, 1, 240, "入口路径");
        if (manifest.Arguments is null || manifest.Arguments.Count > 32
            || manifest.Arguments.Any(argument => argument is null || argument.Length > 1024))
            throw new InvalidDataException("参数最多 32 个，每个最多 1024 个字符。 ");
        if (manifest.Capabilities is null || manifest.Capabilities.Count > AllowedCapabilities.Count)
            throw new InvalidDataException("能力声明数量不合法。 ");
        var normalizedCapabilities = manifest.Capabilities.ToArray();
        if (normalizedCapabilities.Distinct(StringComparer.Ordinal).Count() != normalizedCapabilities.Length
            || normalizedCapabilities.Any(capability => !AllowedCapabilities.Contains(capability)))
            throw new InvalidDataException("存在重复或未知的能力声明。 ");
        if (!string.IsNullOrWhiteSpace(manifest.EntrySha256)
            && (manifest.EntrySha256.Length != 64 || manifest.EntrySha256.Any(character => !Uri.IsHexDigit(character))))
            throw new InvalidDataException("entrySha256 必须是 64 位十六进制 SHA-256。 ");
    }

    private static void ValidateText(string value, int minimum, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < minimum || value.Length > maximum)
            throw new InvalidDataException($"{label}长度必须在 {minimum}-{maximum} 个字符之间。 ");
        if (value.Any(char.IsControl))
            throw new InvalidDataException($"{label}不能包含控制字符。 ");
    }

    private static string ResolveEntryPath(string packageDirectory, string entry)
    {
        if (Path.IsPathFullyQualified(entry)
            || entry.StartsWith('\\')
            || entry.StartsWith('/')
            || entry.Contains(':'))
            throw new InvalidDataException("入口必须是插件目录内的相对路径。 ");
        var fullPath = Path.GetFullPath(Path.Combine(
            packageDirectory,
            entry.Replace('/', Path.DirectorySeparatorChar)));
        EnsureStrictChild(packageDirectory, fullPath, "入口程序");
        return fullPath;
    }

    private static void EnsureStrictChild(string root, string candidate, string label)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidateFull = Path.GetFullPath(candidate);
        if (!candidateFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label}越过允许目录。 ");
    }

    private static void RejectPathReparsePoints(string root, string target)
    {
        RejectReparsePoint(root, "插件目录");
        var relative = Path.GetRelativePath(root, target);
        if (Path.IsPathFullyQualified(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidDataException("入口程序越过插件目录。 ");
        var current = root;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current, "入口路径");
        }
    }

    private static string ComputePackageFingerprint(string packageDirectory)
    {
        var files = EnumeratePackageFilesSafe(packageDirectory)
            .OrderBy(path => Path.GetRelativePath(packageDirectory, path), StringComparer.Ordinal)
            .ToArray();
        if (files.Length is 0 or > MaximumPackageFiles)
            throw new InvalidDataException($"插件文件数量必须在 1-{MaximumPackageFiles} 之间。 ");

        long totalBytes = 0;
        using var packageHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            RejectPathReparsePoints(packageDirectory, file);
            var info = new FileInfo(file);
            totalBytes = checked(totalBytes + info.Length);
            if (totalBytes > MaximumPackageBytes)
                throw new InvalidDataException($"插件包不能超过 {MaximumPackageBytes / 1024 / 1024} MB。 ");

            var relative = Path.GetRelativePath(packageDirectory, file).Replace('\\', '/');
            var nameBytes = Encoding.UTF8.GetBytes(relative);
            packageHash.AppendData(BitConverter.GetBytes(nameBytes.Length));
            packageHash.AppendData(nameBytes);
            packageHash.AppendData(BitConverter.GetBytes(info.Length));
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                packageHash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(packageHash.GetHashAndReset());
    }

    private static IReadOnlyList<string> EnumeratePackageFilesSafe(string packageDirectory)
    {
        var packageRoot = Path.GetFullPath(packageDirectory);
        RejectReparsePoint(packageRoot, "插件目录");
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(packageRoot);
        var directoryCount = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            EnsureStrictChildOrSame(packageRoot, current, "插件子目录");
            RejectReparsePoint(current, "插件子目录");
            if (++directoryCount > MaximumPackageFiles)
                throw new InvalidDataException($"插件目录数量不能超过 {MaximumPackageFiles}。 ");

            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                var fullDirectory = Path.GetFullPath(directory);
                EnsureStrictChild(packageRoot, fullDirectory, "插件子目录");
                RejectReparsePoint(fullDirectory, "插件子目录");
                pending.Push(fullDirectory);
            }
            foreach (var file in Directory.EnumerateFiles(current))
            {
                var fullFile = Path.GetFullPath(file);
                EnsureStrictChild(packageRoot, fullFile, "插件文件");
                RejectReparsePoint(fullFile, "插件文件");
                files.Add(fullFile);
                if (files.Count > MaximumPackageFiles)
                    throw new InvalidDataException($"插件文件数量不能超过 {MaximumPackageFiles}。 ");
            }
        }
        return files;
    }

    private static void EnsureStrictChildOrSame(string root, string candidate, string label)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidateFull = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!candidateFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
            && !candidateFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label}越过允许目录。 ");
    }

    private static ExtensionPackage CreateExecutionSnapshot(
        ExtensionPackage package,
        string extensionDataDirectory)
    {
        var runsRoot = Path.Combine(extensionDataDirectory, "runs");
        Directory.CreateDirectory(runsRoot);
        RejectReparsePoint(runsRoot, "插件运行快照目录");
        var executionDirectory = Path.Combine(runsRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(executionDirectory);
        try
        {
            foreach (var source in EnumeratePackageFilesSafe(package.PackageDirectory))
            {
                var relative = Path.GetRelativePath(package.PackageDirectory, source);
                var destination = Path.GetFullPath(Path.Combine(executionDirectory, relative));
                EnsureStrictChild(executionDirectory, destination, "插件运行快照文件");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
            }
            var snapshotFingerprint = ComputePackageFingerprint(executionDirectory);
            if (!snapshotFingerprint.Equals(package.Fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("插件复制期间发生变化，已拒绝运行；请刷新并重新确认。 ");
            var snapshotEntry = ResolveEntryPath(executionDirectory, package.Manifest.Entry);
            RejectPathReparsePoints(executionDirectory, snapshotEntry);
            return package with
            {
                PackageDirectory = executionDirectory,
                EntryPath = snapshotEntry,
                Fingerprint = snapshotFingerprint
            };
        }
        catch
        {
            SafeDeleteExecutionSnapshot(executionDirectory, extensionDataDirectory);
            throw;
        }
    }

    private static void SafeDeleteExecutionSnapshot(string executionDirectory, string extensionDataDirectory)
    {
        try
        {
            var runsRoot = Path.GetFullPath(Path.Combine(extensionDataDirectory, "runs"));
            var executionFull = Path.GetFullPath(executionDirectory);
            EnsureStrictChild(runsRoot, executionFull, "插件运行快照");
            DeleteTreeWithoutFollowingReparsePoints(executionFull);
        }
        catch
        {
            // A hostile or still-running child can keep a snapshot locked. Leaving a bounded
            // runtime artifact is safer than following a link or deleting outside the run root.
        }
    }

    private static void DeleteTreeWithoutFollowingReparsePoints(string path)
    {
        if (!Directory.Exists(path)) return;
        var directory = new DirectoryInfo(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            directory.Delete();
            return;
        }
        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (item is DirectoryInfo linkedDirectory) linkedDirectory.Delete();
                else item.Delete();
            }
            else if (item is DirectoryInfo childDirectory)
                DeleteTreeWithoutFollowingReparsePoints(childDirectory.FullName);
            else
                item.Delete();
        }
        directory.Delete();
    }

    private static void RejectReparsePoint(string path, string label)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{label}不能是符号链接、目录联接或其他重解析点。 ");
    }

    private ProcessStartInfo CreateStartInfo(ExtensionPackage package, string extensionDataDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = package.EntryPath,
            WorkingDirectory = package.PackageDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = false
        };
        foreach (var argument in package.Manifest.Arguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment.Clear();
        foreach (var name in new[]
                 {
                     "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
                     "PROGRAMDATA", "PATH", "DOTNET_ROOT", "DOTNET_ROOT(x86)"
                 })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) startInfo.Environment[name] = value;
        }
        startInfo.Environment["CMM_EXTENSION_ID"] = package.Manifest.Id;
        startInfo.Environment["CMM_EXTENSION_NAME"] = package.Manifest.Name;
        startInfo.Environment["CMM_EXTENSION_ROOT"] = package.PackageDirectory;
        startInfo.Environment["CMM_EXTENSION_DATA_DIR"] = extensionDataDirectory;
        startInfo.Environment["CMM_EXTENSION_CAPABILITIES"] = string.Join(',', package.Manifest.Capabilities);
        return startInfo;
    }

    private static async Task PumpAsync(TextReader reader, string channel, Action<string>? onOutput)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            try { onOutput?.Invoke($"[{channel}] {line}"); }
            catch { }
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private Dictionary<string, string> ReadTrustStore(out string? warning)
    {
        warning = null;
        if (!File.Exists(_statePath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            RejectReparsePoint(_statePath, "插件信任记录");
            var document = JsonSerializer.Deserialize<ExtensionTrustDocument>(
                               File.ReadAllBytes(_statePath),
                               ManifestJsonOptions)
                           ?? throw new InvalidDataException("信任记录为空。 ");
            if (document.SchemaVersion != 1 || document.Trusted is null)
                throw new InvalidDataException("信任记录版本不受支持。 ");
            return new Dictionary<string, string>(document.Trusted, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            warning = $"插件信任记录无法读取，全部插件已按禁用处理：{ex.Message}";
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WriteTrustStore(IReadOnlyDictionary<string, string> trust)
    {
        var document = new ExtensionTrustDocument
        {
            SchemaVersion = 1,
            Trusted = trust.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, StateJsonOptions);
        var temporary = Path.Combine(RootDirectory, $".trusted-extensions-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class ExtensionTrustDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("trusted")]
        public Dictionary<string, string> Trusted { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
