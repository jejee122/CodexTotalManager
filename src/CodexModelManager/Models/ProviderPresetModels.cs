namespace CodexModelManager.Models;

/// <summary>
/// A source-controlled provider template. Templates only prefill public API
/// metadata; credentials remain user supplied and are stored by SecretStore.
/// </summary>
public sealed record ProviderPreset(
    string Id,
    string DisplayName,
    string SuggestedName,
    string BaseUrl,
    string Adapter,
    int ContextWindow,
    string Summary)
{
    public bool IsCustom => Id.Equals("custom", StringComparison.OrdinalIgnoreCase);

    public string ProtocolText => Adapter switch
    {
        "openai-responses" => "OpenAI Responses",
        "openai-chat" => "OpenAI Chat Completions",
        "anthropic" => "Anthropic Messages",
        "google" => "Google Generative Language",
        _ => Adapter
    };
}

public static class ProviderPresetCatalog
{
    private static readonly ProviderPreset[] Presets =
    [
        new(
            "custom",
            "自定义 / 自建统一网关",
            "",
            "",
            "openai-chat",
            128_000,
            "适合 LiteLLM、New API、One API、LocalAI 和其他 OpenAI 兼容地址；URL 由你填写。"),
        new(
            "openai-api",
            "OpenAI API（开发者平台）",
            "OpenAI API",
            "https://api.openai.com/v1",
            "openai-responses",
            128_000,
            "使用 OpenAI 开发者平台 API Key；它与 ChatGPT/Codex 套餐登录相互独立。"),
        new(
            "xai",
            "xAI Grok（官方 API）",
            "xAI Grok",
            "https://api.x.ai/v1",
            "openai-responses",
            128_000,
            "直接走 xAI 官方 Responses API。只使用你填写的 xAI API Key，不读取 Grok 网页 Cookie。"),
        new(
            "openrouter",
            "OpenRouter",
            "OpenRouter",
            "https://openrouter.ai/api/v1",
            "openai-chat",
            128_000,
            "OpenAI 兼容聚合网关；模型很多，实际上下文长度以所选模型为准。"),
        new(
            "deepseek",
            "DeepSeek",
            "DeepSeek",
            "https://api.deepseek.com/v1",
            "openai-chat",
            128_000,
            "直接连接 DeepSeek 官方 OpenAI 兼容接口。"),
        new(
            "anthropic",
            "Anthropic Claude",
            "Anthropic Claude",
            "https://api.anthropic.com",
            "anthropic",
            200_000,
            "使用 Anthropic 原生 Messages 协议，包含流式回复和工具调用转换。"),
        new(
            "google",
            "Google Gemini",
            "Google Gemini",
            "https://generativelanguage.googleapis.com",
            "google",
            1_000_000,
            "使用 Google Generative Language 原生协议；上下文长度仍以具体 Gemini 模型为准。"),
        new(
            "mistral",
            "Mistral AI",
            "Mistral AI",
            "https://api.mistral.ai/v1",
            "openai-chat",
            128_000,
            "直接连接 Mistral 官方 OpenAI 兼容接口。"),
        new(
            "groq",
            "Groq",
            "Groq",
            "https://api.groq.com/openai/v1",
            "openai-chat",
            128_000,
            "直接连接 Groq 官方 OpenAI 兼容接口。"),
        new(
            "qwen",
            "阿里云百炼 / 通义千问",
            "通义千问",
            "https://dashscope.aliyuncs.com/compatible-mode/v1",
            "openai-chat",
            128_000,
            "使用阿里云百炼的 OpenAI 兼容接口。"),
        new(
            "moonshot",
            "Moonshot / Kimi",
            "Moonshot Kimi",
            "https://api.moonshot.cn/v1",
            "openai-chat",
            128_000,
            "直接连接 Moonshot 官方 OpenAI 兼容接口。"),
        new(
            "perplexity",
            "Perplexity",
            "Perplexity",
            "https://api.perplexity.ai",
            "openai-chat",
            128_000,
            "直接连接 Perplexity 官方 OpenAI 兼容接口。"),
        new(
            "together",
            "Together AI",
            "Together AI",
            "https://api.together.xyz/v1",
            "openai-chat",
            128_000,
            "直接连接 Together AI 官方 OpenAI 兼容接口。")
    ];

    public static IReadOnlyList<ProviderPreset> All => Presets;

    public static ProviderPreset? Find(string? id) => Presets.FirstOrDefault(
        preset => preset.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
