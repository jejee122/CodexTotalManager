using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace CodexModelManager.Services;

public enum ManagedTomlEditStatus
{
    CleanNoManagedBlock,
    CleanManagedBlock,
    ReadyToAppend,
    ReadyToReplace,
    ReadyToRemove,
    AlreadyDesired,
    AlreadyAbsent,
    ConflictDuplicateMarkers,
    ConflictIncompleteMarkers,
    ConflictMarkerOrder,
    ConflictUnmanagedTargetTable,
    ConflictTargetTableOutsideManagedBlock,
    ConflictInvalidManagedBlock,
    ConflictInvalidExpectedHash,
    ConflictManagedBlockMissing,
    ConflictManagedBlockModified,
    InvalidDesiredContent
}

public sealed record ManagedTomlBlockInspection(
    ManagedTomlEditStatus Status,
    bool Conflict,
    string StatusText,
    string NewLine,
    bool HasManagedBlock,
    int ManagedBlockStart,
    int ManagedBlockLength,
    string? CurrentManagedBlock,
    string? CurrentManagedBlockSha256)
{
    public bool IsSafe => !Conflict;
}

public sealed record ManagedTomlBlockEditResult(
    ManagedTomlBlockInspection Inspection,
    ManagedTomlEditStatus Status,
    bool Conflict,
    bool Changed,
    string? CandidateText,
    string? CandidateManagedBlockSha256,
    string StatusText)
{
    public bool CanWrite => !Conflict && CandidateText is not null;
}

public sealed record CodexTomlSafetyInspection(
    bool UnsafeCustomProvider,
    bool AgentsExplicitlyDisabled,
    bool SyntaxValid);

/// <summary>
/// Makes narrowly scoped, optimistic edits to the Total Manager-owned MCP table
/// in a Codex TOML configuration. This type performs no file-system operations.
/// The caller owns byte decoding/encoding and BOM preservation.
/// </summary>
