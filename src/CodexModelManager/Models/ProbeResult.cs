namespace CodexModelManager.Models;

public sealed record ProbeResult(string BaseUrl, IReadOnlyList<string> Models, long LatencyMs);

public sealed record OperationResult(bool Success, string Message)
{
    public static OperationResult Ok(string message) => new(true, message);
    public static OperationResult Fail(string message) => new(false, message);
}

public sealed record ActiveRoute(
    string Provider,
    string Model,
    IReadOnlyList<(string Provider, string Model)> Targets);

public sealed record PoolRouteSnapshot(
    string Id,
    string Alias,
    IReadOnlyList<(string Provider, string Model)> Targets);

public sealed record RecentRouteResult(
    bool HasData,
    string RequestedModel,
    string ActualProvider,
    string RawActualProvider,
    string ActualModel,
    int? Status,
    long? DurationMs,
    DateTimeOffset? Timestamp,
    string Message);

public sealed record OpenCodexRuntimeStatus(
    bool Healthy,
    int? ProcessId,
    int Port,
    TimeSpan? Uptime,
    long? WorkingSetBytes,
    string LastError);
