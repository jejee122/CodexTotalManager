using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public enum CodexAliasSwitchStatus
{
    Success,
    NeedsRestart,
    Busy,
    Unavailable,
    Failed
}

public sealed record CodexDesktopState(
    bool Connected,
    bool IsTurnRunning,
    string? CurrentModel,
    string Message,
    int? HostProcessId = null,
    string? TaskIdentity = null,
    int? VisibleMessageCount = null,
    string? ConversationFingerprint = null);

public sealed record CodexAliasSwitchResult(
    CodexAliasSwitchStatus Status,
    string Message,
    string? CurrentModel = null);

public sealed record CodexRestartResult(bool Success, string Message);

public static class CodexConversationContinuity
{
    public static OperationResult VerifySameTask(
        CodexDesktopState before,
        CodexDesktopState after,
        string expectedModel)
    {
        if (!before.Connected || !after.Connected)
            return OperationResult.Fail("切换前后无法持续读取同一个 Codex 当前任务。");
        if (before.HostProcessId is > 0
            && after.HostProcessId is > 0
            && before.HostProcessId != after.HostProcessId)
            return OperationResult.Fail("Codex 进程在切换期间发生变化，已拒绝把它当成无重启切换。");
        if (!string.Equals(after.CurrentModel?.Trim(), expectedModel.Trim(), StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail($"当前任务没有确认模型 {expectedModel}。");
        if (!string.IsNullOrWhiteSpace(before.TaskIdentity)
            && !string.IsNullOrWhiteSpace(after.TaskIdentity)
            && !string.Equals(before.TaskIdentity, after.TaskIdentity, StringComparison.Ordinal))
            return OperationResult.Fail("切换后不再是原来的 Codex 任务。");
        if (before.VisibleMessageCount is not null
            && after.VisibleMessageCount is not null
            && before.VisibleMessageCount != after.VisibleMessageCount)
            return OperationResult.Fail("切换期间可见聊天消息数量发生变化，已拒绝宣称上下文连续。");
        if (!string.IsNullOrWhiteSpace(before.ConversationFingerprint)
            && !string.IsNullOrWhiteSpace(after.ConversationFingerprint)
            && !string.Equals(
                before.ConversationFingerprint,
                after.ConversationFingerprint,
                StringComparison.Ordinal))
            return OperationResult.Fail("切换期间聊天指纹发生变化，已拒绝宣称记忆连续。");
        var hasProcessEvidence = before.HostProcessId is > 0 && after.HostProcessId is > 0;
        var hasTaskEvidence = !string.IsNullOrWhiteSpace(before.TaskIdentity)
                              && !string.IsNullOrWhiteSpace(after.TaskIdentity);
        var hasMessageCountEvidence = before.VisibleMessageCount is not null
                                      && after.VisibleMessageCount is not null;
        var hasFingerprintEvidence = !string.IsNullOrWhiteSpace(before.ConversationFingerprint)
                                     && !string.IsNullOrWhiteSpace(after.ConversationFingerprint);
        if (!hasProcessEvidence
            && !hasTaskEvidence
            && !hasMessageCountEvidence
            && !hasFingerprintEvidence)
            return OperationResult.Fail("切换前后没有一组可对照的任务证据；本次按未验证处理，也不会自动重启 Codex。");
        if (before.HostProcessId is null
            && string.IsNullOrWhiteSpace(before.TaskIdentity)
            && string.IsNullOrWhiteSpace(before.ConversationFingerprint))
            return OperationResult.Fail("当前 Codex 版本无法提供任务连续性证据，已停止切换且不会自动重启。");
        return OperationResult.Ok("当前 Codex 进程、任务和可见聊天保持连续。");
    }
}

/// <summary>
/// Uses the ordinary Windows accessibility tree first and the already-installed
/// Dream Skin CDP endpoint only as a fallback. Neither path patches Codex files
/// or touches the chat database: both use the visible model menu.
/// </summary>
public sealed class CodexDesktopBridgeService
{
    private const int DreamSkinPort = 9335;
    private const string CodexPageUrl = "app://-/index.html";
    private static string DreamSkinStartScript => ResolveDreamSkinStartScript();

    private static string ResolveDreamSkinStartScript()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Resources", "CodexDreamSkin", "scripts", "start-dream-skin.ps1");
        if (File.Exists(bundled)) return bundled;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexDreamSkin", "engine", "scripts", "start-dream-skin.ps1");
    }

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{DreamSkinPort}"),
        Timeout = TimeSpan.FromSeconds(3)
    };

