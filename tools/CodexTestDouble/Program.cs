using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

static int ReadLoopbackPort(string variableName, int fallback)
{
    var value = Environment.GetEnvironmentVariable(variableName);
    if (string.IsNullOrWhiteSpace(value)) return fallback;
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
        || uri.Scheme != Uri.UriSchemeHttp
        || !uri.IsLoopback
        || uri.Port is < 1024 or > 65535
        || uri.AbsolutePath is not ("" or "/")
        || !string.IsNullOrEmpty(uri.Query)
        || !string.IsNullOrEmpty(uri.Fragment)
        || !string.IsNullOrEmpty(uri.UserInfo))
        throw new InvalidOperationException($"{variableName} must be a plain loopback HTTP URL.");
    return uri.Port;
}

var enginePort = ReadLoopbackPort("CMM_CODEX_TEST_DOUBLE_ENGINE_URL", 19100);
var gatewayPort = ReadLoopbackPort("CMM_CODEX_TEST_DOUBLE_GATEWAY_URL", 19110);
if (enginePort == gatewayPort)
    throw new InvalidOperationException("The test-double engine and gateway ports must be different.");
var runToken = Environment.GetEnvironmentVariable("CMM_CODEX_TEST_DOUBLE_TOKEN")?.Trim();
if (string.IsNullOrWhiteSpace(runToken)
    || runToken.Length is < 32 or > 128
    || runToken.Any(character => !Uri.IsHexDigit(character)))
{
    Console.Error.WriteLine("Codex Test Double requires CMM_CODEX_TEST_DOUBLE_TOKEN (32-128 hex characters).");
    return 64;
}

var runTokenSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runToken)));
var state = new TestDoubleState(enginePort, gatewayPort);
var startedAt = Stopwatch.StartNew();
var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
builder.Logging.ClearProviders();
builder.WebHost.UseUrls(
    $"http://127.0.0.1:{enginePort}",
    $"http://127.0.0.1:{gatewayPort}");
var app = builder.Build();

app.Use(async (context, next) =>
{
    var remote = context.Connection.RemoteIpAddress;
    if (remote is null || !IPAddress.IsLoopback(remote))
    {
        state.RecordNonLoopback();
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "loopback only" });
        return;
    }
    state.RecordRequest(context.Connection.LocalPort);
    context.Response.Headers.CacheControl = "no-store";
    await next();
});

app.MapGet("/", (HttpContext context) => Results.Text(
    TestDoublePage(context.Connection.LocalPort, enginePort, gatewayPort),
    "text/html; charset=utf-8"));

app.MapGet("/healthz", (HttpContext context) =>
    context.Connection.LocalPort != enginePort
        ? WrongPort(enginePort)
        : Results.Json(new
        {
            status = "ok",
            engine = "CodexOpenCodexNative",
            testDoubleService = "CodexTestDoubleEngine",
            testDouble = true,
            pid = Environment.ProcessId,
            port = enginePort,
            uptime = startedAt.Elapsed.TotalSeconds,
            runTokenSha256
        }));

app.MapGet("/health", (HttpContext context) =>
    context.Connection.LocalPort != gatewayPort
        ? WrongPort(gatewayPort)
        : Results.Json(new
        {
            status = "ok",
            product = "CodexTestDouble",
            productVersion = "1.0.0-test-only",
            service = "codex-test-double-gateway",
            testDoubleService = "CodexTestDoubleGateway",
            testDouble = true,
            routeGuardVersion = 4,
            pid = Environment.ProcessId,
            port = gatewayPort,
            routeCount = 2,
            configurationFingerprint = new string('0', 64),
            runTokenSha256
        }));

app.MapGet("/api/system/memory", (HttpContext context) =>
    context.Connection.LocalPort != enginePort
        ? WrongPort(enginePort)
        : Results.Json(new { rss = Process.GetCurrentProcess().WorkingSet64 }));

app.MapGet("/api/models", (HttpContext context) =>
    context.Connection.LocalPort != enginePort
        ? WrongPort(enginePort)
        : Results.Json(state.Models()));

app.MapGet("/api/providers", (HttpContext context) =>
    context.Connection.LocalPort != enginePort
        ? WrongPort(enginePort)
        : Results.Json(new object[]
        {
            new
            {
                name = "openai", baseUrl = "https://codex.invalid/test-only",
                adapter = "openai-responses", hasApiKey = false, disabled = false,
                codexAccountMode = "direct", testOnly = true
            },
            new
            {
                name = "fake-provider", baseUrl = $"http://127.0.0.1:{gatewayPort}/v1",
                adapter = "openai-responses", hasApiKey = false, disabled = false,
                codexAccountMode = "pool"
            }
        }));

app.MapMethods("/api/providers", new[] { "PATCH" }, (HttpContext context) =>
    context.Connection.LocalPort != enginePort
        ? WrongPort(enginePort)
        : Results.Json(new { ok = true, testOnly = true }));

app.MapPost("/api/providers/test", (HttpContext context) =>
    context.Connection.LocalPort != enginePort
        ? WrongPort(enginePort)
        : Results.Json(new { ok = true, message = "FAKE_PROVIDER_OK", testOnly = true }));

app.MapGet("/api/codex-auth/accounts", (HttpContext context) =>
    context.Connection.LocalPort != enginePort
        ? WrongPort(enginePort)
        : Results.Json(state.Accounts()));

app.MapGet("/api/codex-auth/active", (HttpContext context) =>
    context.Connection.LocalPort != enginePort
        ? WrongPort(enginePort)
        : Results.Json(state.ActiveAccount()));

