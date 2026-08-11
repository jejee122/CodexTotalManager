using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NativeProxySmoke;
using CodexOpenCodexNative.Config;
using CodexOpenCodexNative.Host;

var dataRoot = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "native-proxy-smoke-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataRoot);

var fake = new FakeUpstream();
var fakeTask = Task.Run(() => fake.Run());

var store = new NativeProxyConfigStore(dataRoot);
store.Save(new CodexOpenCodexNative.Models.NativeProxyConfig
{
    ListenPort = 19110,
    AdmissionToken = "smoke-token",
    DefaultProvider = "local-chat",
    Providers =
    [
        new()
        {
            Id = "local-chat",
            Name = "本地 chat 上游",
            Adapter = "openai-chat",
            BaseUrl = "http://127.0.0.1:18889/v1",
            ApiKey = "test-key-only",
            DefaultModel = "k3-test",
            Models = ["k3-test"]
        },
        new()
        {
            Id = "local-responses",
            Name = "本地 responses 上游",
            Adapter = "openai-responses",
            BaseUrl = "http://127.0.0.1:18889",
            ApiKey = "test-key-only",
            DefaultModel = "k3-test",
            Models = ["k3-test"]
        },
        new()
        {
            Id = "local-anthropic",
            Name = "本地 anthropic 上游",
            Adapter = "anthropic",
            BaseUrl = "http://127.0.0.1:18889",
            ApiKey = "test-key-only",
            DefaultModel = "claude-3-5-sonnet",
            Models = ["claude-3-5-sonnet"]
        },
        new()
        {
            Id = "local-google",
            Name = "本地 google 上游",
            Adapter = "google",
            BaseUrl = "http://127.0.0.1:18889",
            ApiKey = "test-key-only",
            DefaultModel = "gemini-2.0-flash",
            Models = ["gemini-2.0-flash"]
        }
    ]
});

var host = new NativeProxyHost(store, admissionTokenOverride: "smoke-token", dataRootOverride: dataRoot);
host.Application.Use(next => async (HttpContext context) =>
{
    try
    {
        await next(context);
    }
    catch (Exception ex)
    {
        File.AppendAllText(Path.Combine(dataRoot, "errors.log"),
            $"{DateTime.Now:O} {ex}\n\n");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("internal error");
    }
});
Console.WriteLine($"SMOKE_READY {host.ListenUrl} root={dataRoot}");
Console.WriteLine($"FAKE_UPSTREAM 18889");
await host.RunAsync();

Console.WriteLine("SMOKE_STOPPED");
