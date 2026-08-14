using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public sealed class DreamSkinService
{
    private const int DreamSkinPort = 9335;
    private const string ManagerScriptHash = "04DC2EA5069DF71619E2CD1D782C850C9B4DB92E0B40F514CB768693CA071F32";

    private static readonly Dictionary<string, string> EngineScriptHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["common-windows.ps1"] = "0443214475F5B99773C931FD4613517FCED54FF94CF6437609139D186F022046",
        ["theme-windows.ps1"] = "F271F4DFA9E1155C165758570A7116091E1964AF8D9E4E164A2CC0F76221B158",
        ["start-dream-skin.ps1"] = "703EFF94D4C91B122F106FCF5E3AF5954C913B2C17EE460F41A0CD3AD730DB0A",
        ["restore-dream-skin.ps1"] = "455BBEC8FB5FBC42FDD299382AC4C0D612F8651BFA19BC59AF28F793CA305E5E",
        ["apply-community-theme.ps1"] = "85F43F3C3338228158500F95207D9345B83440CE282BD0E6DD50E077D95BA194"
    };
    private static readonly Regex SafeThemeId = new(
        @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,79}\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex CommunityUri = new(
        @"\Adreamskin://apply\?version=ver_[a-z0-9]{8,64}\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex SafeColor = new(
        @"\A#[0-9A-Fa-f]{3}(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{5})?\z",
        RegexOptions.CultureInvariant);

    private readonly string _resourceRoot;

    public string StateRoot { get; }
    public string BundledEngineRoot => Path.Combine(_resourceRoot, "CodexDreamSkin");
    public string EngineRoot => Directory.Exists(BundledEngineRoot)
        ? BundledEngineRoot
        : Path.Combine(StateRoot, "engine");
    public string ThemesRoot => Path.Combine(StateRoot, "themes");
    public string ManagerScript => Path.Combine(_resourceRoot, "Themes", "dream-skin-manager.ps1");
    public string CommunityApplyScript => Path.Combine(EngineRoot, "scripts", "apply-community-theme.ps1");

    public DreamSkinService(string? stateRoot = null, string? resourceRoot = null)
    {
        StateRoot = Path.GetFullPath(stateRoot ?? ResolveDefaultStateRoot());
        _resourceRoot = Path.GetFullPath(resourceRoot ?? Path.Combine(AppContext.BaseDirectory, "Resources"));
    }

    private static string ResolveDefaultStateRoot()
    {
        var sandboxRoot = Environment.GetEnvironmentVariable("CMM_SANDBOX_DREAMSKIN");
        if (!string.IsNullOrWhiteSpace(sandboxRoot))
            return sandboxRoot;
        return Path.Combine(AppSettingsService.ResolveDefaultDataDirectory(), "dream-skin");
    }

    public async Task<DreamSkinSnapshot> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (RuntimeMode.IsCodexTestDouble)
            return await DiscoverTestDoubleAsync(cancellationToken);

        var managerTrusted = HashMatches(ManagerScript, ManagerScriptHash);
        var required = new[]
        {
            Path.Combine(EngineRoot, "VERSION"),
            Path.Combine(EngineRoot, "scripts", "common-windows.ps1"),
            Path.Combine(EngineRoot, "scripts", "theme-windows.ps1"),
            Path.Combine(EngineRoot, "scripts", "start-dream-skin.ps1"),
            Path.Combine(EngineRoot, "scripts", "restore-dream-skin.ps1"),
            CommunityApplyScript
        };
        var engineScriptsTrusted = required
            .Where(path => Path.GetFileName(path).EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            .All(path => EngineScriptHashes.TryGetValue(Path.GetFileName(path), out var expected)
                         && HashMatches(path, expected));
        var stateRootSafe = Directory.Exists(StateRoot) && IsManagedPathSafe(StateRoot);
        var engineReady = managerTrusted
                          && engineScriptsTrusted
                          && stateRootSafe
                          && Directory.Exists(EngineRoot)
                          && required.All(File.Exists)
                          && IsManagedPathSafe(EngineRoot)
                          && required.All(IsManagedPathSafe);
        var version = ReadSmallText(required[0], 128)?.Trim() ?? "未安装";
        var paused = engineReady && File.Exists(Path.Combine(StateRoot, "paused"));
        var (activeId, activeName) = engineReady
            ? ReadActiveTheme()
            : (string.Empty, string.Empty);
        var themes = engineReady ? ReadThemes(activeId, paused) : Array.Empty<DreamSkinThemeView>();
        var live = engineReady && await HasVerifiedLiveSessionAsync(cancellationToken);

        string title;
        string detail;
        if (!managerTrusted)
        {
            title = File.Exists(ManagerScript) ? "皮肤安全组件已被锁定" : "总管家皮肤组件缺失";
            detail = "不会运行来源不明或被修改过的换肤脚本；模型、账号和记忆配置没有改动。";
        }
        else if (!engineReady)
        {
            title = "没有找到完整的 Dream Skin 引擎";
            detail = "已保存的模型、账号和聊天记忆不受影响；重新安装 Dream Skin 后刷新即可。";
        }
        else if (paused)
        {
            title = "当前使用官方外观";
            detail = live
                ? $"Dream Skin {version} 已连接但保持暂停；选择任意主题即可实时恢复。"
                : $"Dream Skin {version} 已暂停；下次应用主题时会先尝试无重启切换。";
        }
        else if (live)
        {
            title = string.IsNullOrWhiteSpace(activeName) ? "皮肤实时通道已连接" : $"正在使用：{activeName}";
            detail = $"Dream Skin {version} 正常，可以直接在线切换；失败才会询问是否重启 Codex。";
        }
        else
        {
            title = string.IsNullOrWhiteSpace(activeName) ? "Dream Skin 已安装" : $"已选主题：{activeName}";
            detail = $"Dream Skin {version} 已找到，但当前 Codex 没有连接换肤通道；应用时可能需要重启一次。";
        }

        return new DreamSkinSnapshot(
            engineReady,
            managerTrusted,
            live,
            paused,
            version,
            activeId,
            activeName,
            title,
            detail,
            StateRoot,
            ThemesRoot,
            themes);
    }

    public Task<DreamSkinOperationResult> ApplyInstalledThemeAsync(
        string themeId,
        bool allowRestart,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeMode.IsCodexTestDouble)
            return ApplyTestDoubleThemeAsync(themeId, cancellationToken);
        if (RuntimeMode.IsDetachedUi) return Task.FromResult(DetachedBlocked());
        if (!SafeThemeId.IsMatch(themeId) || themeId.EndsWith(".", StringComparison.Ordinal))
            return Task.FromResult(Failed("主题标识不安全，已经拒绝切换。"));
        return RunManagerAsync(
            new[] { "-Action", "ApplyInstalled", "-ThemeId", themeId }
                .Concat(allowRestart ? new[] { "-AllowRestart" } : Array.Empty<string>()),
            allowRestart ? TimeSpan.FromSeconds(150) : TimeSpan.FromSeconds(45),
            cancellationToken);
    }

    public Task<DreamSkinOperationResult> UseOfficialAppearanceAsync(
        bool allowRestart,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeMode.IsCodexTestDouble)
            return ApplyTestDoubleThemeAsync("official", cancellationToken);
        return RuntimeMode.IsDetachedUi
            ? Task.FromResult(DetachedBlocked())
            : RunManagerAsync(
                new[] { "-Action", "Pause" }
                    .Concat(allowRestart ? new[] { "-AllowRestart" } : Array.Empty<string>()),
                allowRestart ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(30),
                cancellationToken);
    }

    public Task<DreamSkinOperationResult> PrepareLiveSessionAsync(
        bool allowRestart,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeMode.IsCodexTestDouble)
            return PrepareTestDoubleSkinSessionAsync(cancellationToken);
        return RuntimeMode.IsDetachedUi
            ? Task.FromResult(DetachedBlocked())
            : RunManagerAsync(
                new[] { "-Action", "PrepareSession" }
                    .Concat(allowRestart ? new[] { "-AllowRestart" } : Array.Empty<string>()),
                allowRestart ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(45),
                cancellationToken);
    }

    public Task<DreamSkinOperationResult> ImportThemeZipAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeMode.IsDetachedUi) return Task.FromResult(DetachedBlocked());
        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath) || !Path.GetExtension(fullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Failed("请选择一个真实的 .zip 主题包。"));
        return RunManagerAsync(
            new[] { "-Action", "ImportZip", "-ArchivePath", fullPath },
            TimeSpan.FromSeconds(60),
            cancellationToken);
    }

    public async Task<DreamSkinOperationResult> ApplyCommunityThemeAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeMode.IsCodexTestDouble)
            return await ApplyTestDoubleCommunityThemeAsync(uri, cancellationToken);
        if (RuntimeMode.IsDetachedUi) return DetachedBlocked();
        var canonical = uri.Trim();
        if (!CommunityUri.IsMatch(canonical))
            return Failed("请输入 Gallery 提供的 dreamskin://apply?version=ver_... 完整链接。");
        if (!IsManagedPathSafe(CommunityApplyScript) || !File.Exists(CommunityApplyScript))
            return Failed("Dream Skin 的在线主题组件缺失或路径不安全。没有下载任何内容。");

        var result = await RunPowerShellAsync(
            CommunityApplyScript,
            new[] { "-Uri", canonical },
            TimeSpan.FromMinutes(4),
            cancellationToken,
            nonInteractive: false);
        if (result.TimedOut) return Failed("在线主题操作超过 4 分钟，已经停止。旧主题快照仍保留。");
        if (result.ExitCode == 0)
            return new DreamSkinOperationResult(DreamSkinOperationStatus.Success, "在线主题流程已完成，正在刷新主题库。");
        var message = LastUsefulLine(result.Error) ?? LastUsefulLine(result.Output) ?? "在线主题没有应用成功。";
        return new DreamSkinOperationResult(
            DreamSkinOperationStatus.Failed,
            message,
            PreviousThemeRecovered: result.ExitCode == 20);
    }

    private async Task<DreamSkinSnapshot> DiscoverTestDoubleAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = NewTestDoubleClient();
            using var response = await client.GetAsync("api/skins", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var activeId = ReadString(root, "activeThemeId") ?? string.Empty;
            var activeName = ReadString(root, "activeThemeName") ?? string.Empty;
            var live = ReadBoolean(root, "liveSessionConnected");
            var paused = ReadBoolean(root, "isPaused");
            var version = ReadString(root, "engineVersion") ?? "1.0.0-test-only";
            var themes = new List<DreamSkinThemeView>();
            if (root.TryGetProperty("themes", out var themeItems)
                && themeItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var theme in themeItems.EnumerateArray())
                {
                    var id = ReadString(theme, "id") ?? string.Empty;
                    if (!SafeThemeId.IsMatch(id)) continue;
                    themes.Add(new DreamSkinThemeView
                    {
                        Id = id,
                        ManifestId = ReadString(theme, "manifestId") ?? id,
                        Name = ReadString(theme, "name") ?? id,
                        Description = ReadString(theme, "description") ?? "本机 Codex 测试替身皮肤。",
                        AppearanceText = ReadString(theme, "appearanceText") ?? "跟随系统",
                        MotionText = ReadString(theme, "motionText") ?? "静态主题",
                        PreviewBackground = ReadTestDoubleColor(theme, "previewBackground", "#101A27"),
                        PreviewAccent = ReadTestDoubleColor(theme, "previewAccent", "#D69A4B"),
                        IsDynamic = ReadBoolean(theme, "isDynamic"),
                        IsActive = id.Equals(activeId, StringComparison.Ordinal),
                        WasLastSelected = ReadBoolean(theme, "wasLastSelected")
                    });
                }
            }
            return new DreamSkinSnapshot(
                true,
                true,
                live,
                paused,
                version,
                activeId,
                activeName,
                live ? $"假 Codex 正在使用：{activeName}" : "假 Codex 换肤通道待连接",
                "这是本机测试替身皮肤状态，没有读取或修改真实 Codex。",
                StateRoot,
                Path.Combine(StateRoot, "test-double-themes"),
                themes);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return new DreamSkinSnapshot(
                false,
                true,
                false,
                false,
                "1.0.0-test-only",
                string.Empty,
                string.Empty,
                "假 Codex 皮肤接口不可用",
                ex.Message,
                StateRoot,
                Path.Combine(StateRoot, "test-double-themes"),
                Array.Empty<DreamSkinThemeView>());
        }
    }

    private Task<DreamSkinOperationResult> ApplyTestDoubleThemeAsync(
        string themeId,
        CancellationToken cancellationToken)
    {
        if (!SafeThemeId.IsMatch(themeId) || themeId.EndsWith(".", StringComparison.Ordinal))
            return Task.FromResult(Failed("测试替身主题标识不安全，已经拒绝切换。"));
        return SendTestDoubleSkinOperationAsync(
            HttpMethod.Put,
            "api/skins/active",
            new { themeId },
            cancellationToken);
    }

    private Task<DreamSkinOperationResult> PrepareTestDoubleSkinSessionAsync(
        CancellationToken cancellationToken) =>
        SendTestDoubleSkinOperationAsync(
            HttpMethod.Post,
            "api/skins/session",
            new { connect = true },
            cancellationToken);

    private Task<DreamSkinOperationResult> ApplyTestDoubleCommunityThemeAsync(
        string uri,
        CancellationToken cancellationToken)
    {
        var canonical = uri.Trim();
        if (!CommunityUri.IsMatch(canonical))
            return Task.FromResult(Failed("请输入测试 Gallery 提供的 dreamskin://apply?version=ver_... 完整链接。"));
        return SendTestDoubleSkinOperationAsync(
            HttpMethod.Post,
            "api/skins/community",
            new { uri = canonical },
            cancellationToken);
    }

    private static async Task<DreamSkinOperationResult> SendTestDoubleSkinOperationAsync(
        HttpMethod method,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = NewTestDoubleClient();
            using var request = new HttpRequestMessage(method, path)
            {
                Content = JsonContent.Create(body)
            };
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Failed($"假 Codex 换肤接口拒绝了操作：HTTP {(int)response.StatusCode}。");
            using var document = JsonDocument.Parse(content);
            var message = ReadString(document.RootElement, "message") ?? "假 Codex 换肤操作已完成。";
            return new DreamSkinOperationResult(DreamSkinOperationStatus.Success, message);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return Failed($"假 Codex 换肤操作失败：{ex.Message}");
        }
    }

    private static HttpClient NewTestDoubleClient()
    {
        var baseAddress = RuntimeMode.CodexTestDoubleEngineUri
                          ?? throw new InvalidOperationException("假 Codex 引擎地址缺失。");
        var token = RuntimeMode.CodexTestDoubleToken
                    ?? throw new InvalidOperationException("假 Codex 一次性令牌缺失。");
        var client = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(5)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-CMM-Test-Token", token);
        return client;
    }

    private static bool ReadBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    private static string ReadTestDoubleColor(JsonElement root, string name, string fallback)
    {
        var value = ReadString(root, name);
        return value is not null && SafeColor.IsMatch(value) ? value : fallback;
    }

    private DreamSkinThemeView[] ReadThemes(string activeId, bool paused)
    {
        if (!Directory.Exists(ThemesRoot) || !IsManagedPathSafe(ThemesRoot)) return Array.Empty<DreamSkinThemeView>();
        var hidden = ReadHiddenThemeIds();
        var themes = new List<DreamSkinThemeView>();
        foreach (var directory in Directory.EnumerateDirectories(ThemesRoot, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (!IsDirectChild(directory, ThemesRoot) || !IsManagedPathSafe(directory)) continue;
                var folderId = Path.GetFileName(directory);
                if (!SafeThemeId.IsMatch(folderId) || hidden.Contains(folderId)) continue;
                var manifestPath = Path.Combine(directory, "theme.json");
                var json = ReadSmallText(manifestPath, 256 * 1024);
                if (json is null) continue;
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var manifestId = ReadString(root, "id") ?? folderId;
                if (!SafeThemeId.IsMatch(manifestId) || hidden.Contains(manifestId)) continue;
                var name = ReadString(root, "name") ?? folderId;
                var appearance = (ReadString(root, "appearance") ?? "auto").ToLowerInvariant();
                var isDynamic = ReadAmbientIncense(root) || ReadString(root, "art", "taskMode") == "ambient";
                var background = ReadSafeColor(root, "colors", "background") ??
                                 (appearance == "light" ? "#F3F0EA" : "#101A27");
                var accent = ReadSafeColor(root, "colors", "accent") ??
                             (appearance == "light" ? "#C77992" : "#D69A4B");
                var image = ResolvePreviewImage(directory, ReadString(root, "image"));
                var wasLastSelected = manifestId.Equals(activeId, StringComparison.OrdinalIgnoreCase);
                var active = wasLastSelected && !paused;
                themes.Add(new DreamSkinThemeView
                {
                    Id = folderId,
                    ManifestId = manifestId,
                    Name = name,
                    Description = BuildDescription(root, isDynamic),
                    AppearanceText = appearance switch
                    {
                        "dark" => "深色",
                        "light" => "浅色",
                        _ => "跟随系统"
                    },
                    MotionText = isDynamic ? "动态效果" : "静态主题",
                    PreviewBackground = background,
                    PreviewAccent = accent,
                    PreviewImagePath = image,
                    IsDynamic = isDynamic,
                    IsActive = active,
                    WasLastSelected = wasLastSelected
                });
            }
            catch
            {
                // A broken theme is hidden instead of weakening the rest of the library.
            }
        }
        return themes
            .OrderByDescending(theme => theme.IsActive)
            .ThenBy(theme => theme.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private (string Id, string Name) ReadActiveTheme()
    {
        try
        {
            var directory = Path.Combine(StateRoot, "active-theme");
            var path = Path.Combine(directory, "theme.json");
            if (!IsManagedPathSafe(directory) || !IsManagedPathSafe(path))
                return (string.Empty, string.Empty);
            var json = ReadSmallText(path, 256 * 1024);
            if (json is null) return (string.Empty, string.Empty);
            using var document = JsonDocument.Parse(json);
            return (ReadString(document.RootElement, "id") ?? string.Empty,
                ReadString(document.RootElement, "name") ?? string.Empty);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    private HashSet<string> ReadHiddenThemeIds()
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(StateRoot, "theme-library.json");
            if (!IsManagedPathSafe(path)) return hidden;
            var json = ReadSmallText(path, 64 * 1024);
            if (json is null) return hidden;
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("hiddenThemeIds", out var ids)
                || ids.ValueKind != JsonValueKind.Array) return hidden;
            foreach (var id in ids.EnumerateArray())
                if (id.ValueKind == JsonValueKind.String && SafeThemeId.IsMatch(id.GetString() ?? string.Empty))
                    hidden.Add(id.GetString()!);
        }
        catch { }
        return hidden;
    }

    private async Task<bool> HasVerifiedLiveSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stateText = ReadSmallText(Path.Combine(StateRoot, "state.json"), 128 * 1024);
            if (stateText is null) return false;
            using var stateDocument = JsonDocument.Parse(stateText);
            var state = stateDocument.RootElement;
            if (!state.TryGetProperty("port", out var portValue) || !portValue.TryGetInt32(out var port)
                || port != DreamSkinPort) return false;
            var browserId = ReadString(state, "browserId");
            if (string.IsNullOrWhiteSpace(browserId) || browserId.Length > 160) return false;

            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1200) };
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/json/version", cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            using var version = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var socket = ReadString(version.RootElement, "webSocketDebuggerUrl");
            return Uri.TryCreate(socket, UriKind.Absolute, out var uri)
                   && uri.IsLoopback
                   && uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
                   && uri.AbsolutePath.EndsWith("/" + browserId, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private async Task<DreamSkinOperationResult> RunManagerAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!HashMatches(ManagerScript, ManagerScriptHash))
            return Failed("总管家皮肤安全组件缺失或被修改，已经锁住，不会运行。" );
        var result = await RunPowerShellAsync(
            ManagerScript,
            arguments,
            timeout,
            cancellationToken,
            nonInteractive: true);
        if (result.TimedOut) return Failed("换肤操作超时并已停止；旧主题备份仍保留。" );
        var line = LastJsonLine(result.Output);
        if (line is null)
            return Failed(LastUsefulLine(result.Error) ?? "换肤组件没有返回可识别的结果。" );
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var statusText = ReadString(root, "status") ?? "Failed";
            var message = ReadString(root, "message") ?? "换肤操作没有完成。";
            var recovered = root.TryGetProperty("recovered", out var recovery)
                            && recovery.ValueKind is JsonValueKind.True or JsonValueKind.False
                            && recovery.GetBoolean();
            var backup = ReadString(root, "backupPath");
            var status = Enum.TryParse<DreamSkinOperationStatus>(statusText, true, out var parsed)
                ? parsed
                : DreamSkinOperationStatus.Failed;
            if (result.ExitCode != 0 && status == DreamSkinOperationStatus.Success)
                status = DreamSkinOperationStatus.Failed;
            return new DreamSkinOperationResult(status, message, recovered, backup);
        }
        catch (JsonException)
        {
            return Failed("换肤组件返回了无法识别的安全检查结果。" );
        }
    }

    private static async Task<ProcessResult> RunPowerShellAsync(
        string script,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool nonInteractive)
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-STA");
        if (nonInteractive) start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("RemoteSigned");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start);
        if (process is null) return new ProcessResult(-1, string.Empty, "无法启动 PowerShell。", false);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return new ProcessResult(process.ExitCode, await stdout, await stderr, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            return new ProcessResult(-1, await stdout, await stderr, true);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            try { await stdout; } catch { }
            try { await stderr; } catch { }
            throw;
        }
    }

    private bool IsManagedPathSafe(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var bundled = BundledEngineRoot.TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(bundled, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(bundled + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var bundledCurrent = full;
                while (true)
                {
                    if (File.Exists(bundledCurrent) || Directory.Exists(bundledCurrent))
                    {
                        var attributes = File.GetAttributes(bundledCurrent);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    }
                    var trimmed = bundledCurrent.TrimEnd(Path.DirectorySeparatorChar);
                    if (trimmed.Equals(bundled, StringComparison.OrdinalIgnoreCase)) return true;
                    bundledCurrent = Path.GetDirectoryName(trimmed) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(bundledCurrent)) return false;
                }
            }
            var root = StateRoot.TrimEnd(Path.DirectorySeparatorChar);
            if (!full.Equals(root, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;
            var current = full;
            while (true)
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                }
                var trimmed = current.TrimEnd(Path.DirectorySeparatorChar);
                if (trimmed.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
                current = Path.GetDirectoryName(trimmed) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(current)) return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDirectChild(string path, string parent) =>
        string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)),
            Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string? ResolvePreviewImage(string directory, string? imageName)
    {
        if (string.IsNullOrWhiteSpace(imageName) || Path.IsPathRooted(imageName)) return null;
        var name = Path.GetFileName(imageName);
        if (!name.Equals(imageName, StringComparison.Ordinal)) return null;
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".webp") return null;
        var path = Path.Combine(directory, name);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 10 * 1024 * 1024) return null;
            return (File.GetAttributes(file.FullName) & FileAttributes.ReparsePoint) == 0
                ? file.FullName
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildDescription(JsonElement root, bool isDynamic)
    {
        var tagline = ReadString(root, "tagline");
        if (!string.IsNullOrWhiteSpace(tagline)) return tagline;
        var subtitle = ReadString(root, "brandSubtitle");
        if (!string.IsNullOrWhiteSpace(subtitle)) return subtitle;
        return isDynamic ? "包含动态视觉效果，由 Dream Skin 实时渲染。" : "本地 Dream Skin 主题。";
    }

    private static bool ReadAmbientIncense(JsonElement root)
    {
        if (!root.TryGetProperty("effects", out var effects) || effects.ValueKind != JsonValueKind.Object
            || !effects.TryGetProperty("ambientIncense", out var incense) || incense.ValueKind != JsonValueKind.Object
            || !incense.TryGetProperty("enabled", out var enabled)) return false;
        return enabled.ValueKind == JsonValueKind.True;
    }

    private static string? ReadSafeColor(JsonElement root, string parent, string name)
    {
        var value = ReadString(root, parent, name);
        return value is not null && SafeColor.IsMatch(value) ? value : null;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadString(JsonElement root, string parent, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(parent, out var child)
        && child.ValueKind == JsonValueKind.Object
            ? ReadString(child, name)
            : null;

    private static string? ReadSmallText(string path, int maximumBytes)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length <= maximumBytes ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HashMatches(string path, string expected)
    {
        try
        {
            if (expected.StartsWith("__", StringComparison.Ordinal)) return false;
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? LastJsonLine(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .LastOrDefault(line => line.StartsWith('{') && line.EndsWith('}'));

    private static string? LastUsefulLine(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .LastOrDefault(line => line.Length > 0);

    private static DreamSkinOperationResult Failed(string message) =>
        new(DreamSkinOperationStatus.Failed, message);

    private static DreamSkinOperationResult DetachedBlocked() =>
        Failed("独立模式禁止连接、修改或重启 Codex；换肤操作没有执行。");

    private sealed record ProcessResult(int ExitCode, string Output, string Error, bool TimedOut);
}