app.MapPut("/api/codex-auth/active", async (HttpContext context) =>
{
    if (context.Connection.LocalPort != enginePort) return WrongPort(enginePort);
    using var body = await JsonDocument.ParseAsync(context.Request.Body);
    var accountId = ReadString(body.RootElement, "accountId");
    if (accountId is not "fake-account-a" and not "fake-account-b")
        return Results.BadRequest(new { error = "unknown fake account" });
    state.SetActiveAccount(accountId);
    return Results.Json(new { ok = true, accountId, testOnly = true });
});

app.MapPut("/api/codex-auth/auto-switch", async (HttpContext context) =>
{
    if (context.Connection.LocalPort != enginePort) return WrongPort(enginePort);
    using var body = await JsonDocument.ParseAsync(context.Request.Body);
    var threshold = ReadInt(body.RootElement, "threshold");
    if (threshold is < 0 or > 100) return Results.BadRequest(new { error = "invalid threshold" });
    state.SetAutoSwitch(threshold);
    return Results.Json(new { ok = true, threshold, testOnly = true });
});

app.MapPut("/api/codex-auth/failover", async (HttpContext context) =>
{
    if (context.Connection.LocalPort != enginePort) return WrongPort(enginePort);
    using var body = await JsonDocument.ParseAsync(context.Request.Body);
    var threshold = ReadInt(body.RootElement, "threshold");
    if (threshold is < 0 or > 20) return Results.BadRequest(new { error = "invalid threshold" });
    state.SetFailover(threshold);
    return Results.Json(new { ok = true, threshold, testOnly = true });
});

app.MapGet("/api/combos", (HttpContext context) =>
    context.Connection.LocalPort != enginePort
        ? WrongPort(enginePort)
        : Results.Json(state.Combos()));

app.MapPut("/api/combos", async (HttpContext context) =>
{
    if (context.Connection.LocalPort != enginePort) return WrongPort(enginePort);
    using var body = await JsonDocument.ParseAsync(context.Request.Body);
    if (!body.RootElement.TryGetProperty("combo", out var combo)
        || !combo.TryGetProperty("targets", out var targets)
        || targets.ValueKind != JsonValueKind.Array)
        return Results.BadRequest(new { error = "invalid fake combo" });
    var target = targets.EnumerateArray().FirstOrDefault();
    var provider = ReadString(target, "provider");
    var model = ReadString(target, "model");
    if (!state.IsKnownTarget(provider, model))
        return Results.BadRequest(new { error = "unknown fake route" });
    state.SetRoute(provider!, model!);
    return Results.Json(new { ok = true, provider, model, testOnly = true });
});

app.MapGet("/api/logs", (HttpContext context) =>
    context.Connection.LocalPort != enginePort
        ? WrongPort(enginePort)
        : Results.Json(new object[]
        {
            new
            {
                timestamp = DateTimeOffset.UtcNow,
                requestedModel = "cmm/main",
                resolvedProvider = "fake-provider",
                resolvedModel = state.CurrentModel,
                accountId = state.ActiveAccountId,
                status = 200,
                inputTokens = 4,
                outputTokens = 2,
                totalTokens = 6,
                testOnly = true
            }
        }));

app.MapGet("/api/test/report", (HttpContext context) =>
    context.Connection.LocalPort != enginePort
        ? WrongPort(enginePort)
        : Results.Json(state.Report()));

app.MapGet("/api/ui/state", (HttpContext context) =>
    context.Connection.LocalPort != enginePort
        ? WrongPort(enginePort)
        : Results.Json(state.UiState()));

app.MapPut("/api/ui/model", async (HttpContext context) =>
{
    if (context.Connection.LocalPort != enginePort) return WrongPort(enginePort);
    using var body = await JsonDocument.ParseAsync(context.Request.Body);
    var model = ReadString(body.RootElement, "model");
    if (!state.IsKnownModel(model)) return Results.BadRequest(new { error = "unknown fake model" });
    state.SetRoute(state.ProviderForModel(model!), model!);
    return Results.Json(new
    {
        ok = true,
        model,
        message = $"已切换到 {state.ModelDisplayName(model!)}（测试替身）",
        testOnly = true
    });
});

app.MapPost("/api/ui/chat", async (HttpContext context) =>
{
    if (context.Connection.LocalPort != enginePort) return WrongPort(enginePort);
    using var body = await JsonDocument.ParseAsync(context.Request.Body);
    var prompt = ReadString(body.RootElement, "prompt")?.Trim();
    if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 2000)
        return Results.BadRequest(new { error = "prompt must contain 1-2000 characters" });
    return Results.Json(state.Chat(prompt));
});

app.MapGet("/api/skins", (HttpContext context) =>
    context.Connection.LocalPort != enginePort
        ? WrongPort(enginePort)
        : Results.Json(state.Skins()));

app.MapPost("/api/skins/session", (HttpContext context) =>
{
    if (context.Connection.LocalPort != enginePort) return WrongPort(enginePort);
    state.RecordLiveSession();
    return Results.Json(new
    {
        ok = true,
        status = "Success",
        message = "假 Codex 在线换肤通道已连接。",
        testOnly = true
    });
});

app.MapPut("/api/skins/active", async (HttpContext context) =>
{
    if (context.Connection.LocalPort != enginePort) return WrongPort(enginePort);
    using var body = await JsonDocument.ParseAsync(context.Request.Body);
    var themeId = ReadString(body.RootElement, "themeId");
    if (!state.TrySetSkin(themeId, out var themeName))
        return Results.BadRequest(new { error = "unknown fake skin" });
    return Results.Json(new
    {
        ok = true,
        status = "Success",
        themeId,
        themeName,
        message = themeId == "official"
            ? "已恢复假 Codex 官方外观。"
            : $"已在线切换为“{themeName}”。",
        testOnly = true
    });
});

