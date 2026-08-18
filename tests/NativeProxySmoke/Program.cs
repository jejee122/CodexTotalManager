using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using NativeProxySmoke;
using CodexOpenCodexNative.Config;
using CodexOpenCodexNative.Host;

if (args.Length == 4 && args[0] == "--config-update")
{
    var updateStore = new NativeProxyConfigStore(Path.GetFullPath(args[1]));
    var value = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
    updateStore.Update(config =>
    {
        if (args[2] == "auto") config.AutoSwitchThreshold = value;
        else if (args[2] == "failover") config.FailoverThreshold = value;
        else throw new InvalidOperationException("unknown config update field");
    });
    Console.WriteLine("NATIVE_CONFIG_CHILD_OK");
    return;
}

var ownsDataRoot = args.Length == 0;
var dataRoot = ownsDataRoot
    ? Path.Combine(Path.GetTempPath(), "native-proxy-smoke-" + Guid.NewGuid().ToString("N")[..8])
    : Path.GetFullPath(args[0]);
Directory.CreateDirectory(dataRoot);

static int GetFreeLoopbackPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
    finally { listener.Stop(); }
}

var nativePort = GetFreeLoopbackPort();
int upstreamPort;
do { upstreamPort = GetFreeLoopbackPort(); } while (upstreamPort == nativePort);

await using var fake = new FakeUpstream(upstreamPort);

var store = new NativeProxyConfigStore(dataRoot);
store.Save(new CodexOpenCodexNative.Models.NativeProxyConfig
{
    ListenPort = nativePort,
    AdmissionToken = "smoke-token",
    DefaultProvider = "local-chat",
    Providers =
    [
        new()
        {
            Id = "local-chat",
            Name = "本地 chat 上游",
            Adapter = "openai-chat",
            BaseUrl = $"http://127.0.0.1:{upstreamPort}/v1",
            ApiKey = "test-key-only",
            DefaultModel = "k3-test",
            Models = ["k3-test"]
        },
        new()
        {
            Id = "local-responses",
            Name = "本地 responses 上游",
            Adapter = "openai-responses",
            BaseUrl = $"http://127.0.0.1:{upstreamPort}",
            ApiKey = "test-key-only",
            DefaultModel = "k3-test",
            Models = ["k3-test"]
        },
        new()
        {
            Id = "local-anthropic",
            Name = "本地 anthropic 上游",
            Adapter = "anthropic",
            BaseUrl = $"http://127.0.0.1:{upstreamPort}",
            ApiKey = "test-key-only",
            DefaultModel = "claude-3-5-sonnet",
            Models = ["claude-3-5-sonnet"]
        },
        new()
        {
            Id = "local-google",
            Name = "本地 google 上游",
            Adapter = "google",
            BaseUrl = $"http://127.0.0.1:{upstreamPort}",
            ApiKey = "test-key-only",
            DefaultModel = "gemini-2.0-flash",
            Models = ["gemini-2.0-flash"]
        }
    ]
});

