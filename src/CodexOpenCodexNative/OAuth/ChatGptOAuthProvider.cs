using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace CodexOpenCodexNative.OAuth;

public sealed class ChatGptOAuthProvider
{
    public const string ProviderId = "chatgpt";
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthUrl = "https://auth.openai.com/oauth/authorize";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private const string Scope = "openid profile email offline_access api.connectors.read api.connectors.invoke";
    private const int CallbackPort = 1455;
    private const string CallbackPath = "/auth/callback";

    private readonly HttpClient _http;
    private readonly OAuthTokenStore _store;

    public ChatGptOAuthProvider(OAuthTokenStore store, HttpClient? http = null)
    {
        _store = store;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string CallbackUrl => $"http://127.0.0.1:{CallbackPort}{CallbackPath}";

    public string BuildLoginUrl(PkcePair pkce, string state)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = ClientId;
        query["redirect_uri"] = CallbackUrl;
        query["response_type"] = "code";
        query["scope"] = Scope;
        query["code_challenge"] = pkce.Challenge;
        query["code_challenge_method"] = "S256";
        query["state"] = state;
        query["originator"] = "opencodex";
        return $"{AuthUrl}?{query}";
    }

    public async Task<OAuthCredentials> ExchangeCodeAsync(
        string code,
        PkcePair pkce,
        CancellationToken cancellationToken = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = CallbackUrl,
            ["code"] = code,
            ["code_verifier"] = pkce.Verifier
        });

        using var response = await _http.PostAsync(TokenUrl, form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"token 交换失败：{(int)response.StatusCode} {Truncate(body, 300)}");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var credentials = new OAuthCredentials
        {
            Access = ReadString(root, "access_token") ?? string.Empty,
            Refresh = ReadString(root, "refresh_token") ?? string.Empty,
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                              + (ReadLong(root, "expires_in") > 0 ? ReadLong(root, "expires_in") * 1000 : 3600_000)
        };
        var idToken = ReadString(root, "id_token");
        credentials.AccountId = ExtractAccountId(idToken, credentials.Access);
        credentials.Email = ExtractEmail(idToken, credentials.Access);
        _store.Save(ProviderId, credentials);
        return credentials;
    }

    public static string? ExtractAccountId(string? idToken, string? accessToken)
    {
        foreach (var token in new[] { idToken, accessToken })
        {
            if (DecodeJwtPayload(token) is not JsonElement payload) continue;
            if (payload.TryGetProperty("chatgpt_account_id", out var direct) && direct.ValueKind == JsonValueKind.String)
                return direct.GetString();
            if (payload.TryGetProperty("https://api.openai.com/auth", out var ns) && ns.ValueKind == JsonValueKind.Object
                && ns.TryGetProperty("chatgpt_account_id", out var nsId) && nsId.ValueKind == JsonValueKind.String)
                return nsId.GetString();
            if (payload.TryGetProperty("organizations", out var orgs) && orgs.ValueKind == JsonValueKind.Array
                && orgs.GetArrayLength() > 0 && orgs[0].TryGetProperty("id", out var orgId)
                && orgId.ValueKind == JsonValueKind.String)
                return orgId.GetString();
        }
        return null;
    }

    public static string? ExtractEmail(string? idToken, string? accessToken)
    {
        foreach (var token in new[] { idToken, accessToken })
        {
            if (DecodeJwtPayload(token) is not JsonElement payload) continue;
            if (payload.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String)
                return email.GetString()?.ToLowerInvariant();
        }
        return null;
    }

    public static JsonElement? DecodeJwtPayload(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var bytes = Convert.FromBase64String(PadBase64(parts[1]));
            return JsonDocument.Parse(Encoding.UTF8.GetString(bytes)).RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string PadBase64(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static long ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
