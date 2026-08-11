using CodexModelManager.Models;

namespace CodexModelManager.Services;

/// <summary>
/// 中立 WorkerBroker：把"角色 → provider/model/账号/预算/超时"的绑定解析
/// 与执行解耦，并对外暴露统一的 delegate_to_worker 语义。
/// P0 验收要求：身份/价格/币种/预算/路由任一未知时，调用前失败关闭；
/// 调用后按实际 token 用量扣减预算。
/// </summary>
public sealed class WorkerBroker
{
    private readonly ExternalWorkerService _worker;
    private readonly SubagentConfigurationService _configuration;
    private readonly WorkerBudgetLedger _budget;

    public WorkerBroker(
        ExternalWorkerService worker,
        SubagentConfigurationService configuration,
        WorkerBudgetLedger? budget = null)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _budget = budget ?? new WorkerBudgetLedger();
    }

    /// <summary>
    /// 委托一个纯文本子任务给指定角色的工人。
    /// P0 失败关闭：角色、价格、币种、预算、超时任一未知时拒绝调用；
    /// 委托完成后按实际 token 用量扣减预算。
    /// </summary>
    public async Task<ExternalWorkerCompletion> DelegateAsync(
        string roleId,
        string task,
        string? context = null,
        int maxOutputTokens = ExternalWorkerService.DefaultOutputTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleId);
        ArgumentNullException.ThrowIfNull(task);

        // P0 失败关闭：角色必须存在且允许外部工人。
        var role = ResolveRoleOrThrow(roleId);
        ValidatePricingOrThrow(role);

        // 预算检查：超支或未配置 → 拒绝（P0 失败关闭）。
        var budgetBlock = _budget.CheckBeforeCall(role);
        if (budgetBlock is not null)
            throw new WorkerBrokerException("budget_exceeded", budgetBlock);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (role.MaxTimeoutSeconds is > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(role.MaxTimeoutSeconds.Value));

        var completion = await _worker.DelegateAsync(new ExternalWorkerInvocation(
            roleId,
            task,
            context,
            maxOutputTokens), timeoutCts.Token);

        // 委托完成后按实际用量扣减预算。
        await _budget.DeductAsync(
            role,
            completion.Usage.PromptTokens,
            completion.Usage.CompletionTokens,
            cancellationToken);

        return completion;
    }

    /// <summary>列出当前可委托的角色（供 MCP 工具选择器使用）。</summary>
    public IReadOnlyList<ExternalWorkerRoleOption> ListDelegatableRoles() =>
        _worker.ReadEnabledRoleOptions();

    /// <summary>解析角色绑定的预算与价格信息（供界面/审计显示）。</summary>
    public WorkerPricing? GetRolePricing(string roleId)
    {
        var role = _configuration.Roles.FirstOrDefault(item => item.Id.Equals(roleId, StringComparison.OrdinalIgnoreCase));
        if (role is null) return null;
        return new WorkerPricing(
            role.PricePerMillionTokens,
            role.Currency,
            role.BudgetLimit,
            role.MaxTimeoutSeconds);
    }

    /// <summary>当前角色已消耗 / 剩余预算（供界面显示）。</summary>
    public WorkerBudgetView? GetRoleBudget(string roleId)
    {
        var role = _configuration.Roles.FirstOrDefault(item => item.Id.Equals(roleId, StringComparison.OrdinalIgnoreCase));
        if (role is null || role.BudgetLimit is null) return null;
        var spent = _budget.GetSpent(roleId);
        return new WorkerBudgetView(roleId, role.Currency ?? string.Empty, role.BudgetLimit.Value, spent, role.BudgetLimit.Value - spent);
    }

    private SubagentRoleDefinition ResolveRoleOrThrow(string roleId)
    {
        var roles = _configuration.Roles;
        var role = roles.FirstOrDefault(item => item.Id.Equals(roleId, StringComparison.OrdinalIgnoreCase));
        if (role is null)
            throw new WorkerBrokerException("role_not_found", $"角色 {roleId} 不存在，已拒绝调用（P0 失败关闭）。");
        if (!role.AllowsExternalWorker)
            throw new WorkerBrokerException("role_external_forbidden", $"角色 {roleId} 不允许外部工人，已拒绝调用（P0 失败关闭）。");
        return role;
    }

    private static void ValidatePricingOrThrow(SubagentRoleDefinition role)
    {
        if (role.PricePerMillionTokens is null or < 0)
            throw new WorkerBrokerException(
                "price_unknown",
                $"角色 {role.Id} 未配置价格（pricePerMillionTokens），已拒绝调用（P0 失败关闭）。");
        if (string.IsNullOrWhiteSpace(role.Currency))
            throw new WorkerBrokerException(
                "currency_unknown",
                $"角色 {role.Id} 未配置币种（currency），已拒绝调用（P0 失败关闭）。");
        if (role.BudgetLimit is null or < 0)
            throw new WorkerBrokerException(
                "budget_unknown",
                $"角色 {role.Id} 未配置预算上限（budgetLimit），已拒绝调用（P0 失败关闭）。");
        if (role.MaxTimeoutSeconds is null or <= 0)
            throw new WorkerBrokerException(
                "timeout_unknown",
                $"角色 {role.Id} 未配置超时上限（maxTimeoutSeconds），已拒绝调用（P0 失败关闭）。");
    }
}

public sealed record WorkerPricing(
    decimal? PricePerMillionTokens,
    string? Currency,
    decimal? BudgetLimit,
    int? MaxTimeoutSeconds);

public sealed record WorkerBudgetView(
    string RoleId,
    string Currency,
    decimal BudgetLimit,
    decimal Spent,
    decimal Remaining);

public sealed class WorkerBrokerException : Exception
{
    public string Code { get; }

    public WorkerBrokerException(string code, string message) : base(message)
    {
        Code = code;
    }
}