app.MapPost("/api/skins/community", async (HttpContext context) =>
{
    if (context.Connection.LocalPort != enginePort) return WrongPort(enginePort);
    using var body = await JsonDocument.ParseAsync(context.Request.Body);
    var uri = ReadString(body.RootElement, "uri")?.Trim();
    if (!state.TryApplyCommunitySkin(uri, out var themeId, out var themeName))
        return Results.BadRequest(new { error = "invalid dreamskin test URI" });
    return Results.Json(new
    {
        ok = true,
        status = "Success",
        themeId,
        themeName,
        message = $"模拟在线皮肤“{themeName}”已经下载、校验并即时应用。",
        testOnly = true,
        networkUsed = false
    });
});

app.MapGet("/v1/models", (HttpContext context) =>
    context.Connection.LocalPort != gatewayPort
        ? WrongPort(gatewayPort)
        : Results.Json(state.GatewayModels()));

app.MapPost("/v1/responses", async (HttpContext context) =>
{
    if (context.Connection.LocalPort != gatewayPort) return WrongPort(gatewayPort);
    using var body = await JsonDocument.ParseAsync(context.Request.Body);
    state.RecordFixedResponse();
    return Results.Json(new
    {
        id = "resp_codex_test_double",
        @object = "response",
        status = "completed",
        model = ReadString(body.RootElement, "model") ?? "fake-model-a",
        output = new object[]
        {
            new
            {
                type = "message", id = "msg_codex_test_double", status = "completed", role = "assistant",
                content = new object[]
                {
                    new { type = "output_text", text = state.ApiResponseText(), annotations = Array.Empty<object>() }
                }
            }
        },
        usage = new { input_tokens = 4, output_tokens = 2, total_tokens = 6 },
        testOnly = true
    });
});

app.MapPost("/v1/chat/completions", async (HttpContext context) =>
{
    if (context.Connection.LocalPort != gatewayPort) return WrongPort(gatewayPort);
    using var body = await JsonDocument.ParseAsync(context.Request.Body);
    state.RecordFixedResponse();
    return Results.Json(new
    {
        id = "chatcmpl_codex_test_double",
        @object = "chat.completion",
        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        model = ReadString(body.RootElement, "model") ?? "fake-model-a",
        choices = new object[]
        {
            new
            {
                index = 0,
                message = new { role = "assistant", content = state.ApiResponseText() },
                finish_reason = "stop"
            }
        },
        usage = new { prompt_tokens = 4, completion_tokens = 2, total_tokens = 6 },
        testOnly = true
    });
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"CODEX_TEST_DOUBLE_READY engine=http://127.0.0.1:{enginePort} gateway=http://127.0.0.1:{gatewayPort}");
});
await app.RunAsync();
return 0;

static IResult WrongPort(int expectedPort) =>
    Results.Json(new { error = $"test endpoint is only available on 127.0.0.1:{expectedPort}" }, statusCode: 404);

static string? ReadString(JsonElement root, string name) =>
    root.ValueKind == JsonValueKind.Object
    && root.TryGetProperty(name, out var value)
    && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

static int ReadInt(JsonElement root, string name) =>
    root.ValueKind == JsonValueKind.Object
    && root.TryGetProperty(name, out var value)
    && value.TryGetInt32(out var parsed)
        ? parsed
        : -1;