var updateAuto = RunConfigUpdateChildAsync(dataRoot, "auto", 61);
var updateFailover = RunConfigUpdateChildAsync(dataRoot, "failover", 3);
await Task.WhenAll(updateAuto, updateFailover);
var concurrentlyUpdated = store.Load();
if (concurrentlyUpdated.AutoSwitchThreshold != 61 || concurrentlyUpdated.FailoverThreshold != 3
    || concurrentlyUpdated.Revision < 3)
    throw new InvalidOperationException("跨进程配置更新丢失了其中一个字段。 ");

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
Console.WriteLine($"FAKE_UPSTREAM {upstreamPort}");
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
try
{
    await fake.StartAsync(deadline.Token);
    await host.StartAsync();

    using var handler = new SocketsHttpHandler { UseProxy = false };
    using var client = new HttpClient(handler)
    {
        BaseAddress = new Uri($"http://127.0.0.1:{nativePort}"),
        Timeout = TimeSpan.FromSeconds(15)
    };
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-CMM-Admission", "Bearer smoke-token");
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "smoke-token");

    await AssertGetAsync(client, "/readyz", "\"status\":\"ready\"", deadline.Token);
    await AssertPutAsync(client, "/api/codex-auth/auto-switch", new { threshold = 73 }, deadline.Token);
    await AssertPutAsync(client, "/api/codex-auth/failover", new { threshold = 4 }, deadline.Token);
    var persistedThresholds = store.Load();
    if (persistedThresholds.AutoSwitchThreshold != 73 || persistedThresholds.FailoverThreshold != 4)
        throw new InvalidOperationException("账号阈值没有持久化到原生引擎配置。");
    await AssertPostAsync(client, "/v1/chat/completions", new
    {
        model = "local-chat/k3-test",
        messages = new[] { new { role = "user", content = "smoke" } },
        stream = false
    }, "CUSTOM_MODEL_OK", deadline.Token);
    await AssertPostAsync(client, "/v1/chat/completions", new
    {
        model = "local-chat/k3-test",
        messages = new[] { new { role = "user", content = "stream smoke" } },
        stream = true
    }, "CUSTOM_MODEL_OK", deadline.Token);
    await AssertPostAsync(client, "/v1/responses", new
    {
        model = "local-responses/k3-test",
        input = "responses smoke",
        stream = true
    }, "RESPONSES_UPSTREAM_OK", deadline.Token);
    await AssertPostAsync(client, "/v1/responses", new
    {
        model = "local-anthropic/claude-3-5-sonnet",
        input = "anthropic smoke",
        stream = false
    }, "ANTHROPIC_UPSTREAM_OK", deadline.Token);
    await AssertPostAsync(client, "/v1/responses", new
    {
        model = "local-google/gemini-2.0-flash",
        input = "google smoke",
        stream = false
    }, "GOOGLE_UPSTREAM_OK", deadline.Token);

    Console.WriteLine($"NATIVE_PROXY_SMOKE_OK nativePort={nativePort} upstreamPort={upstreamPort} protocols=chat,responses,anthropic,google streaming=chat,responses thresholds=persisted configCrossProcess=passed");
}
finally
{
    await host.StopAsync(CancellationToken.None);
    await fake.StopAsync(CancellationToken.None);
    if (ownsDataRoot && Directory.Exists(dataRoot))
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(dataRoot).StartsWith("native-proxy-smoke-", StringComparison.Ordinal))
            throw new InvalidOperationException($"拒绝清理未验证的 smoke 临时目录：{dataRoot}");
        Directory.Delete(dataRoot, recursive: true);
    }
}

static async Task RunConfigUpdateChildAsync(string root, string field, int value)
{
    var processPath = Environment.ProcessPath
                      ?? throw new InvalidOperationException("Current process path is unavailable.");
    var start = new ProcessStartInfo(processPath)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--config-update");
    start.ArgumentList.Add(root);
    start.ArgumentList.Add(field);
    start.ArgumentList.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    using var child = Process.Start(start)
                      ?? throw new InvalidOperationException("Unable to start native config update child.");
    var outputTask = child.StandardOutput.ReadToEndAsync();
    var errorTask = child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
    var output = await outputTask;
    var error = await errorTask;
    if (child.ExitCode != 0 || !output.Contains("NATIVE_CONFIG_CHILD_OK", StringComparison.Ordinal))
        throw new InvalidOperationException(
            $"Config update child failed: field={field} exit={child.ExitCode} stderr={error}");
}

static async Task AssertGetAsync(HttpClient client, string path, string marker, CancellationToken cancellationToken)
{
    using var response = await client.GetAsync(path, cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode || !body.Contains(marker, StringComparison.Ordinal))
        throw new InvalidOperationException($"GET {path} smoke 失败：HTTP {(int)response.StatusCode} {body}");
}

static async Task AssertPostAsync(
    HttpClient client,
    string path,
    object payload,
    string marker,
    CancellationToken cancellationToken)
{
    using var response = await client.PostAsJsonAsync(path, payload, cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode || !body.Contains(marker, StringComparison.Ordinal))
        throw new InvalidOperationException($"POST {path} smoke 失败：HTTP {(int)response.StatusCode} {body}");
}

static async Task AssertPutAsync(
    HttpClient client,
    string path,
    object payload,
    CancellationToken cancellationToken)
{
    using var response = await client.PutAsJsonAsync(path, payload, cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode)
        throw new InvalidOperationException($"PUT {path} smoke 失败：HTTP {(int)response.StatusCode} {body}");
}
