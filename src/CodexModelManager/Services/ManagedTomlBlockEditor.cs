using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace CodexModelManager.Services;

public static class ManagedTomlBlockEditor
{
    public const string BeginMarker = "# BEGIN CODEX TOTAL MANAGER: codex_total_manager_external v1";
    public const string EndMarker = "# END CODEX TOTAL MANAGER: codex_total_manager_external";
    public const string TargetTableHeader = "[mcp_servers.codex_total_manager_external]";

    private const string McpServersKey = @"(?:mcp_servers|""mcp_servers""|'mcp_servers')";
    private const string WorkerKey = @"(?:codex_total_manager_external|""codex_total_manager_external""|'codex_total_manager_external')";
    private const string WorkerPath = McpServersKey + @"\s*\.\s*" + WorkerKey;

    private static readonly Regex TargetRootTableLine = new(
        @"^\s*(?:\[\s*" + WorkerPath + @"\s*\]|\[\[\s*" + WorkerPath + @"\s*\]\])\s*(?:#.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TargetTableFamilyLine = new(
        @"^\s*(?:\[\s*|\[\[\s*)" + WorkerPath + @"\s*(?:\.|\]\]?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static CodexTomlSafetyInspection InspectCodexSafetySettings(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        if (!TryParseTomlDocument(sourceText, out var document))
        {
            return new CodexTomlSafetyInspection(false, false, false);
        }

        var unsafeCustomProvider = document.TryGetValue("model_provider", out var providerValue)
                                   && (providerValue is not string provider
                                       || provider.Equals("custom", StringComparison.OrdinalIgnoreCase));
        var agentsExplicitlyDisabled = false;
        if (document.TryGetValue("agents", out var agentsValue))
        {
            if (agentsValue is not TomlTable agentsTable)
            {
                agentsExplicitlyDisabled = true;
            }
            else if (agentsTable.TryGetValue("enabled", out var enabledValue))
            {
                agentsExplicitlyDisabled = enabledValue is not bool enabled || !enabled;
            }
        }

        return new CodexTomlSafetyInspection(
            unsafeCustomProvider,
            agentsExplicitlyDisabled,
            true);
    }

    private static bool TryParseTomlDocument(string sourceText, out TomlTable document)
    {
        if (TryParseTomlVariant(sourceText, out document)) return true;

        // TOML 1.0 permits horizontal whitespace after a multiline-basic-string
        // continuation backslash. Tomlyn 0.20.0 rejects that valid form, so retry
        // a validation-only copy after a context-aware removal of that whitespace.
        var normalized = NormalizeMultilineBasicContinuationWhitespace(sourceText);
        return !normalized.Equals(sourceText, StringComparison.Ordinal)
               && TryParseTomlVariant(normalized, out document);
    }

    private static bool TryParseTomlVariant(string sourceText, out TomlTable document)
    {
        try
        {
            var syntax = Toml.Validate(Toml.Parse(sourceText));
            if (syntax.HasErrors)
            {
                document = new TomlTable();
                return false;
            }
            document = Toml.ToModel(syntax);
            return true;
        }
        catch (TomlException)
        {
            document = new TomlTable();
            return false;
        }
    }

    private static string NormalizeMultilineBasicContinuationWhitespace(string sourceText)
    {
        var output = new StringBuilder(sourceText.Length);
        var kind = SafetyStringKind.None;
        var changed = false;

        for (var index = 0; index < sourceText.Length;)
        {
            var character = sourceText[index];
            switch (kind)
            {
                case SafetyStringKind.None:
                    if (character == '#')
                    {
                        var commentEnd = index;
                        while (commentEnd < sourceText.Length
                               && sourceText[commentEnd] is not ('\r' or '\n')) commentEnd++;
                        output.Append(sourceText.AsSpan(index, commentEnd - index));
                        index = commentEnd;
                        continue;
                    }
                    if (character is '"' or '\'')
                    {
                        var quoteRun = CountConsecutive(sourceText, index, character);
                        if (quoteRun >= 3)
                        {
                            output.Append(sourceText.AsSpan(index, 3));
                            kind = character == '"'
                                ? SafetyStringKind.MultilineBasic
                                : SafetyStringKind.MultilineLiteral;
                            index += 3;
                        }
                        else
                        {
                            output.Append(character);
                            kind = character == '"' ? SafetyStringKind.Basic : SafetyStringKind.Literal;
                            index++;
                        }
                        continue;
                    }
                    output.Append(character);
                    index++;
                    continue;

                case SafetyStringKind.Basic:
                    if (character is '\r' or '\n') return sourceText;
                    output.Append(character);
                    index++;
                    if (character == '"') kind = SafetyStringKind.None;
                    else if (character == '\\')
                    {
                        if (index >= sourceText.Length || sourceText[index] is '\r' or '\n') return sourceText;
                        output.Append(sourceText[index++]);
                    }
                    continue;

                case SafetyStringKind.Literal:
                    if (character is '\r' or '\n') return sourceText;
                    output.Append(character);
                    index++;
                    if (character == '\'') kind = SafetyStringKind.None;
                    continue;

                case SafetyStringKind.MultilineLiteral:
                    if (character == '\'')
                    {
                        var quoteRun = CountConsecutive(sourceText, index, '\'');
                        if (quoteRun >= 3)
                        {
                            if (quoteRun > 5) return sourceText;
                            kind = SafetyStringKind.None;
                        }
                        output.Append(sourceText.AsSpan(index, quoteRun));
                        index += quoteRun;
                    }
                    else
                    {
                        output.Append(character);
                        index++;
                    }
                    continue;

                case SafetyStringKind.MultilineBasic:
                    if (character == '"')
                    {
                        var quoteRun = CountConsecutive(sourceText, index, '"');
                        if (quoteRun >= 3)
                        {
                            if (quoteRun > 5) return sourceText;
                            kind = SafetyStringKind.None;
                        }
                        output.Append(sourceText.AsSpan(index, quoteRun));
                        index += quoteRun;
                        continue;
                    }
                    if (character == '\\')
                    {
                        var cursor = index + 1;
                        while (cursor < sourceText.Length && sourceText[cursor] is ' ' or '\t') cursor++;
                        if (cursor > index + 1
                            && cursor < sourceText.Length
                            && sourceText[cursor] is '\r' or '\n')
                        {
                            output.Append('\\');
                            changed = true;
                            index = cursor;
                            continue;
                        }

                        output.Append(character);
                        index++;
                        if (index < sourceText.Length && sourceText[index] is not ('\r' or '\n'))
                            output.Append(sourceText[index++]);
                        continue;
                    }
                    output.Append(character);
                    index++;
                    continue;
            }
        }

        return changed ? output.ToString() : sourceText;
    }