static string TestDoublePage(int localPort, int enginePortValue, int gatewayPortValue)
{
    if (localPort != enginePortValue)
        return $$"""
        <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Codex Test Gateway</title><style>body{font-family:Segoe UI,Microsoft YaHei,sans-serif;background:#0b1020;color:#eef4ff;margin:0;display:grid;place-items:center;min-height:100vh}.card{max-width:620px;padding:34px;border:1px solid #2d3c61;border-radius:24px;background:#151d31;box-shadow:0 30px 80px #0008}a{color:#83e1be}code{color:#ffd27a}</style></head>
        <body><div class="card"><h1>Codex 测试网关</h1><p>假网关正在 <code>127.0.0.1:{{gatewayPortValue}}</code> 运行。</p><p>可操作的假 Codex 界面位于假引擎端口。</p><p><a href="http://127.0.0.1:{{enginePortValue}}/">打开假 Codex 工作台</a></p></div></body></html>
        """;

    return """
    <!doctype html>
    <html lang="zh-CN">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width,initial-scale=1">
      <title>Codex Desktop Test Double</title>
      <style>
        :root{--bg:#07111f;--surface:#0e1c2d;--surface2:#14263a;--accent:#6de0b5;--text:#eef7ff;--muted:#93a9bd;--line:#294057;--danger:#ffb86c}
        *{box-sizing:border-box}body{margin:0;background:radial-gradient(circle at 82% 8%,color-mix(in srgb,var(--accent) 14%,transparent),transparent 34%),var(--bg);color:var(--text);font-family:Inter,Segoe UI,Microsoft YaHei,sans-serif;min-height:100vh;transition:background .35s,color .35s}.app{display:grid;grid-template-columns:250px minmax(0,1fr);min-height:100vh}.side{border-right:1px solid var(--line);background:color-mix(in srgb,var(--surface) 92%,transparent);padding:24px 16px;display:flex;flex-direction:column;gap:22px}.brand{display:flex;align-items:center;gap:12px;padding:0 8px}.mark{width:38px;height:38px;border-radius:12px;background:linear-gradient(135deg,var(--accent),#5790ff);display:grid;place-items:center;color:#07111f;font-weight:900;box-shadow:0 10px 30px color-mix(in srgb,var(--accent) 35%,transparent)}.brand strong{display:block}.brand small,.muted{color:var(--muted)}nav{display:grid;gap:7px}.nav{border:0;background:transparent;color:var(--muted);text-align:left;padding:12px 14px;border-radius:12px;font:inherit;cursor:pointer}.nav:hover,.nav.active{background:var(--surface2);color:var(--text)}.guard{margin-top:auto;padding:14px;border:1px solid color-mix(in srgb,var(--danger) 45%,var(--line));border-radius:14px;background:color-mix(in srgb,var(--danger) 8%,var(--surface));font-size:12px;line-height:1.6}.dot{display:inline-block;width:8px;height:8px;border-radius:50%;background:var(--accent);box-shadow:0 0 16px var(--accent);margin-right:7px}.main{padding:24px 30px 38px;min-width:0}.top{display:flex;justify-content:space-between;align-items:center;gap:20px;margin-bottom:24px}.top h1{font-size:20px;margin:0}.model-pill{display:flex;align-items:center;gap:10px;padding:8px 12px;border:1px solid var(--line);background:var(--surface);border-radius:12px}.model-pill select{border:0;background:transparent;color:var(--text);font:inherit;outline:none;max-width:260px}.model-pill option{background:var(--surface);color:var(--text)}.page{display:none}.page.active{display:block}.chat-shell{height:calc(100vh - 112px);display:grid;grid-template-rows:auto 1fr auto;border:1px solid var(--line);border-radius:22px;background:color-mix(in srgb,var(--surface) 88%,transparent);overflow:hidden}.chat-head{padding:18px 22px;border-bottom:1px solid var(--line);display:flex;justify-content:space-between;align-items:center}.chat-head h2{margin:0;font-size:17px}.messages{padding:26px;overflow:auto;display:flex;flex-direction:column;gap:16px}.msg{max-width:min(760px,84%);padding:14px 17px;border-radius:17px;line-height:1.65;white-space:pre-wrap}.msg.assistant{background:var(--surface2);border:1px solid var(--line);align-self:flex-start}.msg.user{background:var(--accent);color:#061018;align-self:flex-end}.composer{display:grid;grid-template-columns:1fr auto;gap:12px;padding:18px;border-top:1px solid var(--line)}textarea{resize:none;min-height:54px;max-height:140px;border:1px solid var(--line);background:var(--bg);color:var(--text);border-radius:14px;padding:14px;font:inherit;outline:none}textarea:focus{border-color:var(--accent)}button.primary{border:0;border-radius:14px;padding:0 22px;background:var(--accent);color:#061018;font-weight:800;cursor:pointer}.title-row{display:flex;justify-content:space-between;align-items:end;margin:8px 0 20px}.title-row h2{margin:0}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:16px}.card{position:relative;border:1px solid var(--line);border-radius:18px;background:var(--surface);padding:20px;overflow:hidden}.card.active{border-color:var(--accent);box-shadow:0 0 0 1px var(--accent) inset}.card h3{margin:0 0 8px}.card p{color:var(--muted);line-height:1.55;min-height:48px}.meta{display:flex;gap:8px;flex-wrap:wrap;margin:14px 0}.tag{font-size:12px;border:1px solid var(--line);border-radius:999px;padding:5px 9px;color:var(--muted)}.card button{width:100%;border:1px solid var(--line);border-radius:11px;background:var(--surface2);color:var(--text);padding:10px;cursor:pointer;font:inherit}.card button:hover{border-color:var(--accent)}.card button:disabled{opacity:.65;cursor:default}.skin-preview{height:112px;border-radius:13px;margin-bottom:16px;position:relative;overflow:hidden}.skin-preview:after{content:'';position:absolute;inset:17px 22px;border-radius:10px;border:1px solid #ffffff25;background:#ffffff0b;box-shadow:22px 18px 0 -8px #ffffff0b}.online{position:absolute;top:12px;right:12px;background:#07111fcc;border:1px solid #ffffff26;border-radius:999px;padding:5px 8px;font-size:11px}.status-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:15px}.metric{font-size:28px;font-weight:800;margin:9px 0}.notice{padding:14px 16px;border:1px solid color-mix(in srgb,var(--danger) 45%,var(--line));background:color-mix(in srgb,var(--danger) 7%,var(--surface));border-radius:14px;color:var(--danger)}#toast{position:fixed;right:24px;bottom:24px;background:var(--text);color:var(--bg);padding:12px 16px;border-radius:12px;opacity:0;transform:translateY(8px);transition:.2s;pointer-events:none;box-shadow:0 16px 45px #0008}#toast.show{opacity:1;transform:none}@media(max-width:760px){.app{grid-template-columns:1fr}.side{display:none}.main{padding:16px}.chat-shell{height:calc(100vh - 90px)}.top h1{display:none}}
      </style>
    </head>
    <body>
      <div class="app">
        <aside class="side">
          <div class="brand"><div class="mark">C</div><div><strong>Codex</strong><small>Desktop Test Double</small></div></div>
          <nav>
            <button class="nav active" data-page="chat">✦ 新对话</button>
            <button class="nav" data-page="models">◫ 模型</button>
            <button class="nav" data-page="skins">◈ 在线皮肤</button>
            <button class="nav" data-page="status">◎ 运行状态</button>
          </nav>
          <div class="guard"><div><span class="dot"></span>本机测试替身在线</div><div>不登录 · 不扣费 · 不访问外网</div><div>关闭程序后状态全部消失</div></div>
        </aside>
        <main class="main">
          <header class="top"><h1 id="pageTitle">新对话</h1><label class="model-pill"><span class="dot"></span><select id="modelSelect" aria-label="选择测试模型"></select></label></header>
          <section class="page active" id="page-chat">
            <div class="chat-shell"><div class="chat-head"><div><h2>今天想测试什么？</h2><span class="muted" id="chatModel"></span></div><span class="tag">TEST ONLY</span></div><div class="messages" id="messages"></div><form class="composer" id="composer"><textarea id="prompt" placeholder="给假 Codex 发一条消息……"></textarea><button class="primary" type="submit">发送</button></form></div>
          </section>
          <section class="page" id="page-models"><div class="title-row"><div><h2>模型切换</h2><div class="muted">切换后，聊天和总管家读到的当前模型会一起变化。</div></div></div><div class="grid" id="modelGrid"></div></section>
          <section class="page" id="page-skins"><div class="title-row"><div><h2>在线皮肤库</h2><div class="muted">模拟在线获取、校验和即时应用；实际不会访问互联网。</div></div></div><div class="notice">这里的“在线”是完整流程模拟：下载结果由本机替身提供，不会真的访问 DreamSkin 网站。</div><div class="grid" id="skinGrid" style="margin-top:16px"></div></section>
          <section class="page" id="page-status"><div class="title-row"><div><h2>隔离运行状态</h2><div class="muted">只显示这个测试替身自己的数据。</div></div></div><div class="status-grid" id="statusGrid"></div></section>
        </main>
      </div>
      <div id="toast"></div>
      <script>
        const titles={chat:'新对话',models:'模型切换',skins:'在线皮肤',status:'运行状态'};let snapshot=null;
        const esc=value=>String(value??'').replace(/[&<>"']/g,char=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
        async function api(path,options={}){const response=await fetch(path,{...options,headers:{'Content-Type':'application/json',...(options.headers||{})}});const data=await response.json();if(!response.ok)throw new Error(data.error||`HTTP ${response.status}`);return data}
        function toast(text){const node=document.getElementById('toast');node.textContent=text;node.classList.add('show');setTimeout(()=>node.classList.remove('show'),2200)}
        function applySkin(){const skin=snapshot.skins.find(item=>item.id===snapshot.activeSkinId)||snapshot.skins[0];if(!skin)return;for(const [key,value] of Object.entries({bg:skin.background,surface:skin.surface,surface2:skin.surface2,accent:skin.accent,text:skin.text,line:skin.line}))document.documentElement.style.setProperty(`--${key}`,value)}
        function renderMessages(){const box=document.getElementById('messages');box.innerHTML='';for(const item of snapshot.messages){const node=document.createElement('div');node.className=`msg ${item.role}`;node.textContent=item.content;box.appendChild(node)}box.scrollTop=box.scrollHeight}
        function renderModels(){const select=document.getElementById('modelSelect');select.innerHTML=snapshot.models.map(item=>`<option value="${esc(item.id)}" ${item.id===snapshot.currentModel?'selected':''}>${esc(item.displayName)}</option>`).join('');document.getElementById('chatModel').textContent=`当前模型：${snapshot.currentModelName}`;document.getElementById('modelGrid').innerHTML=snapshot.models.map(item=>`<article class="card ${item.id===snapshot.currentModel?'active':''}"><h3>${esc(item.displayName)}</h3><p>${esc(item.description)}</p><div class="meta"><span class="tag">${esc(item.context)}</span><span class="tag">${esc(item.speed)}</span><span class="tag">无真实推理</span></div><button data-model="${esc(item.id)}" ${item.id===snapshot.currentModel?'disabled':''}>${item.id===snapshot.currentModel?'正在使用':'切换到此模型'}</button></article>`).join('');document.querySelectorAll('[data-model]').forEach(button=>button.onclick=()=>switchModel(button.dataset.model))}
        function renderSkins(){document.getElementById('skinGrid').innerHTML=snapshot.skins.map(item=>`<article class="card ${item.id===snapshot.activeSkinId?'active':''}"><div class="skin-preview" style="background:linear-gradient(135deg,${item.background},${item.accent})">${item.online?'<span class="online">ONLINE</span>':''}</div><h3>${esc(item.name)}</h3><p>${esc(item.description)}</p><div class="meta"><span class="tag">${item.isDynamic?'动态':'静态'}</span><span class="tag">${item.online?'在线库':'内置'}</span></div><button data-skin="${esc(item.id)}" ${item.id===snapshot.activeSkinId?'disabled':''}>${item.id===snapshot.activeSkinId?'正在使用':'立即应用'}</button></article>`).join('');document.querySelectorAll('[data-skin]').forEach(button=>button.onclick=()=>switchSkin(button.dataset.skin))}
        function renderStatus(){const s=snapshot.stats;document.getElementById('statusGrid').innerHTML=`<article class="card"><span class="muted">当前模型</span><div class="metric" style="font-size:19px">${esc(snapshot.currentModelName)}</div></article><article class="card"><span class="muted">当前皮肤</span><div class="metric" style="font-size:19px">${esc(snapshot.activeSkinName)}</div></article><article class="card"><span class="muted">假请求</span><div class="metric">${s.totalRequests}</div></article><article class="card"><span class="muted">模型切换</span><div class="metric">${s.modelSwitches}</div></article><article class="card"><span class="muted">皮肤切换</span><div class="metric">${s.skinSwitches}</div></article><article class="card"><span class="muted">外部网络请求</span><div class="metric">0</div></article>`}
        async function refresh(){snapshot=await api('/api/ui/state');applySkin();renderMessages();renderModels();renderSkins();renderStatus()}
        async function switchModel(model){const result=await api('/api/ui/model',{method:'PUT',body:JSON.stringify({model})});await refresh();toast(result.message)}
        async function switchSkin(themeId){let result;if(themeId==='gallery-celestial')result=await api('/api/skins/community',{method:'POST',body:JSON.stringify({uri:'dreamskin://apply?version=ver_celestial2026'})});else result=await api('/api/skins/active',{method:'PUT',body:JSON.stringify({themeId})});await refresh();toast(result.message)}
        document.querySelectorAll('.nav').forEach(button=>button.onclick=()=>{document.querySelectorAll('.nav').forEach(item=>item.classList.remove('active'));document.querySelectorAll('.page').forEach(item=>item.classList.remove('active'));button.classList.add('active');document.getElementById(`page-${button.dataset.page}`).classList.add('active');document.getElementById('pageTitle').textContent=titles[button.dataset.page]});
        document.getElementById('modelSelect').onchange=event=>switchModel(event.target.value);
        document.getElementById('composer').onsubmit=async event=>{event.preventDefault();const input=document.getElementById('prompt');const prompt=input.value.trim();if(!prompt)return;input.value='';try{await api('/api/ui/chat',{method:'POST',body:JSON.stringify({prompt})});await refresh()}catch(error){toast(error.message)}};
        const requestedPage=new URLSearchParams(location.search).get('page');if(titles[requestedPage])document.querySelector(`.nav[data-page="${requestedPage}"]`).click();
        refresh().catch(error=>toast(`加载失败：${error.message}`));setInterval(()=>{if(document.getElementById('page-status').classList.contains('active'))refresh().catch(()=>{})},2000);
      </script>
    </body>
    </html>
    """;
}

