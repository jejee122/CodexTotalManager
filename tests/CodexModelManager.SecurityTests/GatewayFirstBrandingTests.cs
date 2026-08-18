using System.Text;
using Xunit;

namespace CodexModelManager.SecurityTests;

public sealed class GatewayFirstBrandingTests
{
    [Fact]
    public void ProductAndHomePage_AreGatewayFirst_AndCodexIsOptional()
    {
        var repo = FindRepositoryRoot();
        var project = File.ReadAllText(
            Path.Combine(repo, @"src\CodexModelManager\CodexModelManager.csproj"),
            Encoding.UTF8);
        var xaml = File.ReadAllText(
            Path.Combine(repo, @"src\CodexModelManager\MainWindow.xaml"),
            Encoding.UTF8);
        var installer = File.ReadAllText(
            Path.Combine(repo, @"scripts\install-local-release.ps1"),
            Encoding.UTF8);

        Assert.Contains("<AssemblyTitle>AI 中转站总管家</AssemblyTitle>", project, StringComparison.Ordinal);
        Assert.Contains("<Product>AI 中转站总管家</Product>", project, StringComparison.Ordinal);
        Assert.DoesNotContain("<Product>Codex 总管家</Product>", project, StringComparison.Ordinal);
        Assert.Contains("Title=\"AI 中转站总管家\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AI 统一中转站 API", xaml, StringComparison.Ordinal);
        Assert.Contains("HomeUnifiedGatewayUrlText", xaml, StringComparison.Ordinal);
        Assert.Contains("HomeResponsesEndpointText", xaml, StringComparison.Ordinal);
        Assert.Contains("HomeChatEndpointText", xaml, StringComparison.Ordinal);
        Assert.Contains("HomeModelsEndpointText", xaml, StringComparison.Ordinal);
        Assert.Contains("StartHomeGatewayButton_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("CopyHomeGatewayKeyButton_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("查看内置上游 API 与 URL", xaml, StringComparison.Ordinal);
        Assert.Contains("可选功能 · Codex 接入", xaml, StringComparison.Ordinal);
        Assert.Contains("一键让 Codex 接入中转站", xaml, StringComparison.Ordinal);
        Assert.Contains("DisplayName = 'AI 中转站总管家'", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void CodexToggle_HasBothConnectAndRestorePaths()
    {
        var repo = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(repo, @"src\CodexModelManager\MainWindow.xaml.cs"),
            Encoding.UTF8);

        Assert.Contains("ToggleCodexConnectionButton_Click", source, StringComparison.Ordinal);
        Assert.Contains("一键取消接入并恢复原网关", source, StringComparison.Ordinal);
        Assert.Contains("RemoveManagedNativeProvider(createSnapshot: false)", source, StringComparison.Ordinal);
        Assert.Contains("EnsureOpenCodexAsync()", source, StringComparison.Ordinal);
        Assert.Contains("不会关闭或重启 Codex", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyOwnershipMarkers_RemainForSafeUpgradeAndDisconnect()
    {
        var repo = FindRepositoryRoot();
        var config = File.ReadAllText(
            Path.Combine(repo, @"src\CodexModelManager\Services\CodexConfigService.cs"),
            Encoding.UTF8);
        var uninstaller = File.ReadAllText(
            Path.Combine(repo, @"scripts\uninstall-local-release.ps1"),
            Encoding.UTF8);

        Assert.Contains("# BEGIN CODEX TOTAL MANAGER", config, StringComparison.Ordinal);
        Assert.Contains("CodexTotalManager", uninstaller, StringComparison.Ordinal);
        Assert.Contains("AI 中转站总管家*.lnk", uninstaller, StringComparison.Ordinal);
        Assert.Contains("Codex 总管家*.lnk", uninstaller, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexTotalManager.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("CodexTotalManager repository root was not found.");
    }
}
