namespace CodexModelManager.Services;

public sealed class AppServices
{
    public AppSettingsService Settings { get; init; } = null!;
    public SecretStore Secrets { get; init; } = null!;
    public OpenCodexClient OpenCodex { get; init; } = null!;
    public OpenCodexProcessService Process { get; init; } = null!;
    public ProviderProbeService Probe { get; init; } = null!;
    public CodexConfigService CodexConfig { get; init; } = null!;
    public CodexModelCatalogService CodexModelCatalog { get; init; } = null!;
    public ConfigBackupService Backups { get; init; } = null!;
    public DashboardStatusService Dashboard { get; init; } = null!;
    public CodexDesktopBridgeService CodexDesktop { get; init; } = null!;
    public RuntimeTruthService RuntimeTruth { get; init; } = null!;
    public AccountUsageLedgerService AccountUsageLedger { get; init; } = null!;
    public AccountUsageLedgerImporter AccountUsageImporter { get; init; } = null!;
    public DreamSkinService DreamSkin { get; init; } = null!;
    public BackupCatalogService BackupCatalog { get; init; } = null!;
    public LocalServiceControlService LocalServices { get; init; } = null!;
    public PoolCatalogService PoolCatalog { get; init; } = null!;
    public CliProxyPoolService CliProxyPools { get; init; } = null!;
    public AccountPoolService AccountPools { get; init; } = null!;
    public UnifiedGatewayService UnifiedGateway { get; init; } = null!;
    public SubagentSourceRegistryService SubagentSources { get; init; } = null!;
    public SubagentConfigurationService Subagents { get; init; } = null!;
    public ExternalWorkerAuditStore ExternalWorkerAudit { get; init; } = null!;
    public ExternalWorkerService ExternalWorker { get; init; } = null!;
    public WorkerBroker WorkerBroker { get; init; } = null!;
    public WorkerBudgetLedger WorkerBudget { get; init; } = null!;
    public NativeEngineService NativeEngine { get; init; } = null!;
    public ExtensionService Extensions { get; init; } = null!;
    public ProductMaintenanceService ProductMaintenance { get; init; } = null!;

