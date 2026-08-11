# OpenCodex → 全 C# 原生重写（CodexOpenCodexNative）迁移地图

目标：把外部独立的 TypeScript 项目 OpenCodex（`lidge-jun/opencodex`，npm `@bitkyc08/opencodex` 2.7.41）逐模块翻译为 C#，彻底融入新总管家（CodexTotalManager），不再依赖 bun/Node 外部进程。

精确迁移基线为 `v2.7.41` / `ac73f189cf7e3f4ee55690ed8dc7e354b7e6ed10`。截至
2026-08-11，上游最新版本为 `v2.12.0`；总管家不会在运行时下载或执行这个 npm 包，也不会自动继承
上游后续提交。每次兼容结论必须以本地 C# 合同测试为准，不能把“上游已修”写成“总管家已具备”。

## 一期（MVP，已完成并验收）

范围：OpenAI 兼容自定义 provider 的完整闭环——客户端 → 原生代理 → 上游。

| OpenCodex TS 模块 | 行数 | C# 落点 | 状态 |
|---|---|---|---|
| `src/types.ts`（领域类型） | 1027 | `Models/OcxModels.cs` | ✅ |
| `src/config.ts`（配置读写） | 1195 | `Config/NativeProxyConfigStore.cs` | ✅ |
| `src/providers/registry.ts` | 1074 | `Providers/ProviderRegistry.cs` | ✅ |
| `src/router.ts`（路由解析） | 367 | `Providers/RouteResolver.cs` | ✅ |
| `src/adapters/base.ts` | 54 | `Adapters/IProviderAdapter.cs` | ✅ |
| `src/adapters/openai-chat.ts` | 795 | `Adapters/OpenAiChatAdapter.cs` | ✅ |
| `src/server/index.ts`（HTTP 宿主） | 851 | `Host/NativeProxyHost.cs` | ✅ |

技术替换：Bun.serve → ASP.NET Core Minimal API；zod → 手写校验；SSE 手工解析 → StreamReader 逐行。

验收记录（2026-08-08，mock 服务器 `tools/mock-openai-server.mjs`）：
- `/healthz` → 200 `{status:ok}` ✅
- `/v1/models` → 模型目录（含 `namespaced` 前缀）✅
- 无 AdmissionToken → 401 ✅
- `/v1/chat/completions` 非流式 → 透传上游 JSON ✅
- `/v1/chat/completions` 流式 → SSE chunk（delta/usage/finish/`[DONE]`）✅
- `/v1/responses` → 501（明确告知二期实现）✅

## 二期（已完成：/v1/responses 协议闭环）

| 优先级 | TS 模块 | 内容 | 状态 |
|---|---|---|---|
| P0 | `src/server/responses/core.ts`（1949） | `/v1/responses` 协议 | ✅ 核心闭环 |
| P0 | `src/responses/parser.ts`（598） | Responses 请求解析 | ✅ `Responses/ResponsesParser.cs` |
| P0 | `src/bridge.ts`（1142） | Responses SSE 桥接 | ✅ `Responses/ResponsesBridge.cs` |

二期验收记录（2026-08-08，mock 上游）：
- 流式：`response.created` → `output_text.delta` → `response.usage` → `output_text.done` → `content_part.done` → `output_item.done` → `response.completed`(end_turn) → `[DONE]`，序列与 TS 原版一致 ✅
- 非流式：完整 Response JSON（output + usage 含 `input_tokens_details`/`output_tokens_details` 零默认）✅
- 复杂 input：`message`/`input_text`/`function_call_output` 数组解析 ✅
- 已知取舍：`response.heartbeat` 心跳、reasoning 项、工具调用（`function_call` item）留待后续；上游静默时靠 HttpClient 300s 超时兜底

## 三期（已完成：全部协议适配器 + 运维端点）

| 优先级 | TS 模块 | C# 落点 | 状态 |
|---|---|---|---|
| P0 | `src/adapters/openai-responses.ts`（695） | `Adapters/OpenAiResponsesAdapter.cs` | ✅ |
| P0 | `src/bridge.ts` 补全 | `Responses/ResponsesBridge.cs`：心跳、reasoning 项、function_call 工具项 | ✅ |
| P1 | `src/server/request-log.ts`（772）+ `usage/*` | `Logging/RequestLogService.cs` + `/api/logs` + `/api/usage` | ✅ |
| P1 | `src/oauth/*`（PKCE + chatgpt） | `OAuth/Pkce.cs`、`OAuth/ChatGptOAuthProvider.cs`、`OAuth/OAuthTokenStore.cs` + `/api/oauth/login|callback|status` | ✅ |
| P2 | `src/adapters/anthropic.ts` + `claude/outbound.ts` | `Adapters/AnthropicAdapter.cs` + `Responses/AnthropicParser.cs` + `Responses/AnthropicOutboundBridge.cs` | ✅ |
| P2 | `src/adapters/google.ts` | `Adapters/GoogleAdapter.cs` | ✅ |
| P2 | `src/server/management-api.ts` | `/api/status`、`/api/logs`、`/api/usage` | ✅ |

