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
/// Reads the ordinary Windows accessibility tree first and the already-installed
/// Dream Skin CDP endpoint only as a read-only fallback. Model selection remains
/// a user action in Codex's own menu.
/// </summary>
public sealed class CodexDesktopBridgeService
{
    private const int DreamSkinPort = 9335;
    private const string CodexPageUrl = "app://-/index.html";
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
        var current = await ReadStateAsync(cancellationToken);
        if (current.IsTurnRunning)
            return new CodexAliasSwitchResult(
                CodexAliasSwitchStatus.Busy,
                "Codex 正在回答。等回答结束后再手动选择模型。",
                current.CurrentModel);
        if (SameModel(current.CurrentModel, alias))
            return new CodexAliasSwitchResult(
                CodexAliasSwitchStatus.Success,
                "当前任务已经显示这个模型。",
                current.CurrentModel);
        return new CodexAliasSwitchResult(
            CodexAliasSwitchStatus.Unavailable,
            $"总管家已准备 {alias}，但不会自动点击或重启 Codex。请在 Codex 自己的模型菜单中选择。",
            current.CurrentModel);
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

    private static bool SameModel(string? left, string right) =>
        string.Equals(left?.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

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
