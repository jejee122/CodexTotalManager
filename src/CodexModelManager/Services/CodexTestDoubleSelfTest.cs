using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexModelManager.Services;

public static class CodexTestDoubleSelfTest
{
    public static async Task<CodexTestDoubleReport> RunAsync(
        AppServices services,
        int cycles,
        CancellationToken cancellationToken = default)
    {
        if (!RuntimeMode.IsDetachedUi || !RuntimeMode.IsCodexTestDouble)
            throw new InvalidOperationException("Codex 测试替身自检只能在独立测试替身模式运行。");
        if (RuntimeMode.AllowsExternalStatusConnections)
            throw new InvalidOperationException("Codex 测试替身自检禁止服务器和 v2rayN 外部状态连接。");
        if (cycles is < 1 or > 50_000)
            throw new ArgumentOutOfRangeException(nameof(cycles));

        var engineUri = RuntimeMode.CodexTestDoubleEngineUri
                        ?? throw new InvalidOperationException("测试替身引擎地址缺失。");
        var gatewayUri = RuntimeMode.CodexTestDoubleGatewayUri
                         ?? throw new InvalidOperationException("测试替身网关地址缺失。");
        var token = RuntimeMode.CodexTestDoubleToken
                    ?? throw new InvalidOperationException("测试替身身份令牌缺失。");
        var expectedTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        using var engineHttp = NewClient(engineUri);
        using var gatewayHttp = NewClient(gatewayUri);
        await ValidateIdentityAsync(
            engineHttp,
            "/healthz",
            "CodexTestDoubleEngine",
            engineUri.Port,
            expectedTokenHash,
            cancellationToken);
        await ValidateIdentityAsync(
            gatewayHttp,
            "/health",
            "CodexTestDoubleGateway",
            gatewayUri.Port,
            expectedTokenHash,
            cancellationToken);

        var engineHealthy = await services.OpenCodex.IsHealthyAsync(cancellationToken);
        if (!engineHealthy)
            throw new InvalidOperationException("总管家的原生引擎客户端没有接受测试替身健康响应。");
        var runtime = await services.OpenCodex.GetRuntimeStatusAsync(cancellationToken);
        if (!runtime.Healthy || runtime.Port != engineUri.Port)
            throw new InvalidOperationException("总管家没有读到测试替身的正确运行端口。");

        var models = await services.OpenCodex.GetModelsAsync(services.Settings, cancellationToken);
        var providers = await services.OpenCodex.GetProvidersAsync(services.Settings, cancellationToken);
        var accountsBefore = await services.OpenCodex.GetCodexAccountsAsync(false, cancellationToken);
        if (models.Count < 2 || providers.Count < 1 || accountsBefore.Accounts.Count < 2)
            throw new InvalidOperationException("测试替身没有返回完整的假模型、假来源或假账号清单。");

        var initialUi = await ReadUiSnapshotAsync(engineHttp, cancellationToken);
        if (initialUi.Provider != "openai"
            || initialUi.Model != "gpt-5.6-sol"
            || initialUi.TaskId != "fake-task-001"
            || initialUi.CodexRestartCount != 0)
            throw new InvalidOperationException("The fake Codex did not start on the isolated official Pro route with a stable task identity.");

        var officialChat = await SendChatAsync(
            engineHttp,
            "Please remember the test code BLUE-731.",
            cancellationToken);
        if (officialChat.Provider != "openai"
            || officialChat.Model != "gpt-5.6-sol"
            || officialChat.TaskId != initialUi.TaskId
            || officialChat.ContextPreserved)
            throw new InvalidOperationException("The official-route baseline chat was not recorded in the expected fake task.");
        var beforePoolSwitch = await ReadUiSnapshotAsync(engineHttp, cancellationToken);

        await services.OpenCodex.SetActiveTargetAsync(
            "fake-provider",
            "fake-model-b",
            cancellationToken);
        var activeTarget = await services.OpenCodex.GetActiveTargetAsync(cancellationToken);
        if (activeTarget is null
            || activeTarget.Value.Provider != "fake-provider"
            || activeTarget.Value.Model != "fake-model-b")
            throw new InvalidOperationException("总管家没有完成假模型路由切换回路。");

        var afterPoolSwitch = await ReadUiSnapshotAsync(engineHttp, cancellationToken);
        AssertHotSwitchContinuity(
            beforePoolSwitch,
            afterPoolSwitch,
            "fake-provider",
            "fake-model-b");
        var officialToPoolHotSwitchSucceeded = true;

        await services.OpenCodex.SetPreferredCodexAccountAsync("fake-account-b", cancellationToken);
        await services.OpenCodex.SetCodexAutoSwitchThresholdAsync(73, cancellationToken);
        await services.OpenCodex.SetCodexFailoverThresholdAsync(4, cancellationToken);
        var accountsAfter = await services.OpenCodex.GetCodexAccountsAsync(true, cancellationToken);
        if (accountsAfter.Settings.ActiveAccountId != "fake-account-b"
            || accountsAfter.Settings.AutoSwitchThreshold != 73
            || accountsAfter.Settings.FailoverThreshold != 4)
            throw new InvalidOperationException("总管家的假账号池切换或阈值回读不一致。");

        var providerTest = await services.OpenCodex.TestProviderAsync("fake-provider", cancellationToken);
        if (!providerTest.Success)
            throw new InvalidOperationException("总管家的假来源测试回路失败。");

        var skinsBefore = await services.DreamSkin.DiscoverAsync(cancellationToken);
        if (!skinsBefore.EngineReady
            || !skinsBefore.LiveSessionConnected
            || skinsBefore.Themes.Count < 4)
            throw new InvalidOperationException("总管家没有读到完整的假 Codex 在线皮肤库。");
        var skinSession = await services.DreamSkin.PrepareLiveSessionAsync(false, cancellationToken);
        if (!skinSession.Success)
            throw new InvalidOperationException("总管家没有连接假 Codex 的在线换肤通道。");
        var installedSkin = await services.DreamSkin.ApplyInstalledThemeAsync(
            "paper-light",
            false,
            cancellationToken);
        if (!installedSkin.Success)
            throw new InvalidOperationException("总管家没有完成假 Codex 内置皮肤切换。");
        var paperSnapshot = await services.DreamSkin.DiscoverAsync(cancellationToken);
        if (paperSnapshot.ActiveThemeId != "paper-light" || paperSnapshot.IsPaused)
            throw new InvalidOperationException("假 Codex 内置皮肤切换后的回读状态不一致。");

        const string onlineThemeUri = "dreamskin://apply?version=ver_celestial2026";
        var onlineSkin = await services.DreamSkin.ApplyCommunityThemeAsync(
            onlineThemeUri,
            cancellationToken);
        if (!onlineSkin.Success)
            throw new InvalidOperationException("总管家没有完成假 Codex 在线皮肤切换。");
        var onlineSnapshot = await services.DreamSkin.DiscoverAsync(cancellationToken);
        if (onlineSnapshot.ActiveThemeId != "gallery-celestial"
            || onlineSnapshot.IsPaused
            || !onlineSnapshot.LiveSessionConnected)
            throw new InvalidOperationException("假 Codex 在线皮肤应用后的回读状态不一致。");

        var officialSkin = await services.DreamSkin.UseOfficialAppearanceAsync(false, cancellationToken);
        if (!officialSkin.Success)
            throw new InvalidOperationException("总管家没有完成假 Codex 官方外观恢复测试。");
        var officialSnapshot = await services.DreamSkin.DiscoverAsync(cancellationToken);
        if (!officialSnapshot.IsPaused || officialSnapshot.ActiveThemeId != "official")
            throw new InvalidOperationException("假 Codex 官方外观状态回读不一致。");
        onlineSkin = await services.DreamSkin.ApplyCommunityThemeAsync(onlineThemeUri, cancellationToken);
        if (!onlineSkin.Success)
            throw new InvalidOperationException("假 Codex 在线皮肤二次应用失败。");

        using var chatResponse = await engineHttp.PostAsJsonAsync(
            "/api/ui/chat",
            new { prompt = "验证模型切换和皮肤切换" },
            cancellationToken);
        chatResponse.EnsureSuccessStatusCode();
        using var chatJson = JsonDocument.Parse(
            await chatResponse.Content.ReadAsStringAsync(cancellationToken));
        var chatRoot = chatJson.RootElement;
        var chatIsTestOnly = chatRoot.TryGetProperty("testOnly", out var chatTestOnly)
                             && chatTestOnly.ValueKind == JsonValueKind.True;
        if (ReadString(chatRoot, "model") != "fake-model-b"
            || ReadString(chatRoot, "provider") != "fake-provider"
            || ReadString(chatRoot, "taskId") != beforePoolSwitch.TaskId
            || !ReadBool(chatRoot, "contextPreserved")
            || !(ReadString(chatRoot, "rememberedUserPrompt")?.Contains("BLUE-731", StringComparison.Ordinal) ?? false)
            || !chatIsTestOnly
            || string.IsNullOrWhiteSpace(ReadString(chatRoot, "response")))
            throw new InvalidOperationException("假 Codex 交互聊天没有使用切换后的测试模型。");

        var afterPoolChat = await ReadUiSnapshotAsync(engineHttp, cancellationToken);
        await services.OpenCodex.SetActiveTargetAsync(
            "openai",
            "gpt-5.6-sol",
            cancellationToken);
        var finalActiveTarget = await services.OpenCodex.GetActiveTargetAsync(cancellationToken);
        if (finalActiveTarget is null
            || finalActiveTarget.Value.Provider != "openai"
            || finalActiveTarget.Value.Model != "gpt-5.6-sol")
            throw new InvalidOperationException("The fake route did not switch back to the isolated official Pro target.");
        var afterOfficialSwitch = await ReadUiSnapshotAsync(engineHttp, cancellationToken);
        AssertHotSwitchContinuity(
            afterPoolChat,
            afterOfficialSwitch,
            "openai",
            "gpt-5.6-sol");
        var conversationContinuityVerified = true;

        var desktop = await services.CodexDesktop.ReadStateAsync(cancellationToken);
        if (desktop.Connected)
            throw new InvalidOperationException("测试替身模式错误地连接了真实 Codex 桌面。");

        var failures = new ConcurrentQueue<string>();
        var completed = 0;
        var timer = Stopwatch.StartNew();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, cycles),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount * 2, 8, 32),
                CancellationToken = cancellationToken
            },
            async (index, tokenForRequest) =>
            {
                try
                {
                    using var response = (index % 6) switch
                    {
                        0 => await engineHttp.GetAsync("/healthz", tokenForRequest),
                        1 => await engineHttp.GetAsync("/api/models", tokenForRequest),
                        2 => await gatewayHttp.GetAsync("/health", tokenForRequest),
                        3 => await gatewayHttp.PostAsJsonAsync(
                            "/v1/responses",
                            new { model = "fake-model-a", input = "test-only", stream = false },
                            tokenForRequest),
                        4 => await engineHttp.GetAsync("/api/skins", tokenForRequest),
                        _ => await engineHttp.GetAsync("/api/ui/state", tokenForRequest)
                    };
                    if (!response.IsSuccessStatusCode)
                        failures.Enqueue($"{index}:{(int)response.StatusCode}");
                    else
                        Interlocked.Increment(ref completed);
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"{index}:{ex.GetType().Name}");
                }
            });
        timer.Stop();
        if (!failures.IsEmpty)
            throw new InvalidOperationException(
                $"测试替身压力请求失败 {failures.Count} 次：{string.Join(",", failures.Take(5))}");

        using var fakeReportResponse = await engineHttp.GetAsync("/api/test/report", cancellationToken);
        fakeReportResponse.EnsureSuccessStatusCode();
        using var fakeReport = JsonDocument.Parse(
            await fakeReportResponse.Content.ReadAsStringAsync(cancellationToken));
        var fakeRoot = fakeReport.RootElement;
        var nonLoopback = ReadInt(fakeRoot, "nonLoopbackRequests");
        var outbound = ReadInt(fakeRoot, "outboundRequests");
        var mutations = ReadInt(fakeRoot, "mutationCount");
        var totalRequests = ReadInt(fakeRoot, "totalRequests");
        var codexRestartCount = ReadInt(fakeRoot, "codexRestartCount");
        var finalProvider = ReadString(fakeRoot, "currentProvider");
        var finalModel = ReadString(fakeRoot, "currentModel");
        var stableTaskId = ReadString(fakeRoot, "taskId");
        if (nonLoopback != 0 || outbound != 0)
            throw new InvalidOperationException("测试替身记录到了非回环访问或外部网络请求。");
        if (codexRestartCount != 0
            || finalProvider != "openai"
            || finalModel != "gpt-5.6-sol"
            || stableTaskId != beforePoolSwitch.TaskId)
            throw new InvalidOperationException("The official-to-pool-to-official flow restarted Codex or changed the fake task identity.");

        var skinSwitches = ReadInt(fakeRoot, "skinSwitchCount");
        var modelSwitches = ReadInt(fakeRoot, "modelSwitchCount");
        var chatTurns = ReadInt(fakeRoot, "chatTurnCount");
        var activeSkin = ReadString(fakeRoot, "currentSkin") ?? string.Empty;
        if (totalRequests < cycles || mutations < 9)
            throw new InvalidOperationException("测试替身请求账本不完整。");
        if (modelSwitches < 2 || skinSwitches < 4 || chatTurns < 2
            || activeSkin != "gallery-celestial")
            throw new InvalidOperationException("测试替身的模型、聊天或在线皮肤切换账本不完整。");

        return new CodexTestDoubleReport(
            true,
            "安装版总管家已通过 Codex 测试替身功能回路与压力测试。",
            cycles,
            completed,
            timer.ElapsedMilliseconds,
            engineUri.Port,
            gatewayUri.Port,
            engineHealthy,
            models.Count,
            providers.Count,
            accountsAfter.Accounts.Count,
            $"{initialUi.Provider}/{initialUi.Model} -> {activeTarget.Value.Provider}/{activeTarget.Value.Model} -> {finalActiveTarget.Value.Provider}/{finalActiveTarget.Value.Model}",
            onlineSnapshot.Themes.Count,
            activeSkin,
            true,
            true,
            officialToPoolHotSwitchSucceeded,
            conversationContinuityVerified,
            codexRestartCount,
            stableTaskId ?? string.Empty,
            desktop.Connected,
            totalRequests,
            mutations,
            nonLoopback,
            outbound);
    }

    private static async Task<FakeUiSnapshot> ReadUiSnapshotAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/api/ui/state", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        return new FakeUiSnapshot(
            ReadString(root, "currentProvider") ?? string.Empty,
            ReadString(root, "currentModel") ?? string.Empty,
            ReadString(root, "taskId") ?? string.Empty,
            ReadInt(root, "messageCount"),
            ReadString(root, "conversationFingerprint") ?? string.Empty,
            ReadInt(root, "codexRestartCount"));
    }

    private static async Task<FakeChatSnapshot> SendChatAsync(
        HttpClient client,
        string prompt,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/ui/chat",
            new { prompt },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        return new FakeChatSnapshot(
            ReadString(root, "provider") ?? string.Empty,
            ReadString(root, "model") ?? string.Empty,
            ReadString(root, "taskId") ?? string.Empty,
            ReadBool(root, "contextPreserved"));
    }

    private static void AssertHotSwitchContinuity(
        FakeUiSnapshot before,
        FakeUiSnapshot after,
        string expectedProvider,
        string expectedModel)
    {
        if (string.IsNullOrWhiteSpace(before.TaskId)
            || string.IsNullOrWhiteSpace(before.ConversationFingerprint)
            || after.Provider != expectedProvider
            || after.Model != expectedModel
            || after.TaskId != before.TaskId
            || after.MessageCount != before.MessageCount
            || after.ConversationFingerprint != before.ConversationFingerprint
            || before.CodexRestartCount != 0
            || after.CodexRestartCount != 0)
            throw new InvalidOperationException(
                $"The hot switch to {expectedProvider}/{expectedModel} changed the task, chat history, or restart count.");
    }

    private static HttpClient NewClient(Uri baseAddress) => new()
    {
        BaseAddress = baseAddress,
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static async Task ValidateIdentityAsync(
        HttpClient client,
        string path,
        string expectedService,
        int expectedPort,
        string expectedTokenHash,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        if (!string.Equals(ReadString(root, "testDoubleService"), expectedService, StringComparison.Ordinal)
            || ReadInt(root, "port") != expectedPort
            || !string.Equals(ReadString(root, "runTokenSha256"), expectedTokenHash, StringComparison.Ordinal))
            throw new InvalidOperationException($"{expectedService} 身份、端口或一次性令牌指纹不匹配。");
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : -1;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private sealed record FakeUiSnapshot(
        string Provider,
        string Model,
        string TaskId,
        int MessageCount,
        string ConversationFingerprint,
        int CodexRestartCount);

    private sealed record FakeChatSnapshot(
        string Provider,
        string Model,
        string TaskId,
        bool ContextPreserved);
}

public sealed record CodexTestDoubleReport(
    bool Success,
    string Message,
    int Cycles,
    int RequestsCompleted,
    long ElapsedMs,
    int EnginePort,
    int GatewayPort,
    bool EngineHealthy,
    int ModelCount,
    int ProviderCount,
    int AccountCount,
    string ActiveTarget,
    int SkinCount,
    string ActiveSkin,
    bool OnlineSkinSwitchSucceeded,
    bool InteractiveChatSucceeded,
    bool OfficialToPoolHotSwitchSucceeded,
    bool ConversationContinuityVerified,
    int CodexRestartCount,
    string StableTaskId,
    bool RealDesktopConnected,
    int FakeTotalRequests,
    int FakeMutationCount,
    int FakeNonLoopbackRequests,
    int FakeOutboundRequests);