sealed class TestDoubleState
{
    private readonly int _enginePort;
    private readonly int _gatewayPort;

    public TestDoubleState(int enginePort, int gatewayPort)
    {
        _enginePort = enginePort;
        _gatewayPort = gatewayPort;
    }

    private static readonly FakeModel[] ModelCatalog =
    {
        new("openai", "gpt-5.6-sol", "Codex Pro Official（测试）", "模拟 Codex 自带 Pro 官方线路。", 200_000, "官方", "标准"),
        new("fake-provider", "fake-model-a", "Codex Test Sol", "模拟高能力自建号池模型，适合验证复杂任务、长上下文和模型路由。", 200_000, "深度", "标准"),
        new("fake-provider", "fake-model-b", "Codex Test Terra", "模拟均衡自建号池模型，适合验证日常对话、编辑和快速切换。", 128_000, "均衡", "快速"),
        new("fake-provider", "fake-model-mini", "Codex Test Mini", "模拟轻量自建号池模型，适合验证低延迟界面和大批量并发请求。", 64_000, "轻量", "极速")
    };

    private static readonly FakeSkin[] SkinCatalog =
    {
        new("official", "Codex 官方测试外观", "干净、克制的浅色工作区。", false, false,
            "#f3f5f8", "#ffffff", "#e9eef4", "#3fa67a", "#17212b", "#d4dde7"),
        new("aurora-night", "极光夜航", "深海蓝背景与柔和极光，适合长时间代码工作。", false, true,
            "#07111f", "#0e1c2d", "#14263a", "#6de0b5", "#eef7ff", "#294057"),
        new("paper-light", "纸页晨光", "暖白纸张质感与低对比度边框。", false, false,
            "#eee8dc", "#faf7f0", "#e5ded0", "#b26f54", "#2c2925", "#cfc5b6"),
        new("neon-grid", "霓虹网格", "高对比霓虹色和动态网格氛围。", true, true,
            "#080814", "#111126", "#191936", "#b56cff", "#f6edff", "#39305b"),
        new("gallery-celestial", "天宫云海 Online", "模拟从在线皮肤库下载、校验并即时应用的动态主题。", true, true,
            "#111827", "#18243a", "#22314b", "#f0b96b", "#fff7e7", "#43516b")
    };