    public async Task<CodexDesktopState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        if (RuntimeMode.IsDetachedUi)
            return new CodexDesktopState(false, false, null, "独立模式未读取 Codex 窗口、任务或调试端口。");
        var windowsState = await CodexWindowsAutomation.ReadStateAsync(cancellationToken);

        try
        {
            await using var session = await ConnectAsync(cancellationToken);
            var cdpState = await ReadStateAsync(session, cancellationToken);
            return cdpState with
            {
                HostProcessId = windowsState.HostProcessId ?? cdpState.HostProcessId,
                TaskIdentity = cdpState.TaskIdentity ?? windowsState.TaskIdentity
            };
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or WebSocketException
                                   or TaskCanceledException
                                   or InvalidOperationException)
        {
            return windowsState;
        }
    }

    public async Task<CodexAliasSwitchResult> EnsureCurrentChatUsesAliasAsync(
        string alias,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeMode.IsDetachedUi)
            return new CodexAliasSwitchResult(
                CodexAliasSwitchStatus.Unavailable,
                "独立模式禁止连接或切换 Codex 当前任务。");
        var windowsResult = await CodexWindowsAutomation.EnsureAliasAsync(alias, cancellationToken);
        if (windowsResult.Status != CodexAliasSwitchStatus.Unavailable)
            return windowsResult;

        CdpSession session;
        try
        {
            session = await ConnectAsync(cancellationToken);
        }
        catch
        {
            return new CodexAliasSwitchResult(
                CodexAliasSwitchStatus.Unavailable,
                "没有找到可操作的 Codex 当前任务。请先打开一个任务。");
        }

        await using (session)
        {
            var before = await ReadStateAsync(session, cancellationToken);
            if (before.IsTurnRunning)
            {
                return new CodexAliasSwitchResult(
                    CodexAliasSwitchStatus.Busy,
                    "Codex 正在回答。等回答结束后再点一次模型。",
                    before.CurrentModel);
            }

            if (SameModel(before.CurrentModel, alias))
            {
                return new CodexAliasSwitchResult(
                    CodexAliasSwitchStatus.Success,
                    "当前聊天已经接到固定入口。",
                    before.CurrentModel);
            }

            await CloseMenusAsync(session, cancellationToken);
            var opened = await session.EvaluateJsonAsync(OpenModelPickerScript, cancellationToken);
            if (!ReadBoolean(opened, "ok"))
            {
                return new CodexAliasSwitchResult(
                    CodexAliasSwitchStatus.Failed,
                    ReadString(opened, "message") ?? "没有找到 Codex 的模型按钮。",
                    before.CurrentModel);
            }

            await Task.Delay(250, cancellationToken);
            var submenu = await session.EvaluateJsonAsync(OpenModelSubmenuScript, cancellationToken);
            if (!ReadBoolean(submenu, "ok"))
            {
                await CloseMenusAsync(session, cancellationToken);
                return new CodexAliasSwitchResult(
                    CodexAliasSwitchStatus.Failed,
                    ReadString(submenu, "message") ?? "没有打开 Codex 的模型列表。",
                    before.CurrentModel);
            }

            await Task.Delay(300, cancellationToken);
            var aliasJson = JsonSerializer.Serialize(alias);
            var selection = await session.EvaluateJsonAsync(
                SelectAliasScript.Replace("__ALIAS__", aliasJson, StringComparison.Ordinal),
                cancellationToken);
            if (!ReadBoolean(selection, "found"))
            {
                await CloseMenusAsync(session, cancellationToken);
                return new CodexAliasSwitchResult(
                    CodexAliasSwitchStatus.NeedsRestart,
                    "Codex 还没有读到固定入口，需要安全重开一次。",
                    before.CurrentModel);
            }

            await Task.Delay(650, cancellationToken);
            var after = await ReadStateAsync(session, cancellationToken);
            if (!SameModel(after.CurrentModel, alias))
            {
                return new CodexAliasSwitchResult(
                    CodexAliasSwitchStatus.Failed,
                    "Codex 没有确认当前聊天的模型切换。",
                    after.CurrentModel);
            }

            return new CodexAliasSwitchResult(
                CodexAliasSwitchStatus.Success,
                "当前聊天已经接到固定入口。",
                after.CurrentModel);
        }
    }

    public Task<CodexRestartResult> RestartCodexWithDreamSkinAsync(
        CancellationToken cancellationToken = default) =>
        RestartCodexWithDreamSkinAsync(null, cancellationToken);

    public async Task<CodexRestartResult> RestartCodexWithDreamSkinAsync(
        IReadOnlyDictionary<string, string>? launchEnvironment,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeMode.IsDetachedUi)
            return new CodexRestartResult(false, "独立模式禁止关闭、启动或重启 Codex。");
        var state = await ReadStateAsync(cancellationToken);
        if (state.Connected && state.IsTurnRunning)
            return new CodexRestartResult(false, "Codex 正在回答，不能现在重开。");
        if (!File.Exists(DreamSkinStartScript))
            return new CodexRestartResult(false, "没有找到 Codex 皮肤启动文件。");

        try
        {
            await using var session = await ConnectAsync(cancellationToken);
            await session.CloseBrowserAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return new CodexRestartResult(false, $"Codex 没有正常关闭：{ex.Message}");
        }

        var closeDeadline = DateTime.UtcNow.AddSeconds(15);
        var closed = false;
        while (DateTime.UtcNow < closeDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await ReadStateAsync(cancellationToken);
            if (!current.Connected)
            {
                closed = true;
                break;
            }
            await Task.Delay(300, cancellationToken);
        }
        if (!closed)
            return new CodexRestartResult(false, "Codex 没有正常关闭。请保存输入后手动关掉所有 Codex 窗口。");

        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (launchEnvironment is not null)
        {
            foreach (var pair in launchEnvironment)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                    return new CodexRestartResult(false, "Codex 托管线路缺少安全启动环境，已取消重开。");
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-STA");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("RemoteSigned");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(DreamSkinStartScript);
        startInfo.ArgumentList.Add("-Port");
        startInfo.ArgumentList.Add(DreamSkinPort.ToString());
        startInfo.ArgumentList.Add("-RestartExisting");
        startInfo.ArgumentList.Add("-FullTheme");
        startInfo.ArgumentList.Add("-OperationLockTimeoutMilliseconds");
        startInfo.ArgumentList.Add("15000");

        using var launcher = new Process { StartInfo = startInfo };
        try
        {
            if (!launcher.Start())
                return new CodexRestartResult(false, "Codex 皮肤启动失败。");
            var stdoutTask = launcher.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = launcher.StandardError.ReadToEndAsync(cancellationToken);
            await launcher.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(75), cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (launcher.ExitCode != 0)
            {
                var detail = LastUsefulLine(stderr) ?? LastUsefulLine(stdout) ?? "启动程序返回失败";
                return new CodexRestartResult(false, $"Codex 没有重开成功：{detail}");
            }
        }
        catch (TimeoutException)
        {
            return new CodexRestartResult(false, "Codex 重开超时了。");
        }
        catch (Exception ex)
        {
            return new CodexRestartResult(false, $"Codex 没有重开成功：{ex.Message}");
        }

        var readyDeadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < readyDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = await ReadStateAsync(cancellationToken);
            if (ready.Connected) return new CodexRestartResult(true, "Codex 已经重新打开。");
            await Task.Delay(500, cancellationToken);
        }
        return new CodexRestartResult(false, "Codex 已启动，但模型按钮还没准备好。请打开刚才的聊天后再点一次。");
    }

    private async Task<CdpSession> ConnectAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("/json/list", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Dream Skin 返回了错误的页面列表。");

        string? webSocketUrl = null;
        foreach (var target in document.RootElement.EnumerateArray())
        {
            if (!string.Equals(ReadString(target, "type"), "page", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(ReadString(target, "url"), CodexPageUrl, StringComparison.Ordinal)) continue;
            if (!string.Equals(ReadString(target, "title"), "Codex", StringComparison.OrdinalIgnoreCase)) continue;
            webSocketUrl = ReadString(target, "webSocketDebuggerUrl");
            if (!string.IsNullOrWhiteSpace(webSocketUrl)) break;
        }
        if (!Uri.TryCreate(webSocketUrl, UriKind.Absolute, out var uri)
            || !uri.IsLoopback
            || !uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("没有找到可信的 Codex 本机页面。");

        return await CdpSession.ConnectAsync(uri, cancellationToken);
    }

    private static async Task<CodexDesktopState> ReadStateAsync(
        CdpSession session,
        CancellationToken cancellationToken)
    {
        var json = await session.EvaluateJsonAsync(ReadStateScript, cancellationToken);
        var running = ReadBoolean(json, "running");
        var model = ReadString(json, "currentModel");
        return new CodexDesktopState(
            true,
            running,
            model,
            running
                ? "Codex 正在回答"
                : string.IsNullOrWhiteSpace(model) ? "当前没有打开聊天" : $"当前聊天：{model}",
            TaskIdentity: ReadString(json, "taskIdentity"),
            VisibleMessageCount: ReadInt(json, "messageCount"),
            ConversationFingerprint: ReadString(json, "conversationFingerprint"));
    }

    private static Task CloseMenusAsync(CdpSession session, CancellationToken cancellationToken) =>
        session.SendEscapeAsync(cancellationToken);

    private static bool SameModel(string? left, string right) =>
        string.Equals(left?.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? LastUsefulLine(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .LastOrDefault(line => line.Length > 0);

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private const string ReadStateScript = """
        JSON.stringify((() => {
          const trigger = document.querySelector('button[data-codex-intelligence-trigger="true"]');
          const currentModel = trigger?.innerText?.split(/\r?\n/)[0]?.trim() || null;
          const running = [...document.querySelectorAll('button')].some((button) => {
            const label = (button.getAttribute('aria-label') || '').trim().toLowerCase();
            return label === '停止' || label === 'stop';
          });
          const messages = [...document.querySelectorAll('[data-message-author-role], [data-local-conversation-user-anchor], [data-local-conversation-final-assistant]')];
          const signature = messages.map((node) => {
            const role = node.getAttribute('data-message-author-role') ||
              (node.hasAttribute('data-local-conversation-user-anchor') ? 'user' : 'assistant');
            const text = (node.innerText || '').replace(/\s+/g, ' ').trim();
            return `${role}:${text.length}:${text.slice(0, 24)}:${text.slice(-24)}`;
          }).join('|');
          let hash = 2166136261;
          for (let index = 0; index < signature.length; index += 1) {
            hash ^= signature.charCodeAt(index);
            hash = Math.imul(hash, 16777619);
          }
          const thread = document.querySelector('.thread-scroll-container');
          const marker = thread?.getAttribute('data-thread-id') ||
            thread?.getAttribute('data-task-id') || thread?.id || location.href;
          return {
            running,
            currentModel,
            taskIdentity: marker || location.href,
            messageCount: messages.length,
            conversationFingerprint: `${messages.length}:${(hash >>> 0).toString(16).padStart(8, '0')}`
          };
        })())
        """;

    private const string OpenModelPickerScript = """
        JSON.stringify((() => {
          const hit = (node) => {
            node.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, cancelable: true, button: 0, buttons: 1, pointerId: 1, pointerType: 'mouse', isPrimary: true }));
            node.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, button: 0, buttons: 1 }));
            node.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, cancelable: true, button: 0, pointerId: 1, pointerType: 'mouse', isPrimary: true }));
            node.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, button: 0 }));
            node.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 }));
          };
          const trigger = document.querySelector('button[data-codex-intelligence-trigger="true"]');
          if (!trigger) return { ok: false, message: '没有找到 Codex 的模型按钮。' };
          hit(trigger);
          return { ok: true };
        })())
        """;

    private const string OpenModelSubmenuScript = """
        JSON.stringify((() => {
          const hit = (node) => {
            node.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, cancelable: true, pointerId: 1, pointerType: 'mouse', isPrimary: true }));
            node.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, cancelable: true }));
            node.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, cancelable: true, button: 0, buttons: 1, pointerId: 1, pointerType: 'mouse', isPrimary: true }));
            node.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, cancelable: true, button: 0, pointerId: 1, pointerType: 'mouse', isPrimary: true }));
            node.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 }));
          };
          const item = [...document.querySelectorAll('[role="menuitem"][aria-haspopup="menu"]')]
            .find((node) => {
              const label = (node.getAttribute('aria-label') || '').trim();
              const firstLine = (node.innerText || '').split(/\r?\n/)[0].trim();
              return label.startsWith('模型 ') || label.startsWith('Model ') || firstLine === '模型' || firstLine === 'Model';
            });
          if (!item) return { ok: false, message: '没有打开 Codex 的模型列表。' };
          hit(item);
          return { ok: true };
        })())
        """;

    private const string SelectAliasScript = """
        JSON.stringify((() => {
          const alias = __ALIAS__;
          const hit = (node) => {
            node.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, cancelable: true, pointerId: 1, pointerType: 'mouse', isPrimary: true }));
            node.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, cancelable: true }));
            node.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, cancelable: true, button: 0, buttons: 1, pointerId: 1, pointerType: 'mouse', isPrimary: true }));
            node.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, cancelable: true, button: 0, pointerId: 1, pointerType: 'mouse', isPrimary: true }));
            node.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 }));
          };
          const items = [...document.querySelectorAll('[role="menuitem"]')]
            .filter((node) => !node.hasAttribute('aria-haspopup'));
          const firstLine = (node) => (node.innerText || '').split(/\r?\n/)[0].trim();
          const target = items.find((node) => firstLine(node).toLowerCase() === alias.toLowerCase());
          if (!target) return { found: false, available: items.map(firstLine).filter(Boolean) };
          hit(target);
          return { found: true };
        })())
        """;

    private sealed class CdpSession : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket;
        private int _nextId;

        private CdpSession(ClientWebSocket socket) => _socket = socket;

        public static async Task<CdpSession> ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(uri, cancellationToken);
                return new CdpSession(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public async Task<JsonElement> EvaluateJsonAsync(
            string expression,
            CancellationToken cancellationToken)
        {
            var response = await SendAsync(
                "Runtime.evaluate",
                new { expression, returnByValue = true },
                cancellationToken);
            if (!response.TryGetProperty("result", out var outer)
                || !outer.TryGetProperty("result", out var result)
                || !result.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Codex 页面没有返回可识别的结果。");
            using var document = JsonDocument.Parse(value.GetString() ?? "{}");
            return document.RootElement.Clone();
        }

        public async Task SendEscapeAsync(CancellationToken cancellationToken)
        {
            var key = new
            {
                type = "keyDown",
                key = "Escape",
                code = "Escape",
                windowsVirtualKeyCode = 27,
                nativeVirtualKeyCode = 27
            };
            await SendAsync("Input.dispatchKeyEvent", key, cancellationToken);
            await SendAsync(
                "Input.dispatchKeyEvent",
                new
                {
                    type = "keyUp",
                    key = "Escape",
                    code = "Escape",
                    windowsVirtualKeyCode = 27,
                    nativeVirtualKeyCode = 27
                },
                cancellationToken);
        }

        public async Task CloseBrowserAsync(CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _nextId);
            var payload = JsonSerializer.Serialize(new { id, method = "Browser.close", @params = new { } });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }

        private async Task<JsonElement> SendAsync(
            string method,
            object parameters,
            CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _nextId);
            var payload = JsonSerializer.Serialize(new { id, method, @params = parameters });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);

            while (true)
            {
                var message = await ReceiveTextAsync(cancellationToken);
                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId)
                    || !responseId.TryGetInt32(out var number)
                    || number != id)
                    continue;
                if (root.TryGetProperty("error", out var error))
                {
                    var errorMessage = ReadString(error, "message") ?? "Codex 页面操作失败。";
                    throw new InvalidOperationException(errorMessage);
                }
                return root.Clone();
            }
        }

        private async Task<string> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("Codex 页面连接已经关闭。");
                await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
                if (result.EndOfMessage) break;
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
                }
            }
            catch { }
            _socket.Dispose();
        }
    }
}
