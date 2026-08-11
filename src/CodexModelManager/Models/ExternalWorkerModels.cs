namespace CodexModelManager.Models;

public sealed record ExternalWorkerInvocation(
    string RoleId,
    string Task,
    string? Context = null,
    int MaxOutputTokens = 1024);

public sealed record ExternalWorkerRoleOption(
    string RoleId,
    string DisplayName,
    string Purpose,
    string ConfiguredModel,
    string SourceId = "");

public sealed record ExternalWorkerTokenUsage(
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens);

public sealed record ExternalWorkerCompletion(
    string RequestId,
    string RoleId,
    string ConfiguredModel,
    string ResolvedModel,
    string AccountSource,
    string Content,
    string? FinishReason,
    ExternalWorkerTokenUsage Usage,
    int HttpStatusCode,
    long ElapsedMilliseconds);

public sealed record ExternalWorkerBackendRequest(
    string Model,
    string SourceId,
    string ExpectedSourceFingerprint,
    string RoleId,
    string RoleInstructions,
    string Task,
    string? Context,
    int MaxOutputTokens);

public sealed record ExternalWorkerBackendResponse(
    string Content,
    string? FinishReason,
    ExternalWorkerTokenUsage Usage,
    int HttpStatusCode,
    string ResolvedModel);

public sealed record ExternalWorkerAuditEntry(
    DateTimeOffset Timestamp,
    string Event,
    string RequestId,
    string RoleId,
    string ConfiguredModel,
    string? ResolvedModel,
    string AccountSource,
    string Status,
    int? HttpStatusCode,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    long? ElapsedMilliseconds,
    string? ErrorCode);

public sealed record ExternalWorkerRuntimeState(
    DateTimeOffset? LastHandshakeAt,
    string? LastHandshakeClient,
    string? LastHandshakeClientVersion,
    DateTimeOffset? LastCallAt,
    bool? LastCallSucceeded,
    string? LastRoleId,
    string? LastRequestedModel,
    string? LastResolvedModel,
    int? LastHttpStatus,
    long? InputTokens,
    long? OutputTokens,
    string? LastError,
    string? LastAccountSource = null);

public sealed class ExternalWorkerException : Exception
{
    public ExternalWorkerException(string code, string safeMessage, int? httpStatusCode = null, Exception? innerException = null)
        : base(safeMessage, innerException)
    {
        Code = code;
        SafeMessage = safeMessage;
        HttpStatusCode = httpStatusCode;
    }

    public string Code { get; }
    public string SafeMessage { get; }
    public int? HttpStatusCode { get; }
}