    private readonly object _gate = new();
    private readonly List<FakeChatMessage> _messages = new()
    {
        new("assistant", "你好，我是本机 Codex 测试替身。你可以切换模型、发送假消息，或者去在线皮肤页即时换肤。")
    };
    private long _totalRequests;
    private long _engineRequests;
    private long _gatewayRequests;
    private long _nonLoopbackRequests;
    private long _fixedResponses;
    private long _mutations;
    private long _modelSwitches;
    private long _skinSwitches;
    private long _chatTurns;
    private string _activeAccountId = "fake-account-a";
    private int _autoSwitchThreshold = 80;
    private int _failoverThreshold = 3;
    private string _provider = "openai";
    private string _model = "gpt-5.6-sol";
    private string _activeSkinId = "aurora-night";
    private bool _liveSessionConnected = true;
    private readonly string _taskId = "fake-task-001";

    public string ActiveAccountId { get { lock (_gate) return _activeAccountId; } }
    public string CurrentProvider { get { lock (_gate) return _provider; } }
    public string CurrentModel { get { lock (_gate) return _model; } }
    public string CurrentSkinId { get { lock (_gate) return _activeSkinId; } }

    public bool IsKnownModel(string? model) =>
        !string.IsNullOrWhiteSpace(model)
        && ModelCatalog.Any(item => item.Id.Equals(model, StringComparison.Ordinal));

    public bool IsKnownTarget(string? provider, string? model) =>
        !string.IsNullOrWhiteSpace(provider)
        && !string.IsNullOrWhiteSpace(model)
        && ModelCatalog.Any(item => item.Provider.Equals(provider, StringComparison.Ordinal)
                                    && item.Id.Equals(model, StringComparison.Ordinal));

    public string ProviderForModel(string model) =>
        ModelCatalog.First(item => item.Id.Equals(model, StringComparison.Ordinal)).Provider;

