namespace CodexModelManager.Services;

public static class RuntimeMode
{
    private const string DetachedEnvironmentVariable = "CMM_DETACHED_UI";
    private const string DetachedNoExternalNetworkEnvironmentVariable = "CMM_DETACHED_NO_EXTERNAL_NETWORK";
    private const string DetachedDataRootEnvironmentVariable = "CMM_DETACHED_DATA_ROOT";
    private const string TestDoubleCommand = "--codex-test-double-self-test";
    private const string TestDoubleTokenEnvironmentVariable = "CMM_CODEX_TEST_DOUBLE_TOKEN";
    private const string TestDoubleEngineEnvironmentVariable = "CMM_CODEX_TEST_DOUBLE_ENGINE_URL";
    private const string TestDoubleGatewayEnvironmentVariable = "CMM_CODEX_TEST_DOUBLE_GATEWAY_URL";
    private static bool _initialized;

    public static bool IsDetachedUi { get; private set; }
    public static bool IsCodexTestDouble { get; private set; }
    public static bool AllowsRealCodexConnectionToggle { get; private set; }
    public static bool AllowsExternalStatusConnections { get; private set; } = true;
    public static bool AllowsCustomExtensions { get; private set; }
    public static Uri? CodexTestDoubleEngineUri { get; private set; }
    public static Uri? CodexTestDoubleGatewayUri { get; private set; }
    public static string? CodexTestDoubleToken { get; private set; }

    public static bool RequiresExplicitIsolation(
        IReadOnlyList<string> arguments,
        Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var requested = arguments.Any(argument =>
            argument.Equals("--detached-ui", StringComparison.OrdinalIgnoreCase));
        var environmentRequested = IsEnabled(readEnvironment(DetachedEnvironmentVariable));
        var testDoubleRequested = arguments.Any(argument =>
            argument.Equals(TestDoubleCommand, StringComparison.OrdinalIgnoreCase));
        var completeSandbox = !string.IsNullOrWhiteSpace(readEnvironment("CMM_SANDBOX_CODEX_HOME"))
                              && !string.IsNullOrWhiteSpace(readEnvironment("CMM_SANDBOX_APPDATA"));
        var explicitIsolationEnvironment = IsEnabled(readEnvironment(DetachedNoExternalNetworkEnvironmentVariable))
                                           || !string.IsNullOrWhiteSpace(readEnvironment(DetachedDataRootEnvironmentVariable));
        return requested
               || environmentRequested
               || testDoubleRequested
               || completeSandbox
               || explicitIsolationEnvironment;
    }

    public static void Initialize(IReadOnlyList<string> arguments)
    {
        if (_initialized) return;
        _initialized = true;

        var testDoubleRequested = arguments.Any(argument =>
            argument.Equals(TestDoubleCommand, StringComparison.OrdinalIgnoreCase));
        var isolationRequested = RequiresExplicitIsolation(arguments);

#if CMM_DETACHED_ONLY
        IsDetachedUi = true;
        AllowsRealCodexConnectionToggle = false;
        AllowsCustomExtensions = false;
#else
        // "Codex is not connected" is an ordinary application state, not a test
        // sandbox.  Only an explicit isolation request may redirect the Manager to
        // fake stores and lock real-machine actions.
        IsDetachedUi = isolationRequested;
        AllowsRealCodexConnectionToggle = !IsDetachedUi;
        AllowsCustomExtensions = !IsDetachedUi;
#endif

        if (!IsDetachedUi) return;

        if (testDoubleRequested)
            ConfigureCodexTestDouble();

        AllowsExternalStatusConnections = !IsCodexTestDouble
                                           && !IsEnabled(Environment.GetEnvironmentVariable(
                                               DetachedNoExternalNetworkEnvironmentVariable));

        Environment.SetEnvironmentVariable(
            DetachedEnvironmentVariable,
            "1",
            EnvironmentVariableTarget.Process);

        var dataRoot = ResolveDetachedDataRoot();
        SetProcessEnvironment("CMM_SANDBOX_CODEX_HOME", Path.Combine(dataRoot, "codex-home"));
        SetProcessEnvironment("CMM_SANDBOX_APPDATA", Path.Combine(dataRoot, "runtime"));
        SetProcessEnvironment("CMM_SANDBOX_OPENCODEX_HOME", Path.Combine(dataRoot, "native-home"));
        SetProcessEnvironment("CMM_SANDBOX_DREAMSKIN", Path.Combine(dataRoot, "dream-skin"));
        SetProcessEnvironment("CMM_RUNTIME_ROOT", Path.Combine(dataRoot, "runtime"));

        // Ordinary detached mode always points at a broken loopback endpoint. The only
        // exception is the explicit test-double command, whose two ports are reserved
        // for the inert local simulator and cannot be changed to a production port.
        SetProcessEnvironment(
            "CMM_SANDBOX_OCX_URL",
            IsCodexTestDouble
                ? CodexTestDoubleEngineUri!.GetLeftPart(UriPartial.Authority)
                : "http://127.0.0.1:1");
    }

