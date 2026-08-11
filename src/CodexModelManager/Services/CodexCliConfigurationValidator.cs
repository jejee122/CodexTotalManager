using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

namespace CodexModelManager.Services;

public sealed record CodexConfigValidationResult(
    bool ValidatorAvailable,
    bool IsValid,
    string StatusText,
    string? ExecutablePath = null);

public interface ICodexConfigValidator
{
    Task<CodexConfigValidationResult> ValidateAsync(
        ReadOnlyMemory<byte> configBytes,
        IReadOnlyDictionary<string, byte[]>? agentFiles = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses the installed Codex app-server in isolated CODEX_HOME directories to validate
/// a complete candidate without starting MCP servers, turns, tools, or model calls.
/// </summary>
public sealed class CodexCliConfigurationValidator : ICodexConfigValidator
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private static readonly HashSet<string> SupportedSandboxModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "read-only", "workspace-write", "danger-full-access"
    };
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly string _validationRoot;
    private readonly TimeSpan _timeout;

    public CodexCliConfigurationValidator(
        string? dataDirectory = null,
        TimeSpan? timeout = null)
    {
        var baseDirectory = dataDirectory ?? AppSettingsService.ResolveDefaultDataDirectory();
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("验证数据目录不能为空。", nameof(dataDirectory));

        _validationRoot = Path.GetFullPath(Path.Combine(baseDirectory, "config-validation"));
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Codex 配置检查超时必须介于 0 秒和 2 分钟之间。");
    }