    private static int CountConsecutive(string text, int start, char character)
    {
        var cursor = start;
        while (cursor < text.Length && text[cursor] == character) cursor++;
        return cursor - start;
    }

    public static ManagedTomlBlockInspection Inspect(
        string sourceText,
        string? expectedManagedBlockSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(sourceText);

        var newLine = DetectNewLine(sourceText);
        var lines = ReadLines(sourceText);
        var beginLines = lines.Where(line =>
            line.IsTopLevelSyntax && line.Text.Trim().Equals(BeginMarker, StringComparison.Ordinal)).ToArray();
        var endLines = lines.Where(line =>
            line.IsTopLevelSyntax && line.Text.Trim().Equals(EndMarker, StringComparison.Ordinal)).ToArray();
        var targetRootLines = lines.Where(line =>
            line.IsTopLevelSyntax && TargetRootTableLine.IsMatch(line.Text)).ToArray();
        var targetFamilyLines = lines.Where(line =>
            line.IsTopLevelSyntax && TargetTableFamilyLine.IsMatch(line.Text)).ToArray();
        var targetKeyAssignmentLines = FindTargetKeyAssignments(sourceText, lines).ToArray();

        if (!TryNormalizeExpectedHash(expectedManagedBlockSha256, out var expectedHash))
        {
            return Conflict(
                ManagedTomlEditStatus.ConflictInvalidExpectedHash,
                "The expected managed-block SHA-256 must contain exactly 64 hexadecimal characters.",
                newLine);
        }

        if (beginLines.Length > 1 || endLines.Length > 1)
        {
            return Conflict(
                ManagedTomlEditStatus.ConflictDuplicateMarkers,
                "Duplicate Total Manager MCP BEGIN or END markers were found; automatic editing is refused.",
                newLine);
        }

        if (beginLines.Length != endLines.Length)
        {
            return Conflict(
                ManagedTomlEditStatus.ConflictIncompleteMarkers,
                "The Total Manager MCP marker pair is incomplete; automatic editing is refused.",
                newLine);
        }

        if (beginLines.Length == 0)
        {
            if (targetFamilyLines.Length > 0 || targetKeyAssignmentLines.Length > 0)
            {
                return Conflict(
                    ManagedTomlEditStatus.ConflictUnmanagedTargetTable,
                    $"{TargetTableHeader}, an equivalent table/key assignment, or one of its child definitions already exists outside a Total Manager managed block.",
                    newLine);
            }

            if (expectedHash is not null)
            {
                return Conflict(
                    ManagedTomlEditStatus.ConflictManagedBlockMissing,
                    "A managed block was expected but is missing; it may have been removed manually.",
                    newLine);
            }

            return new ManagedTomlBlockInspection(
                ManagedTomlEditStatus.CleanNoManagedBlock,
                false,
                "No managed MCP block or conflicting target table is present.",
                newLine,
                false,
                -1,
                0,
                null,
                null);
        }

        var begin = beginLines[0];
        var end = endLines[0];
        if (begin.Start >= end.Start)
        {
            return Conflict(
                ManagedTomlEditStatus.ConflictMarkerOrder,
                "The Total Manager MCP END marker occurs before its BEGIN marker.",
                newLine);
        }

        var blockStart = begin.Start;
        var blockEnd = end.ContentEnd;
        var rootTablesInside = targetRootLines.Count(line => line.Start > begin.Start && line.Start < end.Start);
        var familyTablesInside = targetFamilyLines.Count(line => line.Start > begin.Start && line.Start < end.Start);
        var familyTablesOutside = targetFamilyLines.Length - familyTablesInside;
        var keyAssignmentsInside = targetKeyAssignmentLines.Count(line => line.Start > begin.Start && line.Start < end.Start);
        var keyAssignmentsOutside = targetKeyAssignmentLines.Length - keyAssignmentsInside;

        if (familyTablesOutside > 0 || keyAssignmentsOutside > 0)
        {
            return Conflict(
                ManagedTomlEditStatus.ConflictTargetTableOutsideManagedBlock,
                $"{TargetTableHeader} or an equivalent key assignment also exists outside the managed marker pair.",
                newLine,
                true,
                blockStart,
                blockEnd - blockStart,
                sourceText.Substring(blockStart, blockEnd - blockStart));
        }

        if (rootTablesInside != 1)
        {
            return Conflict(
                ManagedTomlEditStatus.ConflictInvalidManagedBlock,
                rootTablesInside == 0
                    ? $"The managed marker pair does not contain {TargetTableHeader}."
                    : $"The managed marker pair contains {rootTablesInside} copies of {TargetTableHeader}.",
                newLine,
                true,
                blockStart,
                blockEnd - blockStart,
                sourceText.Substring(blockStart, blockEnd - blockStart));
        }

        var currentBlock = sourceText.Substring(blockStart, blockEnd - blockStart);
        var currentHash = ComputeManagedBlockSha256(currentBlock);
        if (expectedHash is not null && !currentHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return new ManagedTomlBlockInspection(
                ManagedTomlEditStatus.ConflictManagedBlockModified,
                true,
                "The managed MCP block no longer matches its expected SHA-256; it may have been edited manually.",
                newLine,
                true,
                blockStart,
                blockEnd - blockStart,
                currentBlock,
                currentHash);
        }

        return new ManagedTomlBlockInspection(
            ManagedTomlEditStatus.CleanManagedBlock,
            false,
            "The managed MCP block is structurally valid and ownership checks passed.",
            newLine,
            true,
            blockStart,
            blockEnd - blockStart,
            currentBlock,
            currentHash);
    }