    public static AppServices Create(string? dataDirectory = null)
    {
        var settings = new AppSettingsService(dataDirectory);
        var secrets = new SecretStore(settings.DataDirectory);
        var poolCatalog = new PoolCatalogService(settings.DataDirectory, settings.ReservedLocalPorts);
        var nativeEngineDataRoot = Path.Combine(settings.DataDirectory, "native-proxy");
        var client = new OpenCodexClient(nativeEngineDataRoot, settings.NativeEnginePort);
        var codexConfig = new CodexConfigService(
            SandboxConfigPath(),
            SandboxBackupDirectory() ?? Path.Combine(settings.DataDirectory, "backups", "codex"),
            $"http://127.0.0.1:{settings.NativeEnginePort}/v1");
        var codexModelCatalog = new CodexModelCatalogService(codexConfig);
        var process = new OpenCodexProcessService(
            settings,
            secrets,
            client,
            codexConfig,
            codexModelCatalog,
            poolCatalog,
            nativeEngineDataRoot);
        var dreamSkin = new DreamSkinService(
            stateRoot: ResolveDreamSkinStateRoot(settings.DataDirectory));
        var desktop = new CodexDesktopBridgeService(codexConfig.ModelsCachePath);
        var accountUsageLedger = RuntimeMode.IsDetachedUi
            ? new AccountUsageLedgerService(
                settings.DataDirectory,
                sourcePath: Path.Combine(nativeEngineDataRoot, "request-log.jsonl"),
                sourceDisabled: true)
            : new AccountUsageLedgerService(
                settings.DataDirectory,
                sourcePath: Path.Combine(nativeEngineDataRoot, "request-log.jsonl"));
        var runtimeTruth = new RuntimeTruthService(
            new DefaultRuntimeTruthSource(poolCatalog, codexConfig, desktop, client),
            accountUsageLedger: accountUsageLedger);
        var backups = new ConfigBackupService(
            SandboxOpenCodexConfigPath() ?? Path.Combine(nativeEngineDataRoot, "config.json"),
            Path.Combine(settings.DataDirectory, "backups"));
        var cliProxy = new CliProxyPoolService(settings, secrets, poolCatalog: poolCatalog);
        var unifiedGateway = new UnifiedGatewayService(settings, secrets, cliProxy, client, poolCatalog);
        var subagentSources = new SubagentSourceRegistryService(
            settings, poolCatalog, cliProxy, client, unifiedGateway);
        var workerStatePath = Path.Combine(settings.DataDirectory, "external-worker-state.json");
        var subagentDataPath = Path.Combine(settings.DataDirectory, "subagents.json");
        var subagentBackupRoot = Path.Combine(settings.DataDirectory, "backups", "subagents");
        var subagents = new SubagentConfigurationService(
            configPath: AppServices.SandboxConfigPath(),
            agentsDirectory: AppServices.SandboxAgentsDirectory(),
            dataPath: subagentDataPath,
            backupRoot: subagentBackupRoot,
            bridgeExecutablePath: Environment.ProcessPath,
            bridgeStatePath: workerStatePath,
            applyBlockReason: async cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await desktop.ReadStateAsync();
                if (state.Connected && state.IsTurnRunning)
                    return "Codex 正在回答。为避免当前任务读取到半套配置，请等回答结束后再应用。";
                if (!state.Connected && IsCodexProcessRunning())
                    return "检测到 Codex 正在运行，但无法确认它是否空闲。请恢复可读状态或关闭 Codex 后再应用。";
                return null;
            },
            codexConfigValidator: new CodexCliConfigurationValidator(settings.DataDirectory),
            sourceDiscovery: subagentSources.DiscoverAsync);
        var workerAudit = new ExternalWorkerAuditStore(
            Path.Combine(settings.DataDirectory, "external-worker-audit.jsonl"),
            workerStatePath);
        var externalWorker = new ExternalWorkerService(
            subagents,
            unifiedGateway,
            workerAudit,
            dataDirectory: settings.DataDirectory);
        var workerBudget = new WorkerBudgetLedger(Path.Combine(settings.DataDirectory, "worker-budget.json"));
        var workerBroker = new WorkerBroker(externalWorker, subagents, workerBudget);
        var pools = new AccountPoolService(
            poolCatalog,
            cliProxy,
            client,
            process,
            codexConfig,
            desktop,
            backups,
            settings,
            secrets);
        var accountUsageImporter = new AccountUsageLedgerImporter(
            accountUsageLedger,
            cancellationToken => pools.ReadViewsAsync(cancellationToken),
            readCatalogCompleteness: () => pools.CatalogRosterCompleteness);
        return new AppServices
        {
            Settings = settings,
            Secrets = secrets,
            OpenCodex = client,
            Process = process,
            Probe = new ProviderProbeService(new[] { settings.NativeEnginePort, settings.UnifiedGatewayPort }),
            CodexConfig = codexConfig,
            CodexModelCatalog = codexModelCatalog,
            Backups = backups,
            Dashboard = !RuntimeMode.IsDetachedUi && settings.ServerMonitoringEnabled
                ? new DashboardStatusService(
                    serverSshConfigPath: settings.ServerSshConfigPath,
                    serverSshConfigSha256: settings.ServerSshConfigSha256,
                    serverAliases: settings.ServerAliases,
                    v2rayProxyPort: settings.V2rayProxyPort)
                : new DashboardStatusService(v2rayProxyPort: settings.V2rayProxyPort),
            CodexDesktop = desktop,
            RuntimeTruth = runtimeTruth,
            AccountUsageLedger = accountUsageLedger,
            AccountUsageImporter = accountUsageImporter,
            DreamSkin = dreamSkin,
            BackupCatalog = CreateBackupCatalogService(settings.DataDirectory),
            LocalServices = new LocalServiceControlService(settings, client, dreamSkin),
            PoolCatalog = poolCatalog,
            CliProxyPools = cliProxy,
            AccountPools = pools,
            UnifiedGateway = unifiedGateway,
            SubagentSources = subagentSources,
            Subagents = subagents,
            ExternalWorkerAudit = workerAudit,
            ExternalWorker = externalWorker,
            WorkerBroker = workerBroker,
            WorkerBudget = workerBudget,
            NativeEngine = CreateNativeEngineService(settings.DataDirectory),
            Extensions = new ExtensionService(settings.DataDirectory),
            ProductMaintenance = new ProductMaintenanceService(settings.DataDirectory)
        };
    }

    private static BackupCatalogService CreateBackupCatalogService(string dataDirectory) => new(
        managerRoot: Path.Combine(dataDirectory, "backups"),
        dreamRoot: Path.Combine(dataDirectory, "dreamskin-backups"));

    private static NativeEngineService CreateNativeEngineService(string dataDirectory)
    {
        var engine = new NativeEngineService();
        engine.EngineDataRootOverride = Path.Combine(dataDirectory, "native-proxy");
        return engine;
    }

    private static bool IsCodexProcessRunning()
        => CodexDesktopProcessDetector.IsRunning();

    public static string? SandboxConfigPath()
    {
        var home = Environment.GetEnvironmentVariable("CMM_SANDBOX_CODEX_HOME");
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, "config.toml");
    }

    public static string? SandboxBackupDirectory()
    {
        var home = Environment.GetEnvironmentVariable("CMM_SANDBOX_CODEX_HOME");
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, "sandbox-backups");
    }

    public static string? SandboxAgentsDirectory()
    {
        var home = Environment.GetEnvironmentVariable("CMM_SANDBOX_CODEX_HOME");
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, "agents");
    }

    public static string? SandboxOpenCodexConfigPath()
    {
        var home = Environment.GetEnvironmentVariable("CMM_SANDBOX_OPENCODEX_HOME");
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, "config.json");
    }

    public static string? SandboxAppDataDirectory()
    {
        var appData = Environment.GetEnvironmentVariable("CMM_SANDBOX_APPDATA");
        return string.IsNullOrWhiteSpace(appData) ? null : appData;
    }

    private static string? SandboxDreamSkinDirectory()
    {
        var root = Environment.GetEnvironmentVariable("CMM_SANDBOX_DREAMSKIN");
        return string.IsNullOrWhiteSpace(root) ? null : root;
    }

    private static string ResolveDreamSkinStateRoot(string dataDirectory)
    {
        var sandbox = SandboxDreamSkinDirectory();
        if (!string.IsNullOrWhiteSpace(sandbox)) return sandbox;
        var current = Path.Combine(dataDirectory, "dream-skin");
        if (Directory.Exists(current)) return current;
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexDreamSkin");
        return Directory.Exists(legacy) ? legacy : current;
    }
}
