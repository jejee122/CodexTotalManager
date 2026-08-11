namespace CodexModelManager.Services;

public static class SelfTest
{
    public static async Task<(bool Success, string Message)> RunAsync(AppServices services)
    {
        var runtime = await services.OpenCodex.GetRuntimeStatusAsync();
        if (!runtime.Healthy) return (false, $"SELF_TEST_FAILED: OpenCodex 未运行；{runtime.LastError}");
        var models = await services.OpenCodex.GetModelsAsync(services.Settings);
        if (!models.Any(model => model.Provider == "openai" && model.Id == "gpt-5.6-sol"))
            return (false, "SELF_TEST_FAILED: 找不到官方 gpt-5.6-sol");
        var safe = services.CodexConfig.MemoryProtectionLooksSafe();
        if (!safe) return (false, "SELF_TEST_FAILED: 检测到旧 custom 配置");
        var active = await services.OpenCodex.GetActiveTargetAsync();
        var configuredDefault = services.CodexConfig.ReadDefaultModel();
        var pools = services.PoolCatalog.GetPools();
        var protectedOfficial = pools.FirstOrDefault(pool => pool.Id == Models.PoolCatalogDefaults.OfficialPoolId);
        if (protectedOfficial is null || !protectedOfficial.IsProtected || !protectedOfficial.Enabled)
            return (false, "SELF_TEST_FAILED: 官方 Pro 保底号池不存在或未受保护");
        var activePoolState = services.PoolCatalog.GetActive();
        var activePool = pools.FirstOrDefault(pool => pool.Id == activePoolState.PoolId);
        if (activePool is null) return (false, "SELF_TEST_FAILED: 当前号池不存在");
        var expectedDefault = activePool.Transport is Models.PoolTransport.OfficialCodex or Models.PoolTransport.NativeCodexAccount
            ? activePoolState.Model
            : activePool.RouteAlias;
        if (!string.Equals(configuredDefault, expectedDefault, StringComparison.OrdinalIgnoreCase))
            return (false, $"SELF_TEST_FAILED: Codex 默认模型 {configuredDefault} 与当前号池 {expectedDefault} 不一致");
        var accountData = await services.OpenCodex.GetCodexAccountsAsync();
        var nativePoolActive = activePool.Transport is Models.PoolTransport.OfficialCodex or Models.PoolTransport.NativeCodexAccount;
        var activeAccount = nativePoolActive
            ? !string.IsNullOrWhiteSpace(activePool.NativeAccountId)
                ? accountData.Accounts.FirstOrDefault(account =>
                    account.Id.Equals(activePool.NativeAccountId, StringComparison.OrdinalIgnoreCase))
                : accountData.Accounts.FirstOrDefault(account => account.IsMain) ?? accountData.Accounts.FirstOrDefault()
            : accountData.Accounts.FirstOrDefault(account => account.IsActive)
              ?? accountData.Accounts.FirstOrDefault(account => account.IsMain)
              ?? accountData.Accounts.FirstOrDefault();
        if (activeAccount is null || !activeAccount.HasCredential || activeAccount.NeedsReauth)
            return (false, "SELF_TEST_FAILED: 当前 Codex 原生账号需要重新登录");
        var expectedAccountMode = activeAccount.IsMain ? "direct" : "pool";
        if (nativePoolActive
            && !accountData.Settings.Mode.Equals(expectedAccountMode, StringComparison.OrdinalIgnoreCase))
            return (false, $"SELF_TEST_FAILED: 当前账号需要 {expectedAccountMode} 模式，实际为 {accountData.Settings.Mode}");
        if (nativePoolActive && !activeAccount.IsMain && accountData.Settings.AutoSwitchThreshold != 0)
            return (false, "SELF_TEST_FAILED: 当前 Plus/附加账号仍开启自动切号");
        if (nativePoolActive && !activeAccount.IsMain && accountData.Settings.FailoverThreshold != 0)
            return (false, "SELF_TEST_FAILED: 当前 Codex 账号线路仍开启故障串池");
        if (nativePoolActive && !activeAccount.IsMain
            && !string.Equals(accountData.Settings.ActiveAccountId, activeAccount.Id, StringComparison.OrdinalIgnoreCase))
            return (false, "SELF_TEST_FAILED: OpenCodex 当前扣费账号与总管家选择不一致");
        if (!await services.Dashboard.IsV2rayReadyAsync())
            return (false, "SELF_TEST_FAILED: v2rayN 本机连接没有准备好");
        var localServices = await services.LocalServices.GetStatusesAsync();
        if (localServices.Count != 3)
            return (false, "SELF_TEST_FAILED: 本机服务清单不完整");
        var dream = localServices.FirstOrDefault(service => service.Id == "dreamskin");
        if (dream is null || dream.Capability == "不可用")
            return (false, "SELF_TEST_FAILED: Dream Skin 管理不可用");
        var backups = services.BackupCatalog.List();
        if (!backups.Any(item => item.CanRestore))
            return (false, "SELF_TEST_FAILED: 没有读到可恢复备份");
        var recentRoute = await services.OpenCodex.GetRecentRouteAsync();
        var usageTimeline = await services.OpenCodex.GetUsageTimelineAsync();
        if (usageTimeline.SourceAvailable
            && usageTimeline.TotalTokens < usageTimeline.Days.Sum(day => day.TotalTokens))
            return (false, "SELF_TEST_FAILED: Token 历史汇总小于每日明细，统计口径异常");
        var desktop = await services.CodexDesktop.ReadStateAsync();
        if (!desktop.Connected)
            return (false, "SELF_TEST_FAILED: 普通 Codex 当前任务模型按钮不可读");
        if (!string.Equals(desktop.CurrentModel, expectedDefault, StringComparison.OrdinalIgnoreCase))
            return (false, $"SELF_TEST_FAILED: 当前号池期望 {expectedDefault}，但当前任务是 {desktop.CurrentModel ?? "未选择模型"}");
        var activeText = active is null ? "尚未创建 cmm/main" : $"{active.Value.Provider}/{active.Value.Model}";
        var routeText = recentRoute.HasData ? $"{recentRoute.ActualProvider}/{recentRoute.ActualModel}" : "暂无请求";
        return (true, $"SELF_TEST_OK: OpenCodex PID={runtime.ProcessId}，模型={models.Count}个，当前账号={activePool.DisplayName}，扣费账号={(activeAccount.IsMain ? "Pro主账号" : activeAccount.PlanText)}，账号模式={accountData.Settings.Mode}，兼容入口={activeText}，Codex当前任务={desktop.CurrentModel ?? "未选择"}，Codex默认={configuredDefault}，最近实际路由={routeText}，Token日志={usageTimeline.LogCount}条/{Models.UsageFormatting.Number(usageTimeline.TotalTokens)}，入口={pools.Count}个，本机服务={localServices.Count}项，备份={backups.Count}项，记忆保护=正常");
    }
}