    public static ManagedTomlBlockEditResult Upsert(
        string sourceText,
        string managedTomlBody,
        string? expectedManagedBlockSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(managedTomlBody);

        var inspection = Inspect(sourceText, expectedManagedBlockSha256);
        if (inspection.Conflict)
        {
            return FromConflict(inspection);
        }

        if (!TryBuildManagedBlock(managedTomlBody, inspection.NewLine, out var desiredBlock, out var error))
        {
            return new ManagedTomlBlockEditResult(
                inspection,
                ManagedTomlEditStatus.InvalidDesiredContent,
                true,
                false,
                null,
                null,
                error);
        }

        var desiredHash = ComputeManagedBlockSha256(desiredBlock);
        if (inspection.HasManagedBlock)
        {
            if (inspection.CurrentManagedBlock!.Equals(desiredBlock, StringComparison.Ordinal))
            {
                return new ManagedTomlBlockEditResult(
                    inspection,
                    ManagedTomlEditStatus.AlreadyDesired,
                    false,
                    false,
                    sourceText,
                    desiredHash,
                    "The managed MCP block already has the requested content.");
            }

            var candidate = string.Concat(
                sourceText.AsSpan(0, inspection.ManagedBlockStart),
                desiredBlock,
                sourceText.AsSpan(inspection.ManagedBlockStart + inspection.ManagedBlockLength));
            return new ManagedTomlBlockEditResult(
                inspection,
                ManagedTomlEditStatus.ReadyToReplace,
                false,
                true,
                candidate,
                desiredHash,
                "A candidate that replaces only the managed MCP block is ready.");
        }

        var separator = sourceText.Length == 0 || EndsWithLineBreak(sourceText)
            ? string.Empty
            : inspection.NewLine;
        var appended = string.Concat(sourceText, separator, desiredBlock);
        return new ManagedTomlBlockEditResult(
            inspection,
            ManagedTomlEditStatus.ReadyToAppend,
            false,
            true,
            appended,
            desiredHash,
            "A candidate that appends the first managed MCP block is ready.");
    }

    public static ManagedTomlBlockEditResult Remove(
        string sourceText,
        string? expectedManagedBlockSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(sourceText);

        var inspection = Inspect(sourceText, expectedManagedBlockSha256);
        if (inspection.Conflict)
        {
            return FromConflict(inspection);
        }

        if (!inspection.HasManagedBlock)
        {
            return new ManagedTomlBlockEditResult(
                inspection,
                ManagedTomlEditStatus.AlreadyAbsent,
                false,
                false,
                sourceText,
                null,
                "No managed MCP block is present.");
        }

        var candidate = string.Concat(
            sourceText.AsSpan(0, inspection.ManagedBlockStart),
            sourceText.AsSpan(inspection.ManagedBlockStart + inspection.ManagedBlockLength));
        return new ManagedTomlBlockEditResult(
            inspection,
            ManagedTomlEditStatus.ReadyToRemove,
            false,
            true,
            candidate,
            null,
            "A candidate that removes only the managed MCP block is ready.");
    }