    public static bool ContainsBlockedServiceCommand(IReadOnlyList<string> arguments) =>
        IsDetachedUi && arguments.Any(argument =>
            argument.StartsWith("--", StringComparison.Ordinal)
             && !argument.Equals("--detached-ui", StringComparison.OrdinalIgnoreCase)
             && !argument.Equals("--ui-preview", StringComparison.OrdinalIgnoreCase)
             && !argument.Equals("--ui-stress", StringComparison.OrdinalIgnoreCase)
             && !argument.Equals(TestDoubleCommand, StringComparison.OrdinalIgnoreCase));

    public static string DetachedStatusText =>
        IsCodexTestDouble
            ? "Codex 测试替身模式：只连接 127.0.0.1:19100/19110 的无能力模拟器，真实 Codex 继续隔离。"
            : AllowsExternalStatusConnections
            ? "Codex 连接默认关闭：真实 Codex、模型和账号保持隔离；只有你点击连接按钮才会切换网关。"
            : "隔离压力测试模式：真实 Codex、服务器和 v2rayN 网络全部断开；只使用本机假数据与回环测试。";

    private static void ConfigureCodexTestDouble()
    {
        var token = Environment.GetEnvironmentVariable(TestDoubleTokenEnvironmentVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(token)
            || token.Length is < 32 or > 128
            || token.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("Codex 测试替身缺少有效的一次性十六进制测试令牌。");

        var engine = ResolveTestDoubleUri(TestDoubleEngineEnvironmentVariable, 19100);
        var gateway = ResolveTestDoubleUri(TestDoubleGatewayEnvironmentVariable, 19110);
        IsCodexTestDouble = true;
        CodexTestDoubleToken = token;
        CodexTestDoubleEngineUri = engine;
        CodexTestDoubleGatewayUri = gateway;
    }

    private static Uri ResolveTestDoubleUri(string environmentVariable, int requiredPort)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || !uri.IsLoopback
            || uri.Port != requiredPort
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                $"Codex 测试替身地址必须严格为 http://127.0.0.1:{requiredPort}/。");
        return new Uri($"http://127.0.0.1:{requiredPort}/", UriKind.Absolute);
    }

    private static void SetProcessEnvironment(string name, string value) =>
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);

    private static string ResolveDetachedDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable(DetachedDataRootEnvironmentVariable);
        var dataRoot = string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "DetachedData"))
            : Path.GetFullPath(configured);
        var volumeRoot = Path.GetPathRoot(dataRoot)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var normalized = dataRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(volumeRoot)
            || normalized.Equals(volumeRoot, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(userProfile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("隔离数据根目录不能是磁盘根目录或用户主目录。");
        if (File.Exists(dataRoot))
            throw new InvalidOperationException("隔离数据根目录不能指向普通文件。");
        return dataRoot;
    }

    private static bool IsEnabled(string? value) =>
        value is not null
        && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
