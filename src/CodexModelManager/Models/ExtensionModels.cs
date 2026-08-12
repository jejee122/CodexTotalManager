using System.Text.Json.Serialization;

namespace CodexModelManager.Models;

public sealed class ExtensionManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("publisher")]
    public string Publisher { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("entry")]
    public string Entry { get; init; } = string.Empty;

    [JsonPropertyName("entrySha256")]
    public string? EntrySha256 { get; init; }

    [JsonPropertyName("arguments")]
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
}

public sealed record ExtensionPackage(
    ExtensionManifest Manifest,
    string PackageDirectory,
    string EntryPath,
    string EntrySha256,
    string Fingerprint,
    bool Enabled,
    bool TrustInvalidated);

public sealed record ExtensionDiscoveryIssue(string FolderName, string Message);

public sealed record ExtensionDiscoveryResult(
    IReadOnlyList<ExtensionPackage> Packages,
    IReadOnlyList<ExtensionDiscoveryIssue> Issues,
    string? TrustStoreWarning);

public sealed record ExtensionExecutionResult(
    string ExtensionId,
    bool Success,
    int ExitCode,
    string Message);