    public static string ComputeManagedBlockSha256(string managedBlock)
    {
        ArgumentNullException.ThrowIfNull(managedBlock);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(managedBlock)));
    }

    private static bool TryBuildManagedBlock(
        string body,
        string newLine,
        out string managedBlock,
        out string error)
    {
        managedBlock = string.Empty;
        error = string.Empty;

        var normalizedBody = NormalizeNewLines(body, newLine).Trim('\r', '\n');
        if (string.IsNullOrWhiteSpace(normalizedBody))
        {
            error = "The desired managed TOML body is empty.";
            return false;
        }

        var lines = ReadLines(normalizedBody);
        if (lines.Any(line =>
                line.Text.Trim().Equals(BeginMarker, StringComparison.Ordinal) ||
                line.Text.Trim().Equals(EndMarker, StringComparison.Ordinal)))
        {
            error = "The desired TOML body must not contain managed-block markers.";
            return false;
        }

        var targetTableCount = lines.Count(line =>
            line.IsTopLevelSyntax && TargetRootTableLine.IsMatch(line.Text));
        if (targetTableCount != 1)
        {
            error = targetTableCount == 0
                ? $"The desired TOML body must contain {TargetTableHeader}."
                : $"The desired TOML body contains {targetTableCount} copies of {TargetTableHeader}.";
            return false;
        }

        managedBlock = string.Concat(
            BeginMarker,
            newLine,
            normalizedBody,
            newLine,
            EndMarker);
        return true;
    }

    private static ManagedTomlBlockEditResult FromConflict(ManagedTomlBlockInspection inspection) =>
        new(
            inspection,
            inspection.Status,
            true,
            false,
            null,
            inspection.CurrentManagedBlockSha256,
            inspection.StatusText);

    private static ManagedTomlBlockInspection Conflict(
        ManagedTomlEditStatus status,
        string statusText,
        string newLine,
        bool hasManagedBlock = false,
        int managedBlockStart = -1,
        int managedBlockLength = 0,
        string? currentManagedBlock = null) =>
        new(
            status,
            true,
            statusText,
            newLine,
            hasManagedBlock,
            managedBlockStart,
            managedBlockLength,
            currentManagedBlock,
            currentManagedBlock is null ? null : ComputeManagedBlockSha256(currentManagedBlock));

    private static bool TryNormalizeExpectedHash(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)) return true;

        var trimmed = value.Trim();
        if (trimmed.Length != 64 || trimmed.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        normalized = trimmed.ToUpperInvariant();
        return true;
    }

    private static string DetectNewLine(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                return index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
            }

            if (text[index] == '\n') return "\n";
        }

        return Environment.NewLine;
    }

    private static bool EndsWithLineBreak(string text) =>
        text.Length > 0 && (text[^1] == '\r' || text[^1] == '\n');

    private static string NormalizeNewLines(string text, string newLine)
    {
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                builder.Append(newLine);
            }
            else if (character == '\n')
            {
                builder.Append(newLine);
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<TextLine> FindTargetKeyAssignments(
        string sourceText,
        IReadOnlyList<TextLine> lines)
    {
        var matches = new List<TextLine>();
        IReadOnlyList<string> currentTablePath = Array.Empty<string>();

        foreach (var line in lines)
        {
            if (!line.IsTopLevelSyntax) continue;

            if (TryParseTableHeaderPath(line.Text, out var tablePath))
            {
                currentTablePath = tablePath;
                continue;
            }

            if (!TryParseKeyAssignment(line.Text, out var keyPath, out var valueStart)) continue;

            var fullPath = currentTablePath.Concat(keyPath).ToArray();
            var currentTableOwnsTarget = PathStartsWithTarget(currentTablePath);
            if (!currentTableOwnsTarget && PathStartsWithTarget(fullPath))
            {
                matches.Add(line);
                continue;
            }

            if (PathEqualsMcpServers(fullPath) &&
                InlineTableDefinesTargetWorker(sourceText, line.Start + valueStart))
            {
                matches.Add(line);
            }
        }

        return matches;
    }

    private static bool TryParseTableHeaderPath(string line, out IReadOnlyList<string> path)
    {
        path = Array.Empty<string>();
        var cursor = 0;
        SkipHorizontalWhitespace(line, ref cursor);
        if (cursor >= line.Length || line[cursor] != '[') return false;

        cursor++;
        var arrayTable = cursor < line.Length && line[cursor] == '[';
        if (arrayTable) cursor++;

        SkipHorizontalWhitespace(line, ref cursor);
        if (!TryParseDottedKey(line, ref cursor, out path)) return false;
        SkipHorizontalWhitespace(line, ref cursor);

        if (cursor >= line.Length || line[cursor] != ']') return false;
        cursor++;
        if (arrayTable)
        {
            if (cursor >= line.Length || line[cursor] != ']') return false;
            cursor++;
        }

        SkipHorizontalWhitespace(line, ref cursor);
        return cursor == line.Length || line[cursor] == '#';
    }

    private static bool TryParseKeyAssignment(
        string line,
        out IReadOnlyList<string> path,
        out int valueStart)
    {
        path = Array.Empty<string>();
        valueStart = -1;
        var cursor = 0;
        SkipHorizontalWhitespace(line, ref cursor);
        if (cursor >= line.Length || line[cursor] is '#' or '[') return false;
        if (!TryParseDottedKey(line, ref cursor, out path)) return false;
        SkipHorizontalWhitespace(line, ref cursor);
        if (cursor >= line.Length || line[cursor] != '=') return false;

        valueStart = cursor + 1;
        return true;
    }

    private static bool TryParseDottedKey(
        string text,
        ref int cursor,
        out IReadOnlyList<string> path)
    {
        var segments = new List<string>();
        while (true)
        {
            SkipHorizontalWhitespace(text, ref cursor);
            if (!TryParseKeySegment(text, ref cursor, out var segment))
            {
                path = Array.Empty<string>();
                return false;
            }

            segments.Add(segment);
            SkipHorizontalWhitespace(text, ref cursor);
            if (cursor >= text.Length || text[cursor] != '.')
            {
                path = segments;
                return true;
            }

            cursor++;
        }
    }

    private static bool TryParseKeySegment(string text, ref int cursor, out string segment)
    {
        segment = string.Empty;
        if (cursor >= text.Length) return false;

        if (text[cursor] == '\'')
        {
            var start = ++cursor;
            while (cursor < text.Length && text[cursor] != '\'') cursor++;
            if (cursor >= text.Length) return false;
            segment = text[start..cursor];
            cursor++;
            return true;
        }

        if (text[cursor] == '"')
        {
            cursor++;
            var decoded = new StringBuilder();
            while (cursor < text.Length)
            {
                var character = text[cursor++];
                if (character == '"')
                {
                    segment = decoded.ToString();
                    return true;
                }

                if (character != '\\')
                {
                    if (character is '\r' or '\n') return false;
                    decoded.Append(character);
                    continue;
                }

                if (cursor >= text.Length || !TryDecodeBasicKeyEscape(text, ref cursor, decoded)) return false;
            }

            return false;
        }

        var bareStart = cursor;
        while (cursor < text.Length && IsBareKeyCharacter(text[cursor])) cursor++;
        if (cursor == bareStart) return false;
        segment = text[bareStart..cursor];
        return true;
    }

    private static bool TryDecodeBasicKeyEscape(string text, ref int cursor, StringBuilder output)
    {
        var escape = text[cursor++];
        switch (escape)
        {
            case 'b': output.Append('\b'); return true;
            case 't': output.Append('\t'); return true;
            case 'n': output.Append('\n'); return true;
            case 'f': output.Append('\f'); return true;
            case 'r': output.Append('\r'); return true;
            case '"': output.Append('"'); return true;
            case '\\': output.Append('\\'); return true;
            case 'u': return TryDecodeUnicodeEscape(text, ref cursor, 4, output);
            case 'U': return TryDecodeUnicodeEscape(text, ref cursor, 8, output);
            default: return false;
        }
    }

    private static bool TryDecodeUnicodeEscape(
        string text,
        ref int cursor,
        int digitCount,
        StringBuilder output)
    {
        if (cursor + digitCount > text.Length) return false;

        long scalar = 0;
        for (var index = 0; index < digitCount; index++)
        {
            var character = text[cursor + index];
            if (!Uri.IsHexDigit(character)) return false;
            scalar = (scalar * 16) + HexValue(character);
        }

        if (scalar > int.MaxValue || !Rune.IsValid((int)scalar)) return false;
        output.Append(new Rune((int)scalar).ToString());
        cursor += digitCount;
        return true;
    }

    private static int HexValue(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'a' and <= 'f' => character - 'a' + 10,
        >= 'A' and <= 'F' => character - 'A' + 10,
        _ => throw new ArgumentOutOfRangeException(nameof(character))
    };

    private static bool TryReadTomlString(string text, int valueStart, out string value)
    {
        value = string.Empty;
        var cursor = valueStart;
        SkipTomlTrivia(text, ref cursor);
        if (cursor >= text.Length || text[cursor] is not ('"' or '\'')) return false;

        var quote = text[cursor];
        var multiline = IsTripleQuote(text, cursor, quote);
        cursor += multiline ? 3 : 1;
        if (multiline)
        {
            if (cursor < text.Length && text[cursor] == '\r')
            {
                cursor++;
                if (cursor < text.Length && text[cursor] == '\n') cursor++;
            }
            else if (cursor < text.Length && text[cursor] == '\n')
            {
                cursor++;
            }
        }

        var decoded = new StringBuilder();
        while (cursor < text.Length)
        {
            if (multiline && IsTripleQuote(text, cursor, quote)
                          && (quote == '\'' || !IsEscaped(text, cursor)))
            {
                value = decoded.ToString();
                return true;
            }
            if (!multiline && text[cursor] == quote)
            {
                value = decoded.ToString();
                return true;
            }

            var character = text[cursor++];
            if (!multiline && character is '\r' or '\n') return false;
            if (quote == '\'' || character != '\\')
            {
                decoded.Append(character);
                continue;
            }

            if (multiline && cursor < text.Length && text[cursor] is '\r' or '\n')
            {
                if (text[cursor] == '\r')
                {
                    cursor++;
                    if (cursor < text.Length && text[cursor] == '\n') cursor++;
                }
                else
                {
                    cursor++;
                }
                while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
                continue;
            }

            if (cursor >= text.Length || !TryDecodeBasicKeyEscape(text, ref cursor, decoded)) return false;
        }

        return false;
    }

    private static bool TryReadTomlBoolean(string text, int valueStart, out bool value)
    {
        value = false;
        var cursor = valueStart;
        SkipTomlTrivia(text, ref cursor);
        if (MatchesTomlToken(text, cursor, "true"))
        {
            value = true;
            return true;
        }
        return MatchesTomlToken(text, cursor, "false");
    }

    private static bool MatchesTomlToken(string text, int start, string token)
    {
        if (start < 0 || start + token.Length > text.Length
                      || !text.AsSpan(start, token.Length).SequenceEqual(token.AsSpan())) return false;
        var end = start + token.Length;
        return end == text.Length || text[end] is ' ' or '\t' or '\r' or '\n' or '#' or ',' or '}';
    }

    private static bool TryInspectAgentsInlineTable(string text, int valueStart, out bool disabled)
    {
        disabled = false;
        var cursor = valueStart;
        SkipTomlTrivia(text, ref cursor);
        if (cursor >= text.Length || text[cursor] != '{') return false;

        var braceDepth = 1;
        var arrayDepth = 0;
        var expectingKey = true;
        cursor++;
        while (cursor < text.Length && braceDepth > 0)
        {
            if (expectingKey && braceDepth == 1 && arrayDepth == 0)
            {
                SkipTomlTrivia(text, ref cursor);
                if (cursor < text.Length && text[cursor] == '}') return true;
                if (!TryParseDottedKey(text, ref cursor, out var keyPath)) return false;
                SkipHorizontalWhitespace(text, ref cursor);
                if (cursor >= text.Length || text[cursor] != '=') return false;
                cursor++;
                if (keyPath.Count == 1 && keyPath[0].Equals("enabled", StringComparison.Ordinal))
                {
                    if (!TryReadTomlBoolean(text, cursor, out var enabled)) return false;
                    if (!enabled)
                    {
                        disabled = true;
                        return true;
                    }
                }
                expectingKey = false;
            }

            if (cursor >= text.Length) break;
            var character = text[cursor];
            if (character == '#')
            {
                SkipComment(text, ref cursor);
                continue;
            }
            if (character is '"' or '\'')
            {
                SkipTomlString(text, ref cursor);
                continue;
            }

            switch (character)
            {
                case '{': braceDepth++; break;
                case '}':
                    braceDepth--;
                    if (braceDepth == 0) return true;
                    break;
                case '[': arrayDepth++; break;
                case ']': if (arrayDepth > 0) arrayDepth--; break;
                case ',' when braceDepth == 1 && arrayDepth == 0: expectingKey = true; break;
            }
            cursor++;
        }

        return false;
    }

    private static bool InlineTableDefinesTargetWorker(string text, int valueStart)
    {
        var cursor = valueStart;
        SkipTomlTrivia(text, ref cursor);
        if (cursor >= text.Length || text[cursor] != '{') return false;

        var braceDepth = 1;
        var arrayDepth = 0;
        var expectingKey = true;
        cursor++;

        while (cursor < text.Length && braceDepth > 0)
        {
            if (expectingKey && braceDepth == 1 && arrayDepth == 0)
            {
                SkipTomlTrivia(text, ref cursor);
                if (cursor >= text.Length || text[cursor] == '}') return false;

                var keyCursor = cursor;
                if (TryParseDottedKey(text, ref cursor, out var keyPath))
                {
                    SkipHorizontalWhitespace(text, ref cursor);
                    if (cursor < text.Length && text[cursor] == '=')
                    {
                        if (keyPath.Count > 0 &&
                            keyPath[0].Equals("codex_total_manager_external", StringComparison.Ordinal))
                        {
                            return true;
                        }

                        cursor++;
                        expectingKey = false;
                        continue;
                    }
                }

                cursor = keyCursor;
                expectingKey = false;
            }

            var character = text[cursor];
            if (character == '#')
            {
                SkipComment(text, ref cursor);
                continue;
            }

            if (character is '"' or '\'')
            {
                SkipTomlString(text, ref cursor);
                continue;
            }

            switch (character)
            {
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    braceDepth--;
                    break;
                case '[':
                    arrayDepth++;
                    break;
                case ']':
                    if (arrayDepth > 0) arrayDepth--;
                    break;
                case ',' when braceDepth == 1 && arrayDepth == 0:
                    expectingKey = true;
                    break;
            }

            cursor++;
        }

        return false;
    }

    private static void SkipTomlTrivia(string text, ref int cursor)
    {
        while (cursor < text.Length)
        {
            if (char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
                continue;
            }

            if (text[cursor] != '#') return;
            SkipComment(text, ref cursor);
        }
    }

    private static void SkipComment(string text, ref int cursor)
    {
        while (cursor < text.Length && text[cursor] is not ('\r' or '\n')) cursor++;
    }

    private static void SkipTomlString(string text, ref int cursor)
    {
        var quote = text[cursor];
        var multiline = IsTripleQuote(text, cursor, quote);
        cursor += multiline ? 3 : 1;

        while (cursor < text.Length)
        {
            if (multiline && IsTripleQuote(text, cursor, quote) &&
                (quote == '\'' || !IsEscaped(text, cursor)))
            {
                cursor += 3;
                return;
            }

            if (!multiline && text[cursor] == quote)
            {
                cursor++;
                return;
            }

            if (quote == '"' && text[cursor] == '\\')
            {
                cursor = Math.Min(cursor + 2, text.Length);
                continue;
            }

            if (!multiline && text[cursor] is '\r' or '\n') return;
            cursor++;
        }
    }

    private static void SkipHorizontalWhitespace(string text, ref int cursor)
    {
        while (cursor < text.Length && text[cursor] is ' ' or '\t') cursor++;
    }

    private static bool IsBareKeyCharacter(char character) =>
        character is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            >= '0' and <= '9' or
            '_' or '-';

    private static bool PathStartsWithTarget(IReadOnlyList<string> path) =>
        path.Count >= 2 &&
        path[0].Equals("mcp_servers", StringComparison.Ordinal) &&
        path[1].Equals("codex_total_manager_external", StringComparison.Ordinal);

    private static bool PathEqualsMcpServers(IReadOnlyList<string> path) =>
        path.Count == 1 && path[0].Equals("mcp_servers", StringComparison.Ordinal);

    private static bool IsTomlLexicallyAndStructurallyValid(
        string text,
        IReadOnlyList<TextLine> lines)
    {
        var delimiters = new Stack<char>();
        var stringKind = SafetyStringKind.None;
        var scratch = new StringBuilder();
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            switch (stringKind)
            {
                case SafetyStringKind.Basic:
                    if (character is '\r' or '\n') return false;
                    if (character == '"') stringKind = SafetyStringKind.None;
                    else if (character == '\\')
                    {
                        var escapeCursor = index + 1;
                        scratch.Clear();
                        if (escapeCursor >= text.Length
                            || !TryDecodeBasicKeyEscape(text, ref escapeCursor, scratch)) return false;
                        index = escapeCursor - 1;
                    }
                    continue;
                case SafetyStringKind.Literal:
                    if (character is '\r' or '\n') return false;
                    if (character == '\'') stringKind = SafetyStringKind.None;
                    continue;
                case SafetyStringKind.MultilineBasic:
                    if (IsTripleQuote(text, index, '"') && !IsEscaped(text, index))
                    {
                        stringKind = SafetyStringKind.None;
                        index += 2;
                    }
                    else if (character == '\\')
                    {
                        if (index + 1 < text.Length && text[index + 1] is '\r' or '\n')
                        {
                            index++;
                            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                            while (index + 1 < text.Length && char.IsWhiteSpace(text[index + 1])) index++;
                        }
                        else
                        {
                            var escapeCursor = index + 1;
                            scratch.Clear();
                            if (escapeCursor >= text.Length
                                || !TryDecodeBasicKeyEscape(text, ref escapeCursor, scratch)) return false;
                            index = escapeCursor - 1;
                        }
                    }
                    continue;
                case SafetyStringKind.MultilineLiteral:
                    if (IsTripleQuote(text, index, '\''))
                    {
                        stringKind = SafetyStringKind.None;
                        index += 2;
                    }
                    continue;
            }

            if (character == '#')
            {
                while (index + 1 < text.Length && text[index + 1] is not ('\r' or '\n')) index++;
                continue;
            }
            if (character == '"')
            {
                if (IsTripleQuote(text, index, '"'))
                {
                    stringKind = SafetyStringKind.MultilineBasic;
                    index += 2;
                }
                else
                {
                    stringKind = SafetyStringKind.Basic;
                }
                continue;
            }
            if (character == '\'')
            {
                if (IsTripleQuote(text, index, '\''))
                {
                    stringKind = SafetyStringKind.MultilineLiteral;
                    index += 2;
                }
                else
                {
                    stringKind = SafetyStringKind.Literal;
                }
                continue;
            }

            if (character is '[' or '{') delimiters.Push(character);
            else if (character == ']')
            {
                if (delimiters.Count == 0 || delimiters.Pop() != '[') return false;
            }
            else if (character == '}')
            {
                if (delimiters.Count == 0 || delimiters.Pop() != '{') return false;
            }
        }

        if (stringKind != SafetyStringKind.None || delimiters.Count != 0) return false;
        foreach (var line in lines.Where(item => item.IsTopLevelSyntax))
        {
            var trimmed = line.Text.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            if (TryParseTableHeaderPath(line.Text, out _)) continue;
            if (!TryParseKeyAssignment(line.Text, out _, out var valueStart)) return false;
            var cursor = valueStart;
            SkipHorizontalWhitespace(line.Text, ref cursor);
            if (cursor >= line.Text.Length || line.Text[cursor] == '#') return false;
        }
        return true;
    }

    private static IReadOnlyList<TextLine> ReadLines(string text)
    {
        var rawLines = new List<(int Start, int ContentEnd, int End, string Text)>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n')) continue;

            var contentEnd = index;
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            rawLines.Add((start, contentEnd, index + 1, text[start..contentEnd]));
            start = index + 1;
        }

        if (start < text.Length || text.Length == 0)
        {
            rawLines.Add((start, text.Length, text.Length, text[start..]));
        }

        var lines = new List<TextLine>(rawLines.Count);
        var multilineString = MultilineStringKind.None;
        var arrayDepth = 0;
        var inlineTableDepth = 0;
        foreach (var rawLine in rawLines)
        {
            var isTopLevelSyntax = multilineString == MultilineStringKind.None &&
                                   arrayDepth == 0 &&
                                   inlineTableDepth == 0;
            lines.Add(new TextLine(
                rawLine.Start,
                rawLine.ContentEnd,
                rawLine.End,
                rawLine.Text,
                isTopLevelSyntax));
            AdvanceTomlLexicalState(rawLine.Text, ref multilineString, ref arrayDepth, ref inlineTableDepth);
        }

        return lines;
    }

    private static void AdvanceTomlLexicalState(
        string line,
        ref MultilineStringKind multilineString,
        ref int arrayDepth,
        ref int inlineTableDepth)
    {
        for (var index = 0; index < line.Length; index++)
        {
            if (multilineString == MultilineStringKind.Basic)
            {
                if (IsTripleQuote(line, index, '"') && !IsEscaped(line, index))
                {
                    multilineString = MultilineStringKind.None;
                    index += 2;
                }

                continue;
            }

            if (multilineString == MultilineStringKind.Literal)
            {
                if (IsTripleQuote(line, index, '\''))
                {
                    multilineString = MultilineStringKind.None;
                    index += 2;
                }

                continue;
            }

            var character = line[index];
            if (character == '#') break;

            if (character == '"')
            {
                if (IsTripleQuote(line, index, '"'))
                {
                    multilineString = MultilineStringKind.Basic;
                    index += 2;
                }
                else
                {
                    SkipBasicString(line, ref index);
                }

                continue;
            }

            if (character == '\'')
            {
                if (IsTripleQuote(line, index, '\''))
                {
                    multilineString = MultilineStringKind.Literal;
                    index += 2;
                }
                else
                {
                    SkipLiteralString(line, ref index);
                }

                continue;
            }

            switch (character)
            {
                case '[':
                    arrayDepth++;
                    break;
                case ']':
                    if (arrayDepth > 0) arrayDepth--;
                    break;
                case '{':
                    inlineTableDepth++;
                    break;
                case '}':
                    if (inlineTableDepth > 0) inlineTableDepth--;
                    break;
            }
        }
    }

    private static void SkipBasicString(string line, ref int index)
    {
        for (index++; index < line.Length; index++)
        {
            if (line[index] == '\\')
            {
                index++;
                continue;
            }

            if (line[index] == '"') return;
        }
    }

    private static void SkipLiteralString(string line, ref int index)
    {
        for (index++; index < line.Length; index++)
        {
            if (line[index] == '\'') return;
        }
    }

    private static bool IsTripleQuote(string line, int index, char quote) =>
        index + 2 < line.Length &&
        line[index] == quote &&
        line[index + 1] == quote &&
        line[index + 2] == quote;

    private static bool IsEscaped(string line, int index)
    {
        var backslashCount = 0;
        for (var cursor = index - 1; cursor >= 0 && line[cursor] == '\\'; cursor--) backslashCount++;
        return backslashCount % 2 != 0;
    }

    private enum MultilineStringKind
    {
        None,
        Basic,
        Literal
    }

    private enum SafetyStringKind
    {
        None,
        Basic,
        Literal,
        MultilineBasic,
        MultilineLiteral
    }

    private sealed record TextLine(
        int Start,
        int ContentEnd,
        int End,
        string Text,
        bool IsTopLevelSyntax);
}