    public async Task<CodexConfigValidationResult> ValidateAsync(
        ReadOnlyMemory<byte> configBytes,
        IReadOnlyDictionary<string, byte[]>? agentFiles = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safeAgentFiles = NormalizeAgentFiles(agentFiles);
        var executablePath = ResolveCodexExecutable();
        if (executablePath is null)
        {
            return new CodexConfigValidationResult(
                false,
                false,
                "未找到可用的 Codex 自身解析器；为防止写坏配置，应用已锁定。");
        }

        var validationId = Guid.NewGuid().ToString("N");
        string? validationDirectory = null;
        CodexConfigValidationResult? result = null;
        var cleanupSucceeded = true;
        try
        {
            using var timeoutCancellation = new CancellationTokenSource(_timeout);
            using var validationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);
            var validationToken = validationCancellation.Token;

            validationDirectory = CreateValidationDirectory(validationId);
            await File.WriteAllBytesAsync(
                Path.Combine(validationDirectory, "config.toml"),
                configBytes,
                validationToken);

            if (safeAgentFiles.Count > 0)
            {
                var agentsDirectory = Path.Combine(validationDirectory, "agents");
                Directory.CreateDirectory(agentsDirectory);
                foreach (var pair in safeAgentFiles)
                {
                    validationToken.ThrowIfCancellationRequested();
                    await File.WriteAllBytesAsync(
                        Path.Combine(agentsDirectory, pair.Key),
                        pair.Value,
                        validationToken);
                }
            }

            var mainConfigRun = await RunAppServerConfigReadAsync(
                executablePath,
                validationDirectory,
                validationToken);
            if (!mainConfigRun.Accepted)
            {
                result = new CodexConfigValidationResult(
                    true,
                    false,
                    "Codex app-server 拒绝候选配置或报告配置警告；未写入真实配置。",
                    executablePath);
            }
            else if (!ValidateAgentDocuments(safeAgentFiles))
            {
                result = new CodexConfigValidationResult(
                    true,
                    false,
                    "候选 Agent 配置不符合严格 TOML 或必填字段规则；未写入真实配置。",
                    executablePath);
            }
            else
            {
                result = new CodexConfigValidationResult(
                    true,
                    true,
                    "Codex app-server 已接受候选主配置，且启动至退出未报告 Agent 配置警告（零额度）。",
                    executablePath);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = new CodexConfigValidationResult(
                true,
                false,
                "Codex app-server 隔离检查超时；未写入真实配置。",
                executablePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or Win32Exception
                                   or SecurityException
                                   or NotSupportedException
                                   or InvalidOperationException)
        {
            result = new CodexConfigValidationResult(
                false,
                false,
                "Codex app-server 无法在隔离环境中完成检查；为安全起见应用已锁定。",
                executablePath);
        }
        finally
        {
            if (validationDirectory is not null)
                cleanupSucceeded = TryDeleteValidationDirectory(validationDirectory, validationId);
        }

        result ??= new CodexConfigValidationResult(
            false,
            false,
            "Codex app-server 未返回检查结果；为安全起见应用已锁定。",
            executablePath);

        if (!cleanupSucceeded)
        {
            return result with
            {
                IsValid = false,
                StatusText = "隔离验证目录未能安全清理；已停止删除并锁定应用，真实配置未受影响。"
            };
        }

        return result;
    }

    private async Task<AppServerConfigReadResult> RunAppServerConfigReadAsync(
        string executablePath,
        string validationDirectory,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = validationDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--stdio");
        start.Environment["CODEX_HOME"] = validationDirectory;

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Codex app-server 检查进程未能启动。");

        using var ioCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var outputState = new AppServerOutputState();
        var stdoutReader = ReadAppServerOutputAsync(
            process.StandardOutput,
            outputState,
            ioCancellation.Token);
        var stderrDrain = DrainAndDiscardAsync(process.StandardError, ioCancellation.Token);
        try
        {
            await WriteProtocolMessageAsync(
                process.StandardInput,
                "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex_total_manager_config_validator\",\"version\":\"1.0\"}}}",
                cancellationToken);
            var initialize = await outputState.InitializeResponse.Task.WaitAsync(cancellationToken);
            var isolationMatches = initialize.Succeeded
                                   && initialize.CodexHome is not null
                                   && PathComparer.Equals(
                                       Path.TrimEndingDirectorySeparator(Path.GetFullPath(initialize.CodexHome)),
                                       Path.TrimEndingDirectorySeparator(Path.GetFullPath(validationDirectory)));

            AppServerProtocolResponse configRead = new(false, null);
            if (isolationMatches)
            {
                await WriteProtocolMessageAsync(
                    process.StandardInput,
                    "{\"method\":\"initialized\"}",
                    cancellationToken);
                await WriteProtocolMessageAsync(
                    process.StandardInput,
                    "{\"id\":2,\"method\":\"config/read\",\"params\":{\"cwd\":null,\"includeLayers\":false}}",
                    cancellationToken);
                configRead = await outputState.ConfigReadResponse.Task.WaitAsync(cancellationToken);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            await stdoutReader;
            await stderrDrain;
            return new AppServerConfigReadResult(
                process.ExitCode == 0
                && isolationMatches
                && configRead.Succeeded
                && !outputState.ConfigWarningSeen
                && !outputState.InvalidOutputSeen);
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { }
            if (!HasExited(process))
                await TerminateProcessTreeAsync(process);

            ioCancellation.Cancel();
            await AwaitDrainsAsync(stdoutReader, stderrDrain);
        }
    }

    private static async Task WriteProtocolMessageAsync(
        StreamWriter writer,
        string message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteLineAsync(message.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task ReadAppServerOutputAsync(
        StreamReader reader,
        AppServerOutputState state,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        state.MarkInvalidOutput();
                        continue;
                    }

                    if (root.TryGetProperty("method", out var method)
                        && method.ValueKind == JsonValueKind.String
                        && string.Equals(method.GetString(), "configWarning", StringComparison.Ordinal))
                        state.MarkConfigWarning();

                    if (!root.TryGetProperty("id", out var id)
                        || id.ValueKind != JsonValueKind.Number
                        || !id.TryGetInt64(out var numericId)
                        || numericId is not (1 or 2))
                        continue;

                    var response = ParseProtocolResponse(root, includeCodexHome: numericId == 1);
                    if (numericId == 1) state.InitializeResponse.TrySetResult(response);
                    else state.ConfigReadResponse.TrySetResult(response);
                }
                catch (JsonException)
                {
                    state.MarkInvalidOutput();
                }
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or ObjectDisposedException
                                   or OperationCanceledException
                                   or DecoderFallbackException)
        {
            if (!cancellationToken.IsCancellationRequested) state.MarkInvalidOutput();
        }
        finally
        {
            var failed = new AppServerProtocolResponse(false, null);
            state.InitializeResponse.TrySetResult(failed);
            state.ConfigReadResponse.TrySetResult(failed);
        }
    }

    private static AppServerProtocolResponse ParseProtocolResponse(
        JsonElement root,
        bool includeCodexHome)
    {
        if (root.TryGetProperty("error", out var error)
            && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            return new AppServerProtocolResponse(false, null);
        if (!root.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object)
            return new AppServerProtocolResponse(false, null);

        string? codexHome = null;
        if (includeCodexHome
            && result.TryGetProperty("codexHome", out var codexHomeElement)
            && codexHomeElement.ValueKind == JsonValueKind.String)
            codexHome = codexHomeElement.GetString();
        return new AppServerProtocolResponse(true, codexHome);
    }

    private string CreateValidationDirectory(string validationId)
    {
        if (!Guid.TryParseExact(validationId, "N", out _))
            throw new InvalidOperationException("隔离验证目录标识无效。");

        Directory.CreateDirectory(_validationRoot);
        if ((File.GetAttributes(_validationRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("隔离验证根目录不能是重解析点。");
        var directory = Path.GetFullPath(Path.Combine(_validationRoot, validationId));
        if (!IsExactValidationChild(directory, validationId) || Directory.Exists(directory) || File.Exists(directory))
            throw new InvalidOperationException("无法安全创建隔离验证目录。");

        Directory.CreateDirectory(directory);
        var attributes = File.GetAttributes(directory);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("隔离验证目录不能是重解析点。");
        return directory;
    }

    private bool TryDeleteValidationDirectory(string validationDirectory, string validationId)
    {
        if (!IsExactValidationChild(validationDirectory, validationId)) return false;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (!Directory.Exists(validationDirectory)) return true;
                if ((File.GetAttributes(validationDirectory) & FileAttributes.ReparsePoint) != 0) return false;

                DeleteDirectoryTree(validationDirectory, validationDirectory);
                if (!Directory.Exists(validationDirectory) && !File.Exists(validationDirectory)) return true;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or SecurityException
                                       or NotSupportedException)
            {
                if (attempt == 5) return false;
            }

            Thread.Sleep(100 * (1 << attempt));
        }

        return false;
    }

    private static void DeleteDirectoryTree(string ownedRoot, string currentDirectory)
    {
        EnsurePathInsideOwnedRoot(ownedRoot, currentDirectory, allowRoot: true);
        foreach (var entry in Directory.EnumerateFileSystemEntries(currentDirectory))
        {
            EnsurePathInsideOwnedRoot(ownedRoot, entry, allowRoot: false);
            var attributes = File.GetAttributes(entry);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
            if (isDirectory && !isReparsePoint)
            {
                DeleteDirectoryTree(ownedRoot, entry);
                continue;
            }

            if (isDirectory)
            {
                Directory.Delete(entry, false);
                continue;
            }

            if ((attributes & FileAttributes.ReadOnly) != 0 && !isReparsePoint)
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            File.Delete(entry);
        }

        Directory.Delete(currentDirectory, false);
    }

    private bool IsExactValidationChild(string path, string validationId)
    {
        if (!Guid.TryParseExact(validationId, "N", out _)) return false;
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(_validationRoot);
        var parent = Directory.GetParent(fullPath)?.FullName;
        return parent is not null
               && PathComparer.Equals(Path.TrimEndingDirectorySeparator(parent), root)
               && string.Equals(Path.GetFileName(fullPath), validationId, StringComparison.Ordinal);
    }

    private static void EnsurePathInsideOwnedRoot(string ownedRoot, string path, bool allowRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownedRoot));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var relative = Path.GetRelativePath(root, candidate);
        if ((allowRoot && relative == ".") || IsSafeRelativePath(relative)) return;
        throw new IOException("隔离目录清理目标越界，已停止删除。");
    }

    private static bool IsSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath == "."
            || relativePath == ".."
            || Path.IsPathFullyQualified(relativePath))
            return false;
        return !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool ValidateAgentDocuments(IReadOnlyDictionary<string, byte[]> agentFiles)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in agentFiles)
        {
            string sourceText;
            try { sourceText = Utf8NoBom.GetString(pair.Value); }
            catch (DecoderFallbackException) { return false; }

            if (!TryParseAgentDocument(sourceText, out var document)) return false;
            if (!TryGetRequiredAgentString(document, "name", out var name)
                || name.Any(char.IsControl)
                || !names.Add(name)
                || !TryGetRequiredAgentString(document, "description", out _)
                || !TryGetRequiredAgentString(document, "developer_instructions", out _))
                return false;

            if (document.TryGetValue("model", out var modelValue)
                && (modelValue is not string model || !IsSafeModelValue(model)))
                return false;
            if (document.TryGetValue("model_reasoning_effort", out var effortValue)
                && (effortValue is not string effort || !IsSafeReasoningEffort(effort)))
                return false;
            if (document.TryGetValue("sandbox_mode", out var sandboxValue)
                && (sandboxValue is not string sandbox || !SupportedSandboxModes.Contains(sandbox)))
                return false;
        }

        return true;
    }

    private static bool TryGetRequiredAgentString(TomlTable document, string key, out string value)
    {
        if (document.TryGetValue(key, out var rawValue)
            && rawValue is string stringValue
            && !string.IsNullOrWhiteSpace(stringValue))
        {
            value = stringValue;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsSafeModelValue(string model) =>
        model.Length is > 0 and <= 256
        && !model.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    private static bool IsSafeReasoningEffort(string effort) =>
        effort.Length is > 0 and <= 64
        && !effort.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    private static bool TryParseAgentDocument(string sourceText, out TomlTable document)
    {
        if (TryParseTomlVariant(sourceText, out document)) return true;
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
        var kind = TomlValidationStringKind.None;
        var changed = false;

        for (var index = 0; index < sourceText.Length;)
        {
            var character = sourceText[index];
            switch (kind)
            {
                case TomlValidationStringKind.None:
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
                                ? TomlValidationStringKind.MultilineBasic
                                : TomlValidationStringKind.MultilineLiteral;
                            index += 3;
                        }
                        else
                        {
                            output.Append(character);
                            kind = character == '"'
                                ? TomlValidationStringKind.Basic
                                : TomlValidationStringKind.Literal;
                            index++;
                        }
                        continue;
                    }
                    output.Append(character);
                    index++;
                    continue;

                case TomlValidationStringKind.Basic:
                    if (character is '\r' or '\n') return sourceText;
                    output.Append(character);
                    index++;
                    if (character == '"') kind = TomlValidationStringKind.None;
                    else if (character == '\\')
                    {
                        if (index >= sourceText.Length || sourceText[index] is '\r' or '\n') return sourceText;
                        output.Append(sourceText[index++]);
                    }
                    continue;

                case TomlValidationStringKind.Literal:
                    if (character is '\r' or '\n') return sourceText;
                    output.Append(character);
                    index++;
                    if (character == '\'') kind = TomlValidationStringKind.None;
                    continue;

                case TomlValidationStringKind.MultilineLiteral:
                    if (character == '\'')
                    {
                        var quoteRun = CountConsecutive(sourceText, index, '\'');
                        if (quoteRun >= 3)
                        {
                            if (quoteRun > 5) return sourceText;
                            kind = TomlValidationStringKind.None;
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

                case TomlValidationStringKind.MultilineBasic:
                    if (character == '"')
                    {
                        var quoteRun = CountConsecutive(sourceText, index, '"');
                        if (quoteRun >= 3)
                        {
                            if (quoteRun > 5) return sourceText;
                            kind = TomlValidationStringKind.None;
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

    private static IReadOnlyDictionary<string, byte[]> NormalizeAgentFiles(
        IReadOnlyDictionary<string, byte[]>? agentFiles)
    {
        var normalized = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (agentFiles is null) return normalized;

        foreach (var pair in agentFiles)
        {
            var fileName = ValidateAgentFileName(pair.Key);
            if (pair.Value is null)
                throw new ArgumentException("代理配置内容不能为空。", nameof(agentFiles));
            if (!normalized.TryAdd(fileName, pair.Value))
                throw new ArgumentException("代理配置文件名不能重复。", nameof(agentFiles));
        }

        return normalized;
    }

    private static string ValidateAgentFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Length > 160
            || fileName is "." or ".."
            || Path.IsPathFullyQualified(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || fileName.Contains(Path.DirectorySeparatorChar)
            || fileName.Contains(Path.AltDirectorySeparatorChar)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !fileName.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("代理配置必须使用安全的 .toml 文件名。", nameof(fileName));

        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem)
            || stem.EndsWith(' ')
            || stem.EndsWith('.'))
            throw new ArgumentException("代理配置文件名格式不安全。", nameof(fileName));

        var deviceName = stem.Trim().Split('.', 2)[0].TrimEnd(' ', '.');
        if (ReservedWindowsNames.Contains(deviceName))
            throw new ArgumentException("代理配置文件名是 Windows 保留名称。", nameof(fileName));
        return fileName;
    }

    private static string? ResolveCodexExecutable()
    {
        var candidates = new List<string>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, ".codex", ".sandbox-bin", "codex.exe"));
            candidates.Add(Path.Combine(userProfile, ".codex", "plugins", ".plugin-appserver", "codex.exe"));
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var rawEntry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var entry = Environment.ExpandEnvironmentVariables(rawEntry.Trim().Trim('"'));
                if (!string.IsNullOrWhiteSpace(entry)
                    && Path.IsPathFullyQualified(entry)
                    && !entry.StartsWith(@"\\", StringComparison.Ordinal))
                    candidates.Add(Path.Combine(entry, "codex.exe"));
            }
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (string.Equals(Path.GetFileName(fullPath), "codex.exe", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(fullPath))
                    return fullPath;
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException
                                       or SecurityException)
            {
                // Ignore malformed PATH entries and continue with the remaining explicit candidates.
            }
        }

        return null;
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static async Task TerminateProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or Win32Exception
                                   or NotSupportedException)
        {
            // A concurrent process exit is expected here.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            // Disposing the Process and its redirected streams below is the final bounded fallback.
        }
    }

    private static async Task DrainAndDiscardAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken) > 0)
            {
                Array.Clear(buffer, 0, buffer.Length);
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or ObjectDisposedException
                                   or OperationCanceledException)
        {
            // The output is intentionally discarded. Cancellation is used only to bound shutdown.
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task AwaitDrainsAsync(params Task[] drains)
    {
        try
        {
            await Task.WhenAll(drains).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is IOException
                                   or ObjectDisposedException
                                   or OperationCanceledException
                                   or TimeoutException)
        {
            // No output is retained or surfaced; shutdown remains bounded.
        }
    }

    private readonly record struct AppServerConfigReadResult(bool Accepted);
    private readonly record struct AppServerProtocolResponse(bool Succeeded, string? CodexHome);

    private sealed class AppServerOutputState
    {
        private int _configWarningSeen;
        private int _invalidOutputSeen;

        public TaskCompletionSource<AppServerProtocolResponse> InitializeResponse { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AppServerProtocolResponse> ConfigReadResponse { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ConfigWarningSeen => Volatile.Read(ref _configWarningSeen) != 0;
        public bool InvalidOutputSeen => Volatile.Read(ref _invalidOutputSeen) != 0;

        public void MarkConfigWarning() => Interlocked.Exchange(ref _configWarningSeen, 1);
        public void MarkInvalidOutput() => Interlocked.Exchange(ref _invalidOutputSeen, 1);
    }

    private enum TomlValidationStringKind
    {
        None,
        Basic,
        Literal,
        MultilineBasic,
        MultilineLiteral
    }
}
