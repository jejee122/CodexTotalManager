using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CodexModelManager.Services;

public sealed class ProductMaintenanceService : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CodexTotalManager";
    private const long MaximumReleaseResponseBytes = 1024 * 1024;
    private static readonly Uri ReleasesEndpoint = new(
        "https://api.github.com/repos/jejee122/CodexTotalManager/releases?per_page=20");
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public ProductMaintenanceService(string dataDirectory, HttpClient? httpClient = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        DiagnosticDirectory = Path.Combine(DataDirectory, "diagnostics");
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaximumReleaseResponseBytes
        };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("CodexTotalManager", SafeHeaderVersion(CurrentVersion)));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public string DataDirectory { get; }

    public string DiagnosticDirectory { get; }

    public string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? "未知";

    public bool StartWithWindowsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }
    }

    public void SetStartWithWindows(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("开机启动只支持 Windows。 ");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("无法打开 Windows 当前用户的开机启动设置。 ");
        if (!enabled)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(RunValueName, BuildStartupCommand(), RegistryValueKind.String);
    }

    public string BuildDiagnosticSummary(AppSettingsService settings)
    {
        var diagnostics = ReadDirectorySummary(DiagnosticDirectory, "crash-*.*");
        var mode = RuntimeMode.IsDetachedUi
            ? "独立测试模式（不连接真实 Codex）"
            : "普通桌面模式";
        var settingsState = string.IsNullOrWhiteSpace(settings.LoadWarning)
            ? "正常"
            : "已进入只读保护，请查看 diagnostics";
        return string.Join(Environment.NewLine, new[]
        {
            "Codex 总管家诊断摘要",
            $"软件版本：{CurrentVersion}",
            $"运行模式：{mode}",
            $"系统：{RuntimeInformation.OSDescription.Trim()}",
            $"程序架构：{RuntimeInformation.ProcessArchitecture}",
            $"设置状态：{settingsState}",
            $"Native Engine 端口：{settings.NativeEnginePort}（仅本机）",
            $"统一网关端口：{settings.UnifiedGatewayPort}（仅本机）",
            $"诊断日志：{diagnostics.FileCount} 个，共 {FormatBytes(diagnostics.TotalBytes)}",
            $"最近诊断：{diagnostics.LastWriteText}",
            "隐私说明：本摘要不包含用户名、本机完整路径、账号、Cookie、Token、API Key、服务器地址或聊天内容。"
        });
    }

    public async Task<ProductUpdateResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesEndpoint);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub 返回 HTTP {(int)response.StatusCode}，暂时无法检查更新。 ");
        if (response.Content.Headers.ContentLength is > MaximumReleaseResponseBytes)
            throw new InvalidOperationException("GitHub 更新信息超过安全大小限制，已停止读取。 ");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(
                           stream,
                           JsonOptions,
                           cancellationToken)
                       ?? new List<GitHubRelease>();
        var valid = releases
            .Where(release => !release.Draft
                              && TryParseSemanticVersion(release.TagName, out _)
                              && TryValidateReleaseUri(release.HtmlUrl, out _))
            .OrderByDescending(release => ParseSemanticVersion(release.TagName), SemanticVersionComparer.Instance)
            .FirstOrDefault();
        if (valid is null)
            return new ProductUpdateResult(
                CurrentVersion,
                null,
                false,
                null,
                "GitHub 仓库目前没有可验证的正式 Release。源码更新不等于可安装更新。 ");

        var latest = valid.TagName.Trim().TrimStart('v', 'V');
        var available = TryParseSemanticVersion(CurrentVersion, out var current)
                        && CompareSemanticVersions(ParseSemanticVersion(latest), current) > 0;
        var releaseUri = TryValidateReleaseUri(valid.HtmlUrl, out var verifiedUri) ? verifiedUri : null;
        var status = available
            ? $"发现新版本 {latest}。只会打开 GitHub Release，不会静默下载或覆盖当前软件。"
            : $"当前已经是最新或更高的候选版本（GitHub：{latest}）。";
        return new ProductUpdateResult(CurrentVersion, latest, available, releaseUri, status);
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    internal static int CompareVersionText(string left, string right) =>
        CompareSemanticVersions(ParseSemanticVersion(left), ParseSemanticVersion(right));

    private static string BuildStartupCommand()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            throw new InvalidOperationException("找不到总管家程序文件，无法设置开机启动。 ");

        var directory = new DirectoryInfo(Path.GetDirectoryName(processPath)!);
        for (var depth = 0; depth < 4 && directory is not null; depth++, directory = directory.Parent)
        {
            var launcher = Path.Combine(directory.FullName, "Launch-Manager-Hidden.vbs");
            var pointer = Path.Combine(directory.FullName, "runtime-v3", "active-release.json");
            if (File.Exists(launcher) && File.Exists(pointer))
                return $"wscript.exe \"{launcher}\"";
        }

        return $"\"{processPath}\" --startup";
    }

    private static DirectorySummary ReadDirectorySummary(string directory, string pattern)
    {
        try
        {
            if (!Directory.Exists(directory)) return new DirectorySummary(0, 0, "没有记录");
            var files = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .ToArray();
            var latest = files.OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
            return new DirectorySummary(
                files.Length,
                files.Sum(file => file.Length),
                latest is null ? "没有记录" : latest.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch
        {
            return new DirectorySummary(0, 0, "无法读取");
        }
    }

    private static bool TryValidateReleaseUri(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps
            || !candidate.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(candidate.UserInfo))
            return false;
        uri = candidate;
        return true;
    }

    private static SemanticVersion ParseSemanticVersion(string value)
    {
        if (!TryParseSemanticVersion(value, out var parsed))
            throw new FormatException($"无法识别版本号：{value}");
        return parsed;
    }

    private static bool TryParseSemanticVersion(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = Regex.Match(
            value.Trim(),
            @"^[vV]?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$",
            RegexOptions.CultureInvariant);
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, out var major)
            || !int.TryParse(match.Groups["minor"].Value, out var minor)
            || !int.TryParse(match.Groups["patch"].Value, out var patch))
            return false;
        var prerelease = match.Groups["pre"].Success
            ? match.Groups["pre"].Value.Split('.', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    private static int CompareSemanticVersions(SemanticVersion left, SemanticVersion right)
    {
        var result = left.Major.CompareTo(right.Major);
        if (result != 0) return result;
        result = left.Minor.CompareTo(right.Minor);
        if (result != 0) return result;
        result = left.Patch.CompareTo(right.Patch);
        if (result != 0) return result;
        if (left.Prerelease.Count == 0) return right.Prerelease.Count == 0 ? 0 : 1;
        if (right.Prerelease.Count == 0) return -1;
        for (var index = 0; index < Math.Max(left.Prerelease.Count, right.Prerelease.Count); index++)
        {
            if (index >= left.Prerelease.Count) return -1;
            if (index >= right.Prerelease.Count) return 1;
            var leftPart = left.Prerelease[index];
            var rightPart = right.Prerelease[index];
            var leftNumeric = int.TryParse(leftPart, out var leftNumber);
            var rightNumeric = int.TryParse(rightPart, out var rightNumber);
            if (leftNumeric && rightNumeric) result = leftNumber.CompareTo(rightNumber);
            else if (leftNumeric != rightNumeric) result = leftNumeric ? -1 : 1;
            else result = string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
            if (result != 0) return result;
        }
        return 0;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes / 1024d / 1024d:0.0} MB"
    };

    private static string SafeHeaderVersion(string version)
    {
        var safe = Regex.Replace(version, "[^0-9A-Za-z.-]", "-");
        return string.IsNullOrWhiteSpace(safe) ? "0.0.0" : safe;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly record struct DirectorySummary(int FileCount, long TotalBytes, string LastWriteText);

    private readonly record struct SemanticVersion(
        int Major,
        int Minor,
        int Patch,
        IReadOnlyList<string> Prerelease);

    private sealed class SemanticVersionComparer : IComparer<SemanticVersion>
    {
        public static SemanticVersionComparer Instance { get; } = new();

        public int Compare(SemanticVersion x, SemanticVersion y) => CompareSemanticVersions(x, y);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }
    }
}

public sealed record ProductUpdateResult(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    Uri? ReleaseUri,
    string Message);