    public string ModelDisplayName(string model) =>
        ModelCatalog.FirstOrDefault(item => item.Id.Equals(model, StringComparison.Ordinal))?.DisplayName
        ?? model;

    public object[] Models() => ModelCatalog.Select(item => new
    {
        provider = item.Provider,
        id = item.Id,
        namespaced = $"{item.Provider}/{item.Id}",
        displayName = item.DisplayName,
        description = item.Description,
        disabled = false,
        native = false,
        contextWindow = item.ContextWindow,
        testOnly = true
    }).Cast<object>().ToArray();

    public object GatewayModels() => new
    {
        @object = "list",
        data = ModelCatalog.Select(item => new
        {
            id = item.Id,
            @object = "model",
            owned_by = item.Provider,
            display_name = item.DisplayName,
            context_window = item.ContextWindow
        }).ToArray()
    };

    public void RecordRequest(int localPort)
    {
        Interlocked.Increment(ref _totalRequests);
        if (localPort == _enginePort) Interlocked.Increment(ref _engineRequests);
        if (localPort == _gatewayPort) Interlocked.Increment(ref _gatewayRequests);
    }

    public void RecordNonLoopback() => Interlocked.Increment(ref _nonLoopbackRequests);
    public void RecordFixedResponse() => Interlocked.Increment(ref _fixedResponses);

    public string ApiResponseText()
    {
        var name = ModelDisplayName(CurrentModel);
        return $"FAKE_CODEX_RESPONSE · {name} · 本回复由本机测试替身生成，没有调用真实模型。";
    }

    public object Chat(string prompt)
    {
        lock (_gate)
        {
            var modelName = ModelDisplayName(_model);
            var previousUserPrompt = _messages
                .LastOrDefault(item => item.Role.Equals("user", StringComparison.Ordinal))
                ?.Content;
            var response = _model switch
            {
                "gpt-5.6-sol" => $"Codex Pro Official（测试）已记住当前任务内容：“{prompt}”。这是官方线路模拟，不会调用真实账号。",
                "fake-model-a" => $"我已用 {modelName} 模拟完成深度分析。你发送的是：“{prompt}”。这是本机固定逻辑回复，没有真实推理或扣费。",
                "fake-model-b" => $"{modelName} 已快速处理：“{prompt}”。模型切换和聊天状态回路正常，但内容仍是模拟结果。",
                _ => $"{modelName} 极速响应：“{prompt}”。这是用于压力测试的轻量假回复。"
            };
            if (_model == "fake-model-b" && !string.IsNullOrWhiteSpace(previousUserPrompt))
                response = $"{modelName} still sees the user message from before the switch: {previousUserPrompt}";
            _messages.Add(new FakeChatMessage("user", prompt));
            _messages.Add(new FakeChatMessage("assistant", response));
            if (_messages.Count > 31) _messages.RemoveRange(1, _messages.Count - 31);
            Interlocked.Increment(ref _fixedResponses);
            Interlocked.Increment(ref _chatTurns);
            Interlocked.Increment(ref _mutations);
            return new
            {
                ok = true,
                response,
                provider = _provider,
                model = _model,
                modelName,
                taskId = _taskId,
                messageCount = _messages.Count,
                contextPreserved = !string.IsNullOrWhiteSpace(previousUserPrompt),
                rememberedUserPrompt = previousUserPrompt,
                testOnly = true
            };
        }
    }

    public object Accounts()
    {
        lock (_gate)
        {
            return new
            {
                accounts = new object[]
                {
                    Account("fake-account-a", "fake-a@example.invalid", true),
                    Account("fake-account-b", "fake-b@example.invalid", false)
                },
                testOnly = true
            };
        }
    }

    public object ActiveAccount()
    {
        lock (_gate)
            return new
            {
                activeCodexAccountId = _activeAccountId,
                autoSwitchThreshold = _autoSwitchThreshold,
                upstreamFailoverThreshold = _failoverThreshold,
                testOnly = true
            };
    }

    public object Combos()
    {
        lock (_gate)
            return new
            {
                combos = new object[]
                {
                    new
                    {
                        id = "cmm-switch",
                        alias = "cmm/main",
                        strategy = "failover",
                        targets = new object[] { new { provider = _provider, model = _model, weight = 1 } }
                    }
                },
                testOnly = true
            };
    }

    public object Skins()
    {
        lock (_gate)
        {
            var active = SkinCatalog.First(item => item.Id == _activeSkinId);
            return new
            {
                engineReady = true,
                managerScriptTrusted = true,
                liveSessionConnected = _liveSessionConnected,
                isPaused = _activeSkinId == "official",
                engineVersion = "1.0.0-test-only",
                activeThemeId = active.Id,
                activeThemeName = active.Name,
                themes = SkinCatalog.Select(item => SkinView(item, item.Id == _activeSkinId)).ToArray(),
                testOnly = true,
                networkUsed = false
            };
        }
    }

