using System.Text;
using System.Text.Json;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

/// <summary>
/// 工人预算账本：按角色记录累计消耗（美元），基于实际 token 用量 × 角色单价。
/// 委托前查余额，超支/未配置即拒绝（P0 失败关闭）；委托后按实际用量扣减并持久化。
/// </summary>
public sealed class WorkerBudgetLedger
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private Dictionary<string, WorkerBudgetState> _state = new(StringComparer.OrdinalIgnoreCase);
    private bool _disabled;
    private string? _disableReason;

    public WorkerBudgetLedger(string? path = null)
    {
        var localData = AppSettingsService.ResolveDefaultDataDirectory();
        _path = path ?? System.IO.Path.Combine(localData, "worker-budget.json");
        Load();
    }

    public string BudgetFilePath => _path;

    /// <summary>
    /// 委托前检查：角色预算是否已知且未超支。
    /// 返回 null = 可调用；返回字符串 = 拒绝原因（P0 失败关闭）。
    /// </summary>
    public string? CheckBeforeCall(SubagentRoleDefinition role)
    {
        if (_disabled)
            return $"预算账本已禁用（{_disableReason}），已拒绝调用（P0 失败关闭）。";
        if (role.PricePerMillionTokens is null or < 0)
            return $"角色 {role.Id} 未配置价格，已拒绝调用（P0 失败关闭）。";
        if (string.IsNullOrWhiteSpace(role.Currency))
            return $"角色 {role.Id} 未配置币种，已拒绝调用（P0 失败关闭）。";
        if (role.BudgetLimit is null or < 0)
            return $"角色 {role.Id} 未配置预算上限，已拒绝调用（P0 失败关闭）。";

        var spent = GetSpent(role.Id);
        if (spent >= role.BudgetLimit.Value)
            return $"角色 {role.Id} 预算已耗尽（已用 {spent:F4} {role.Currency} / 上限 {role.BudgetLimit:F2} {role.Currency}），已拒绝调用。";
        return null;
    }

    /// <summary>
    /// 委托完成后按实际用量扣减成本。
    /// 成本 = (promptTokens + completionTokens) / 1_000_000 * 单价。
    /// </summary>
    public async Task<decimal> DeductAsync(
        SubagentRoleDefinition role,
        int? promptTokens,
        int? completionTokens,
        CancellationToken cancellationToken = default)
    {
        if (role.PricePerMillionTokens is null or < 0 || role.BudgetLimit is null)
            throw new InvalidOperationException($"角色 {role.Id} 未配置价格或预算，无法记账（失败关闭）。");

        // 负数 token 用量视为异常，拒绝记账（失败关闭）。
        if (promptTokens is < 0 || completionTokens is < 0)
            throw new InvalidOperationException($"角色 {role.Id} 返回了负数 token 用量（prompt={promptTokens}, completion={completionTokens}），已拒绝记账。");

        // 实际用量完全缺失时不得按 0 计费（与"未知时失败关闭"一致）。
        if (promptTokens is null && completionTokens is null)
            throw new InvalidOperationException($"角色 {role.Id} 未返回 token 用量，无法记账（P0 失败关闭）。");

        var totalTokens = (long)(promptTokens ?? 0) + (completionTokens ?? 0);
        var cost = (decimal)totalTokens / 1_000_000m * role.PricePerMillionTokens.Value;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = GetSpentLocked(role.Id);
            var updated = new WorkerBudgetState
            {
                RoleId = role.Id,
                Currency = role.Currency ?? string.Empty,
                BudgetLimit = role.BudgetLimit.Value,
                Spent = current + cost,
                LastDeductedAt = DateTimeOffset.UtcNow
            };
            _state[role.Id] = updated;
            // 持久化失败必须上报（失败关闭），否则预算记录不可靠。
            PersistLocked();
            return cost;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>当前角色已消耗金额。</summary>
    public decimal GetSpent(string roleId)
    {
        lock (_gate)
        {
            return GetSpentLocked(roleId);
        }
    }

    /// <summary>当前角色剩余额度（null = 未配置/未知）。</summary>
    public decimal? GetRemaining(SubagentRoleDefinition role)
    {
        if (role.BudgetLimit is null) return null;
        return role.BudgetLimit.Value - GetSpent(role.Id);
    }

    /// <summary>所有角色的预算快照（供界面显示）。</summary>
    public IReadOnlyDictionary<string, WorkerBudgetState> Snapshot()
    {
        lock (_gate)
        {
            return _state.ToDictionary(kvp => kvp.Key, kvp => kvp.Value with { });
        }
    }

    private decimal GetSpentLocked(string roleId) =>
        _state.TryGetValue(roleId, out var state) ? state.Spent : 0m;

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var parsed = JsonSerializer.Deserialize<WorkerBudgetFile>(File.ReadAllText(_path), _jsonOptions);
            _state = parsed?.Roles is null
                ? new Dictionary<string, WorkerBudgetState>(StringComparer.OrdinalIgnoreCase)
                : parsed.Roles.ToDictionary(r => r.RoleId, r => r, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // 账本损坏：进入"禁用记账"状态（失败关闭，但只影响预算功能，不拖垮管家）。
            _disabled = true;
            _disableReason = $"工人预算账本损坏或无法解析（{_path}）：{ex.Message}";
        }
    }

    /// <summary>账本是否因损坏而禁用（预算功能失败关闭）。</summary>
    public bool Disabled => _disabled;

    /// <summary>账本禁用原因。</summary>
    public string? DisableReason => _disableReason;

    private void PersistLocked()
    {
        // 跨进程锁：主程序和独立 MCP 进程可能同时记账，防止并发写坏账本。
        using var fileLock = AcquireFileLock();
        // 持久化失败必须上报（失败关闭），否则预算扣减不可靠。
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new WorkerBudgetFile
        {
            SchemaVersion = 1,
            Roles = _state.Values.OrderBy(r => r.RoleId, StringComparer.OrdinalIgnoreCase).ToList()
        }, _jsonOptions), Utf8NoBom);
        File.Move(temp, _path, true);
    }

    private FileStream AcquireFileLock()
    {
        var lockPath = _path + ".lock";
        var directory = System.IO.Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        // 以独占方式打开锁文件；并发写者在此等待。
        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private sealed class WorkerBudgetFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<WorkerBudgetState> Roles { get; set; } = new();
    }
}

public sealed record WorkerBudgetState
{
    public string RoleId { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public decimal BudgetLimit { get; init; }
    public decimal Spent { get; init; }
    public DateTimeOffset? LastDeductedAt { get; init; }
}