三期验收记录（2026-08-08，内置多协议 FakeUpstream 实测）：
- chat 上游 → `/v1/chat/completions`（流式/非流式）✅
- responses 上游 → `/v1/responses` 透传桥接 ✅
- anthropic 上游 → `/v1/messages`（Anthropic 协议直连）✅
- chat 上游 → `/v1/messages`（Anthropic 出站协议转换）✅
- google 上游 → `/v1/chat/completions` 与 `/v1/messages`（Google 协议转换）✅
- chat 上游 → `/v1/responses` 非流式转换 ✅
- `/api/logs`（最近 200 条环形缓冲 + JSONL 落盘）、`/api/usage`（按 provider 分组汇总）、`/api/status` ✅
- OAuth：PKCE S256 + 官方 client_id + 1455 回调端口，与原版一致 ✅
- 已修 bug：Body 双读、using 提前释放响应流、事件流二次枚举、非流式文本提取协议相关

## 四期（已完成：新管家内置引擎集成 + 真实 Codex 验证）

| 事项 | 内容 | 状态 |
|---|---|---|
| 集成 | `CodexModelManager.csproj` 引用 CodexOpenCodexNative；`Services/NativeEngineService.cs` 托管引擎；AppServices 挂载 `NativeEngine` | ✅ |
| 启动模式 | `CodexModelManager.exe --native-engine --port <端口> [--data-root <路径>]`（与 --unified-gateway 同款命令行模式） | ✅ |
| 健康自检 | 启动后 4 秒健康检查，失败输出诊断文件并退出码 3 | ✅ |
| zstd 支持 | Codex 客户端用 zstd 压缩 body（Bun 原生支持，ASP.NET 默认不支持）→ 加 `ZstdSharp.Port` 包 + 自定义 `IDecompressionProvider` | ✅ |
| 认证透传 | Codex 的 Authorization/chatgpt-account-id/x-codex-* 等 17 个头透传给上游（原版 FORWARD_HEADERS 清单） | ✅ |
| 真实验证 | 2026-08-08：Codex CLI 临时指向引擎（10111）→ 引擎转发 bun(10100) → 官方 ChatGPT → **Codex 真实输出"收到"**，链路全通；验证后 config.toml 已还原 | ✅ |

真实验证中修掉的坑（全部已修）：
1. Codex 用 zstd 压缩 body → 默认解压中间件不支持 → 加 ZstdSharp
2. Codex 的 WebSocket 升级（405）后 fallback HTTPS → 正常兼容行为，无需实现 ws
3. 认证头透传遗漏（Authorization 被 provider.ApiKey 覆盖）→ 转发优先 Codex 头

已知小瑕疵：Codex 日志出现一次无害的 `OutputTextDelta without active item` 警告（不影响输出），后续可调。

## 五期（剩余，未做）

| 优先级 | TS 模块 | 内容 | 说明 |
|---|---|---|---|
| P3 | `src/gui/` | Web GUI | 用总管家 WPF 替代，不移植 |
| P3 | `src/tray/`、`src/update/`、`src/service.ts` | 托盘/自动更新/系统服务 | 总管家已有等价能力 |
| P3 | `src/adapters/cursor.ts`、`kiro.ts`、`azure.ts`、`mimo-free.ts` | 其余上游适配器 | 按需裁剪 |
| P3 | `src/vision/*`、`src/web-search/*` | 视觉/联网搜索 | 按需 |
| 切换生产 | 停 bun → `--native-engine --port 10100` | 正式替代外部 OpenCodex | **需用户明确批准**（涉及生产切换） |

## 硬性约定

- 新代码不碰生产数据：默认数据根为 `%LOCALAPPDATA%\CodexTotalManager\native-proxy\`
- API Key 由总管家 SecretStore 注入，不落明文
- 总管家内部路由统一使用 `cmm/` 命名空间，并由 `InternalRouteNames` 集中定义；不再把旧项目暗号当作活动路由
- 未实现端点必须返回明确错误（如 501），禁止静默吞掉
- 每一期完成必须过 mock 闭环验收后才算数
