# Provider presets and gateway boundary

Provider presets are source-controlled metadata inside AI Gateway Manager.
They are not downloaded plugins, embedded third-party services or background
containers. Selecting a preset only fills a public endpoint, adapter type and
conservative context-window value. The user must supply the provider's own API
key, which is stored through the existing Windows CurrentUser secret store.

## Built-in presets

| Preset | Endpoint | Native adapter |
| --- | --- | --- |
| OpenAI API | `https://api.openai.com/v1` | OpenAI Responses |
| xAI Grok | `https://api.x.ai/v1` | OpenAI Responses |
| OpenRouter | `https://openrouter.ai/api/v1` | OpenAI Chat |
| DeepSeek | `https://api.deepseek.com/v1` | OpenAI Chat |
| Anthropic Claude | `https://api.anthropic.com` | Anthropic Messages |
| Google Gemini | `https://generativelanguage.googleapis.com` | Google Generative Language |
| Mistral AI | `https://api.mistral.ai/v1` | OpenAI Chat |
| Groq | `https://api.groq.com/openai/v1` | OpenAI Chat |
| Alibaba Bailian / Qwen | `https://dashscope.aliyuncs.com/compatible-mode/v1` | OpenAI Chat |
| Moonshot / Kimi | `https://api.moonshot.cn/v1` | OpenAI Chat |
| Perplexity | `https://api.perplexity.ai` | OpenAI Chat |
| Together AI | `https://api.together.xyz/v1` | OpenAI Chat |

Model discovery uses the provider's model-list endpoint before anything is
saved. The OpenAI developer API key is separate from a ChatGPT or Codex plan
login. Anthropic and Google use their native authentication headers. Other
presets use an OpenAI-compatible Bearer header. Remote HTTP, URL credentials,
queries and fragments are rejected; only HTTPS or an explicit loopback HTTP
address is accepted.

Discovery is bounded to 4 MB, 10,000 distinct models and 100 pages. Perplexity
is a deliberate special case: discovery uses `/v1/models`, while inference uses
`/chat/completions` on `https://api.perplexity.ai`. HTTP redirects are never
followed, so Bearer, `x-api-key` and `x-goog-api-key` credentials cannot be
carried to a redirected host.

Every saved third-party model is exposed as `provider/model`. Bare model IDs
remain reserved for the built-in OpenAI provider, preventing two providers with
the same model name from being selected by dictionary order.

## GitHub projects reviewed

- `xai-org/xai-sdk-python`: official xAI SDK and API reference implementation.
- `BerriAI/litellm`: high-star multi-provider gateway with dedicated xAI Chat,
  Responses and Realtime provider modules.
- `QuantumNous/new-api`: high-star multi-provider gateway with a dedicated
  `relay/channel/xai` adapter.
- `ENTERPILOT/GoModel`: active MIT-licensed gateway with xAI and other mainstream
  providers behind OpenAI- and Anthropic-compatible surfaces.
- `songquanpeng/one-api` and `mudler/LocalAI`: high-star examples of
  OpenAI-compatible gateway surfaces and self-hosted endpoints.

No code, executable, OAuth credential, browser cookie or installation script
from those repositories is bundled by this feature. The integration is a C#
implementation using AI Gateway Manager's existing adapters and security model.

## Deliberate exclusions

- browser-cookie scraping or replay;
- Cloudflare bypass and unofficial web endpoints;
- silent OAuth login or automatic browser control;
- Azure OpenAI deployment URLs and AWS Bedrock SigV4 until dedicated adapters,
  configuration fields and offline contract tests exist;
- automatic activation without a user-provided provider credential.
