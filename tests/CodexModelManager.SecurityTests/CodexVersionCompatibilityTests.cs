using System.Text.Json;
using CodexModelManager.Services;
using Xunit;

namespace CodexModelManager.SecurityTests;

public sealed class CodexVersionCompatibilityTests
{
    [Fact]
    public void ModelLabelResolver_ReadsNewModelsFromCodexCache()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "cmm-codex-model-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "models_cache.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                models = new object[]
                {
                    new { slug = "gpt-5.5", display_name = "GPT-5.5" },
                    new { slug = "gpt-6.0-nova", display_name = "GPT-6.0-Nova" }
                }
            }));

            Assert.Equal(
                "gpt-5.5",
                CodexWindowsAutomation.ResolveModelLabel("5.5 极高", path));
            Assert.Equal(
                "gpt-6.0-nova",
                CodexWindowsAutomation.ResolveModelLabel("6.0 Nova high", path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ModelLabelResolver_FallsBackSafelyWhenCacheIsMissingOrBroken()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            "cmm-missing-model-cache-" + Guid.NewGuid().ToString("N"),
            "models_cache.json");
        Assert.Equal(
            "gpt-5.6-sol",
            CodexWindowsAutomation.ResolveModelLabel("5.6 Sol 极高", missing));
        Assert.Equal(
            "gpt-5.5",
            CodexWindowsAutomation.ResolveModelLabel("5.5 high", missing));
    }

    [Theory]
    [InlineData("page", "app://-/index.html", "Codex", "ws://127.0.0.1:9335/devtools/page/old", true)]
    [InlineData("page", "app://chatgpt/index.html#/codex", "ChatGPT", "ws://localhost:9335/devtools/page/new", true)]
    [InlineData("worker", "app://-/index.html", "Codex", "ws://127.0.0.1:9335/devtools/page/x", false)]
    [InlineData("page", "https://example.com/index.html", "Codex", "ws://127.0.0.1:9335/devtools/page/x", false)]
    [InlineData("page", "app://-/settings.html", "Codex", "ws://127.0.0.1:9335/devtools/page/x", false)]
    [InlineData("page", "app://-/index.html", "Other", "ws://127.0.0.1:9335/devtools/page/x", false)]
    [InlineData("page", "app://-/index.html", "Codex", "ws://192.0.2.1:9335/devtools/page/x", false)]
    [InlineData("page", "app://-/index.html", "Codex", "ws://127.0.0.1:9336/devtools/page/x", false)]
    [InlineData("page", "app://-/index.html", "Codex", "ws://127.0.0.1:9335/not-devtools/x", false)]
    public void CdpTargetMatcher_AcceptsVersionDriftButRemainsLoopbackAndAppScoped(
        string type,
        string pageUrl,
        string title,
        string webSocketUrl,
        bool expected)
    {
        Assert.Equal(
            expected,
            CodexDesktopBridgeService.IsTrustedCodexPageTarget(
                type,
                pageUrl,
                title,
                webSocketUrl));
    }
}