    public object UiState()
    {
        lock (_gate)
        {
            var activeSkin = SkinCatalog.First(item => item.Id == _activeSkinId);
            return new
            {
                currentModel = _model,
                currentProvider = _provider,
                currentModelName = ModelDisplayName(_model),
                models = ModelCatalog.Select(item => new
                {
                    provider = item.Provider,
                    id = item.Id,
                    displayName = item.DisplayName,
                    description = item.Description,
                    context = $"{item.ContextWindow / 1000}K 上下文",
                    capability = item.Capability,
                    speed = item.Speed
                }).ToArray(),
                activeSkinId = activeSkin.Id,
                activeSkinName = activeSkin.Name,
                skins = SkinCatalog.Select(item => SkinView(item, item.Id == _activeSkinId)).ToArray(),
                messages = _messages.Select(item => new { role = item.Role, content = item.Content }).ToArray(),
                taskId = _taskId,
                messageCount = _messages.Count,
                conversationFingerprint = ConversationFingerprint(),
                codexRestartCount = 0,
                stats = new
                {
                    totalRequests = Interlocked.Read(ref _totalRequests),
                    modelSwitches = Interlocked.Read(ref _modelSwitches),
                    skinSwitches = Interlocked.Read(ref _skinSwitches),
                    chatTurns = Interlocked.Read(ref _chatTurns),
                    outboundRequests = 0
                },
                ports = new { engine = _enginePort, gateway = _gatewayPort },
                testOnly = true
            };
        }
    }

    public void SetActiveAccount(string value)
    {
        lock (_gate) _activeAccountId = value;
        Interlocked.Increment(ref _mutations);
    }

    public void SetAutoSwitch(int value)
    {
        lock (_gate) _autoSwitchThreshold = value;
        Interlocked.Increment(ref _mutations);
    }

    public void SetFailover(int value)
    {
        lock (_gate) _failoverThreshold = value;
        Interlocked.Increment(ref _mutations);
    }

    public void SetRoute(string provider, string model)
    {
        lock (_gate)
        {
            _provider = provider;
            _model = model;
        }
        Interlocked.Increment(ref _mutations);
        Interlocked.Increment(ref _modelSwitches);
    }

    public void RecordLiveSession()
    {
        lock (_gate) _liveSessionConnected = true;
        Interlocked.Increment(ref _mutations);
    }

    public bool TrySetSkin(string? themeId, out string themeName)
    {
        var theme = SkinCatalog.FirstOrDefault(item => item.Id.Equals(themeId, StringComparison.Ordinal));
        if (theme is null)
        {
            themeName = string.Empty;
            return false;
        }
        lock (_gate)
        {
            _activeSkinId = theme.Id;
            _liveSessionConnected = true;
        }
        themeName = theme.Name;
        Interlocked.Increment(ref _mutations);
        Interlocked.Increment(ref _skinSwitches);
        return true;
    }

    public bool TryApplyCommunitySkin(string? uri, out string themeId, out string themeName)
    {
        const string prefix = "dreamskin://apply?version=ver_";
        var version = uri is not null && uri.StartsWith(prefix, StringComparison.Ordinal)
            ? uri[prefix.Length..]
            : string.Empty;
        if (version.Length is < 8 or > 64
            || version.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')))
        {
            themeId = string.Empty;
            themeName = string.Empty;
            return false;
        }
        themeId = "gallery-celestial";
        return TrySetSkin(themeId, out themeName);
    }

    public object Report() => new
    {
        totalRequests = Interlocked.Read(ref _totalRequests),
        engineRequests = Interlocked.Read(ref _engineRequests),
        gatewayRequests = Interlocked.Read(ref _gatewayRequests),
        nonLoopbackRequests = Interlocked.Read(ref _nonLoopbackRequests),
        outboundRequests = 0,
        fixedResponseCount = Interlocked.Read(ref _fixedResponses),
        mutationCount = Interlocked.Read(ref _mutations),
        modelSwitchCount = Interlocked.Read(ref _modelSwitches),
        skinSwitchCount = Interlocked.Read(ref _skinSwitches),
        chatTurnCount = Interlocked.Read(ref _chatTurns),
        activeAccountId = ActiveAccountId,
        currentProvider = CurrentProvider,
        currentModel = CurrentModel,
        currentSkin = CurrentSkinId,
        taskId = _taskId,
        conversationFingerprint = ConversationFingerprint(),
        codexRestartCount = 0,
        testOnly = true
    };

    private static object SkinView(FakeSkin item, bool active) => new
    {
        id = item.Id,
        manifestId = item.Id,
        name = item.Name,
        description = item.Description,
        appearanceText = item.Id == "paper-light" || item.Id == "official" ? "浅色" : "深色",
        motionText = item.Dynamic ? "动态效果" : "静态主题",
        background = item.Background,
        surface = item.Surface,
        surface2 = item.Surface2,
        accent = item.Accent,
        text = item.Text,
        line = item.Line,
        previewBackground = item.Background,
        previewAccent = item.Accent,
        isDynamic = item.Dynamic,
        online = item.Online,
        isActive = active,
        wasLastSelected = active
    };

    private object Account(string id, string email, bool isMain) => new
    {
        id,
        email,
        plan = "test-only",
        isMain,
        hasCredential = false,
        needsReauth = false,
        health = new { status = "healthy" },
        quota = new
        {
            weeklyPercent = id == "fake-account-a" ? 12.0 : 34.0,
            monthlyPercent = id == "fake-account-a" ? 21.0 : 43.0,
            weeklyResetAt = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds(),
            monthlyResetAt = DateTimeOffset.UtcNow.AddDays(15).ToUnixTimeSeconds(),
            updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            resetCredits = 0
        }
    };

    private string ConversationFingerprint()
    {
        lock (_gate)
        {
            var source = string.Join(
                "|",
                _messages.Select(item => $"{item.Role}:{item.Content.Length}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        }
    }
}

sealed record FakeModel(
    string Provider,
    string Id,
    string DisplayName,
    string Description,
    int ContextWindow,
    string Capability,
    string Speed);

sealed record FakeSkin(
    string Id,
    string Name,
    string Description,
    bool Online,
    bool Dynamic,
    string Background,
    string Surface,
    string Surface2,
    string Accent,
    string Text,
    string Line);

sealed record FakeChatMessage(string Role, string Content);
