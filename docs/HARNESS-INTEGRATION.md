# 外部 Harness 接入统一网关指南

给 DSH（DeepSeek Harness）、opencode、Trae、zcode 等本机 AI 工具接入 AI 中转站总管家统一 API 的一页说明。Codex 不是使用这套统一 API 的前置条件。
面向读者：任何要把自己工具的模型来源指到总管家的 harness 或人。

## 前置条件

1. 总管家已运行，号池管理页里至少有一个完成 OAuth 授权的 Codex 账号池（CLIProxy 出口）。
2. 已点击"启动 / 同步网关"，页面显示"本机运行中"。

## 三样接入信息

| 项目 | 值 |
|---|---|
| Base URL | `http://127.0.0.1:10110/v1`（端口以界面显示为准） |
| API Key | 界面"复制 API Key"得到的主钥匙 `cmm-gw-…`，或按下面方法发放的独立钥匙 |
| 协议 | OpenAI Chat Completions（`/chat/completions`）、OpenAI Responses（`/responses`）、Anthropic Messages（`/messages`） |

模型名规则（`GET /v1/models` 可列出全部）：

- `codex-auto/<模型>` —— **跨账号自动轮换**：哪个账号有额度用哪个；429/登录失效自动换下一个；
- `cli/<号池ID>/<模型>`、`codex-plus/<模型>`、`codex-pro/<模型>` —— **固定使用该账号**。
- `<来源编号>/<模型>` —— **固定使用某个第三方 API**。第三方模型不提供裸模型名，避免和官方模型或其他来源撞线。

普通 harness 只需要自己的网关 API Key，不需要也不应该填写内部来源指纹。网关会在每次精确路由请求前，
重新确认来源仍存在、仍启用、协议和模型名单没有变化；Worker 内部提供的指纹只用于额外检查“授权后来源是否变化”。

## 独立钥匙（推荐每个 harness 一把）

主钥匙等价于万能钥匙。更稳妥的做法是每个 harness 发一把独立钥匙，用量分开记账、可单独吊销：

```powershell
# 发放（完整钥匙只在创建这一次显示，立刻复制保存）
CodexModelManager.exe --gateway-key-create dsh
CodexModelManager.exe --gateway-key-create opencode

# 查看已有钥匙（只显示尾号）
CodexModelManager.exe --gateway-key-list

# 吊销
CodexModelManager.exe --gateway-key-revoke dsh
```

钥匙名只能用小写字母、数字、连字符。请求日志 `unified-gateway-request-log.jsonl`（在总管家数据目录）按钥匙名记账。

## 响应头

| 头 | 含义 |
|---|---|
| `X-CMM-Served-By` | 实际扣费账号的号池 ID（每次响应都有） |
| `X-CMM-Rotation-Group` / `X-CMM-Rotation-Attempts` | 轮换组模型名 / 本次尝试的账号数 |
| `X-CMM-Rotation-Exhausted: true` | 所有账号都限流，返回的是最后一个账号的原始错误 |
| `X-CMM-Retry-After-Seconds` | 全部账号冷却中时，提示还有几秒恢复（此时 HTTP 503） |

## 注意事项（大白话）

1. **长对话连续性**：用 `/responses` 续聊（带 `previous_response_id`）时，同一个对话会被粘在同一个账号上；只有该账号限流被迫换号时，新账号可能不认识旧的对话 ID（返回 4xx）——此时客户端应去掉 `previous_response_id`、带全量历史重发一次。Chat Completions 和 Anthropic Messages 每次自带全量历史，没有这个问题。
2. **流式**：支持 SSE 流式透传。
3. **失败关闭**：网关不会"跨号池兜底"到没配置的来源；精确路由名只在它自己的号池里生效。
4. **只听本机**：网关只绑定 127.0.0.1，其他电脑不可直接访问。
5. **大小限制**：单次 JSON 请求体最多 32 MB，超出会在发送上游前返回 413。

## 最小可用示例

```bash
curl http://127.0.0.1:10110/v1/chat/completions \
  -H "Authorization: Bearer cmm-gw-xxxxxxxx" \
  -H "Content-Type: application/json" \
  -d '{"model":"codex-auto/gpt-5.6-terra","messages":[{"role":"user","content":"hi"}]}'
```

成功响应除正常内容外会带 `X-CMM-Served-By: <号池ID>`，即本次实际扣费账号。
