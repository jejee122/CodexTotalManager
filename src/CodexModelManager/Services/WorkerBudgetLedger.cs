using System.Text;
using System.Text.Json;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

/// <summary>
/// Worker budget ledger. Every mutation is one cross-process transaction:
/// lock file -> reload current disk state -> check -> update -> atomic replace.
/// A call reserves its conservative maximum cost before contacting an upstream.
/// An unclosed reservation remains charged after cancellation or a process crash.
/// </summary>
public sealed class WorkerBudgetLedger
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly TimeSpan ReservationTtl = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private Dictionary<string, WorkerBudgetState> _state = new(StringComparer.OrdinalIgnoreCase);
    private List<WorkerBudgetReservationState> _reservations = new();
    private bool _disabled;
    private string? _disableReason;

    public WorkerBudgetLedger(string? path = null)
    {
        var localData = AppSettingsService.ResolveDefaultDataDirectory();
        _path = path ?? Path.Combine(localData, "worker-budget.json");
    }

    public string BudgetFilePath => _path;
    public bool Disabled => _disabled;
    public string? DisableReason => _disableReason;

    public async Task<string?> CheckBeforeCallAsync(
        SubagentRoleDefinition role,
        CancellationToken cancellationToken = default)
    {
        if (_disabled)
            return $"预算账本已禁用（{_disableReason}），已拒绝调用（P0 失败关闭）。";
        if (ValidateRole(role) is { } validation) return validation;

        var spent = await GetSpentAsync(role.Id, cancellationToken).ConfigureAwait(false);
        if (spent >= role.BudgetLimit!.Value)
            return $"角色 {role.Id} 预算已耗尽（已用或已预留 {spent:F4} {role.Currency} / 上限 {role.BudgetLimit:F2} {role.Currency}），已拒绝调用。";
        return null;
    }

    public async Task<WorkerBudgetReservation> ReserveAsync(
        SubagentRoleDefinition role,
        long maximumBillableTokens,
        CancellationToken cancellationToken = default)
    {
        if (ValidateRole(role) is { } validation)
            throw new InvalidOperationException(validation);
        if (maximumBillableTokens <= 0)
            throw new InvalidOperationException("调用前无法确定最大计费 Token，已失败关闭。");

        var maximumCost = CostFor(maximumBillableTokens, role.PricePerMillionTokens!.Value);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var fileLock = await AcquireFileLockAsync(cancellationToken);
            ReloadFromDiskLocked();
            ThrowIfDisabled();
            ReclaimExpiredReservationsLocked(DateTimeOffset.UtcNow, persist: true);
            var current = GetEffectiveSpentLocked(role.Id);
            if (current + maximumCost > role.BudgetLimit!.Value)
                throw new InvalidOperationException(
                    $"角色 {role.Id} 剩余预算不足以覆盖本次最坏成本（已用或已预留 {current:F4}，本次最多 {maximumCost:F4}，上限 {role.BudgetLimit:F4} {role.Currency}）。");

            var reservation = new WorkerBudgetReservation(
                Guid.NewGuid().ToString("N"),
                role.Id,
                role.Currency!,
                maximumCost,
                DateTimeOffset.UtcNow);
            _reservations.Add(new WorkerBudgetReservationState
            {
                Id = reservation.Id,
                RoleId = reservation.RoleId,
                Currency = reservation.Currency,
                ReservedCost = reservation.ReservedCost,
                CreatedAt = reservation.CreatedAt
            });
            PersistLocked();
            return reservation;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<decimal> SettleAsync(
        WorkerBudgetReservation reservation,
        SubagentRoleDefinition role,
        int? promptTokens,
        int? completionTokens,
        CancellationToken cancellationToken = default)
    {
        ValidateUsage(role, promptTokens, completionTokens);
        var totalTokens = checked((long)(promptTokens ?? 0) + (completionTokens ?? 0));
        var actualCost = CostFor(totalTokens, role.PricePerMillionTokens!.Value);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var fileLock = await AcquireFileLockAsync(cancellationToken);
            ReloadFromDiskLocked();
            ThrowIfDisabled();
            ReclaimExpiredReservationsLocked(DateTimeOffset.UtcNow, persist: true);
            var pending = _reservations.FirstOrDefault(item =>
                item.Id.Equals(reservation.Id, StringComparison.Ordinal)
                && item.RoleId.Equals(role.Id, StringComparison.OrdinalIgnoreCase));
            if (pending is null)
                throw new InvalidOperationException("预算预留不存在或已经结算，已拒绝重复记账。");

            var current = GetCommittedSpentLocked(role.Id);
            _state[role.Id] = new WorkerBudgetState
            {
                RoleId = role.Id,
                Currency = role.Currency!,
                BudgetLimit = role.BudgetLimit!.Value,
                Spent = current + actualCost,
                LastDeductedAt = DateTimeOffset.UtcNow
            };
            _reservations.Remove(pending);
            PersistLocked();
            if (actualCost > pending.ReservedCost
                || GetEffectiveSpentLocked(role.Id) > role.BudgetLimit.Value)
                throw new InvalidOperationException(
                    "上游报告的实际成本超过调用前预留，账本已如实记录并锁住后续调用。");
            return actualCost;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Compatibility entry point for already-incurred usage. New calls must use
    /// ReserveAsync/SettleAsync so the budget is protected before network I/O.
    /// </summary>
    public async Task<decimal> DeductAsync(
        SubagentRoleDefinition role,
        int? promptTokens,
        int? completionTokens,
        CancellationToken cancellationToken = default)
    {
        ValidateUsage(role, promptTokens, completionTokens);
        var totalTokens = checked((long)(promptTokens ?? 0) + (completionTokens ?? 0));
        var cost = CostFor(totalTokens, role.PricePerMillionTokens!.Value);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var fileLock = await AcquireFileLockAsync(cancellationToken);
            ReloadFromDiskLocked();
            ThrowIfDisabled();
            var current = GetCommittedSpentLocked(role.Id);
            _state[role.Id] = new WorkerBudgetState
            {
                RoleId = role.Id,
                Currency = role.Currency!,
                BudgetLimit = role.BudgetLimit!.Value,
                Spent = current + cost,
                LastDeductedAt = DateTimeOffset.UtcNow
            };
            PersistLocked();
            return cost;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<decimal> GetSpentAsync(
        string roleId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            ReloadFromDiskLocked();
            ThrowIfDisabled();
            ReclaimExpiredReservationsLocked(DateTimeOffset.UtcNow, persist: true);
            return GetEffectiveSpentLocked(roleId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<decimal?> GetRemainingAsync(
        SubagentRoleDefinition role,
        CancellationToken cancellationToken = default) =>
        role.BudgetLimit is null
            ? null
            : role.BudgetLimit.Value - await GetSpentAsync(role.Id, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<string, WorkerBudgetState>> SnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            ReloadFromDiskLocked();
            ThrowIfDisabled();
            ReclaimExpiredReservationsLocked(DateTimeOffset.UtcNow, persist: true);
            var roleIds = _state.Keys.Concat(_reservations.Select(item => item.RoleId))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return roleIds.ToDictionary(
                roleId => roleId,
                roleId => _state.TryGetValue(roleId, out var state)
                    ? state with { Spent = GetEffectiveSpentLocked(roleId) }
                    : new WorkerBudgetState { RoleId = roleId, Spent = GetEffectiveSpentLocked(roleId) },
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkerBudgetReservation>> PendingReservationsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            ReloadFromDiskLocked();
            ThrowIfDisabled();
            ReclaimExpiredReservationsLocked(DateTimeOffset.UtcNow, persist: true);
            return _reservations
                .OrderBy(item => item.CreatedAt)
                .Select(item => new WorkerBudgetReservation(
                    item.Id, item.RoleId, item.Currency, item.ReservedCost, item.CreatedAt))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseReservationAsync(
        string reservationId,
        string roleId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reservationId) || string.IsNullOrWhiteSpace(roleId))
            throw new ArgumentException("预留编号和角色不能为空。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var fileLock = await AcquireFileLockAsync(cancellationToken);
            ReloadFromDiskLocked();
            ThrowIfDisabled();
            var removed = _reservations.RemoveAll(item =>
                item.Id.Equals(reservationId, StringComparison.Ordinal)
                && item.RoleId.Equals(roleId, StringComparison.OrdinalIgnoreCase));
            if (removed != 1)
                throw new InvalidOperationException("预算预留不存在、已结算或角色不匹配，未做修改。");
            PersistLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? ValidateRole(SubagentRoleDefinition role)
    {
        if (role.PricePerMillionTokens is null or < 0)
            return $"角色 {role.Id} 未配置价格，已拒绝调用（P0 失败关闭）。";
        if (string.IsNullOrWhiteSpace(role.Currency))
            return $"角色 {role.Id} 未配置币种，已拒绝调用（P0 失败关闭）。";
        if (role.BudgetLimit is null or < 0)
            return $"角色 {role.Id} 未配置预算上限，已拒绝调用（P0 失败关闭）。";
        return null;
    }

    private static void ValidateUsage(
        SubagentRoleDefinition role,
        int? promptTokens,
        int? completionTokens)
    {
        if (ValidateRole(role) is { } validation)
            throw new InvalidOperationException(validation);
        if (promptTokens is < 0 || completionTokens is < 0)
            throw new InvalidOperationException(
                $"角色 {role.Id} 返回了负数 token 用量（prompt={promptTokens}, completion={completionTokens}），已拒绝记账。");
        if (promptTokens is null && completionTokens is null)
            throw new InvalidOperationException(
                $"角色 {role.Id} 未返回 token 用量；调用前预留将保持占用，防止取消变成免费调用。");
    }

    private static decimal CostFor(long tokens, decimal pricePerMillion) =>
        (decimal)tokens / 1_000_000m * pricePerMillion;

    private decimal GetCommittedSpentLocked(string roleId) =>
        _state.TryGetValue(roleId, out var state) ? state.Spent : 0m;

    private decimal GetEffectiveSpentLocked(string roleId) =>
        GetCommittedSpentLocked(roleId) + _reservations
            .Where(item => item.RoleId.Equals(roleId, StringComparison.OrdinalIgnoreCase))
            .Sum(item => item.ReservedCost);

    private void ReloadFromDiskLocked()
    {
        if (!File.Exists(_path))
        {
            _state = new Dictionary<string, WorkerBudgetState>(StringComparer.OrdinalIgnoreCase);
            _reservations = new List<WorkerBudgetReservationState>();
            return;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<WorkerBudgetFile>(File.ReadAllText(_path), _jsonOptions)
                         ?? throw new InvalidDataException("预算文件为空。");
            _state = parsed.Roles.ToDictionary(r => r.RoleId, r => r, StringComparer.OrdinalIgnoreCase);
            _reservations = parsed.PendingReservations ?? new List<WorkerBudgetReservationState>();
        }
        catch (Exception ex)
        {
            Disable(ex);
            throw new InvalidOperationException(_disableReason, ex);
        }
    }

    private int ReclaimExpiredReservationsLocked(DateTimeOffset now, bool persist)
    {
        var cutoff = now - ReservationTtl;
        var removed = _reservations.RemoveAll(item => item.CreatedAt <= cutoff);
        if (removed > 0 && persist) PersistLocked();
        return removed;
    }

    private void Disable(Exception ex)
    {
        _disabled = true;
        _disableReason = $"工人预算账本损坏或无法解析（{_path}）：{ex.Message}";
    }

    private void ThrowIfDisabled()
    {
        if (_disabled) throw new InvalidOperationException(_disableReason);
    }

    private void PersistLocked()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temp = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new WorkerBudgetFile
            {
                SchemaVersion = 2,
                Roles = _state.Values.OrderBy(r => r.RoleId, StringComparer.OrdinalIgnoreCase).ToList(),
                PendingReservations = _reservations.OrderBy(r => r.CreatedAt).ToList()
            }, _jsonOptions), Utf8NoBom);
            File.Move(temp, _path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return OpenLockFile(); }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private FileStream OpenLockFile()
    {
        var lockPath = _path + ".lock";
        var directory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private sealed class WorkerBudgetFile
    {
        public int SchemaVersion { get; set; } = 2;
        public List<WorkerBudgetState> Roles { get; set; } = new();
        public List<WorkerBudgetReservationState>? PendingReservations { get; set; } = new();
    }

    private sealed class WorkerBudgetReservationState
    {
        public string Id { get; set; } = string.Empty;
        public string RoleId { get; set; } = string.Empty;
        public string Currency { get; set; } = string.Empty;
        public decimal ReservedCost { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}

public sealed record WorkerBudgetReservation(
    string Id,
    string RoleId,
    string Currency,
    decimal ReservedCost,
    DateTimeOffset CreatedAt);

public sealed record WorkerBudgetState
{
    public string RoleId { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public decimal BudgetLimit { get; init; }
    public decimal Spent { get; init; }
    public DateTimeOffset? LastDeductedAt { get; init; }
}
