using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Logging;

namespace UltraLauncher.Core.Auth;

public sealed record XboxToken(string Token, string UserHash);

public sealed record MinecraftToken(string AccessToken, DateTimeOffset ExpiresAt);

public sealed record MinecraftProfile(string Id, string Name);

/// <summary>
/// Цепочка Xbox Live → XSTS → Minecraft Services (официальные публичные эндпоинты).
/// Токены никогда не пишутся в журнал.
/// </summary>
public sealed class XboxMinecraftClient
{
    public const string XboxUserAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    public const string XstsAuthUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    public const string MinecraftLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    public const string EntitlementsUrl = "https://api.minecraftservices.com/entitlements/mcstore";
    public const string ProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    private readonly HttpClient _http;
    private readonly ILauncherLog _log;

    public XboxMinecraftClient(HttpClient http, ILauncherLog log)
    {
        _http = http;
        _log = log;
    }

    public async Task<XboxToken> AuthenticateXboxAsync(string microsoftAccessToken, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = "d=" + microsoftAccessToken,
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
        };
        using var response = await PostJsonAsync(XboxUserAuthUrl, body, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new LauncherException(ErrorCode.AuthFailed, $"Xbox Live отклонил вход (HTTP {(int)response.StatusCode}).", "Повторите вход позже.");
        return await ReadXboxTokenAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<XboxToken> AuthorizeXstsAsync(string xboxUserToken, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new JsonArray(xboxUserToken),
            },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT",
        };
        using var response = await PostJsonAsync(XstsAuthUrl, body, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw MapXstsError(ParseXErr(text));
        }
        if (!response.IsSuccessStatusCode)
            throw new LauncherException(ErrorCode.AuthFailed, $"Служба XSTS вернула HTTP {(int)response.StatusCode}.", "Повторите вход позже.");
        return await ReadXboxTokenAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<MinecraftToken> LoginMinecraftAsync(XboxToken xsts, CancellationToken ct)
    {
        var body = new JsonObject { ["identityToken"] = $"XBL3.0 x={xsts.UserHash};{xsts.Token}" };
        using var response = await PostJsonAsync(MinecraftLoginUrl, body, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new LauncherException(ErrorCode.AuthAppNotApproved,
                "Minecraft Services отклонил вход для этого приложения (HTTP 403).",
                "Новые Client ID нужно согласовать с Mojang (форма https://aka.ms/mce-reviewappid). Подробности — в README, раздел «Вход Microsoft».");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new LauncherException(ErrorCode.AuthFailed, "Слишком много попыток входа.", "Подождите несколько минут и повторите.");
        if (!response.IsSuccessStatusCode)
            throw new LauncherException(ErrorCode.AuthFailed, $"Minecraft Services вернул HTTP {(int)response.StatusCode}.", "Повторите вход позже.");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var token = doc.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 86400;
        SecretRedactor.Register(token);
        return new MinecraftToken(token, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    /// <summary>Есть ли у аккаунта Minecraft Java Edition (подписанные права магазина).</summary>
    public async Task<bool> HasGameAsync(MinecraftToken token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, EntitlementsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new LauncherException(ErrorCode.AuthFailed, $"Не удалось проверить наличие игры (HTTP {(int)response.StatusCode}).", "Повторите позже.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return HasJavaEntitlement(doc.RootElement);
    }

    internal static bool HasJavaEntitlement(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in items.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is "game_minecraft" or "product_minecraft") return true;
        }
        return false;
    }

    /// <summary>Профиль Minecraft. null — у аккаунта нет профиля Java Edition (нет игры или не выбран ник).</summary>
    public async Task<MinecraftProfile?> GetProfileAsync(MinecraftToken token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProfileUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new LauncherException(ErrorCode.AuthExpired, "Сессия Minecraft истекла.", "Войдите в аккаунт заново.");
        if (!response.IsSuccessStatusCode)
            throw new LauncherException(ErrorCode.AuthFailed, $"Не удалось получить профиль Minecraft (HTTP {(int)response.StatusCode}).", "Повторите позже.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return new MinecraftProfile(doc.RootElement.GetProperty("id").GetString()!, doc.RootElement.GetProperty("name").GetString()!);
    }

    internal static long? ParseXErr(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("XErr", out var x) && x.TryGetInt64(out var v) ? v : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static LauncherException MapXstsError(long? xerr) => xerr switch
    {
        2148916233 => new LauncherException(ErrorCode.AuthNoXboxAccount,
            "У этой учётной записи Microsoft нет профиля Xbox.",
            "Войдите на https://www.xbox.com и создайте профиль, затем повторите вход."),
        2148916235 => new LauncherException(ErrorCode.AuthRegionBlocked,
            "Xbox Live недоступен в вашей стране или регионе.", null),
        2148916236 or 2148916237 => new LauncherException(ErrorCode.AuthFailed,
            "Для этой учётной записи нужна проверка возраста (требование региона).",
            "Пройдите проверку на https://www.xbox.com и повторите вход."),
        2148916238 => new LauncherException(ErrorCode.AuthChildAccount,
            "Это детская учётная запись — её нужно добавить в семейную группу Microsoft.",
            "Попросите взрослого добавить вас в семью на https://account.microsoft.com/family."),
        _ => new LauncherException(ErrorCode.AuthFailed,
            $"Xbox Live отказал в доступе{(xerr is null ? "" : $" (код {xerr})")}.", "Повторите вход позже."),
    };

    private async Task<XboxToken> ReadXboxTokenAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var token = doc.RootElement.GetProperty("Token").GetString()!;
        var uhs = doc.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;
        SecretRedactor.Register(token);
        return new XboxToken(token, uhs);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string url, JsonObject body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await SendAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ErrorTranslator.IsTransient(ex))
        {
            _log.Warn("Auth", $"Сетевая ошибка при обращении к {request.RequestUri?.Host}: {ex.Message}");
            throw new LauncherException(ErrorCode.Network, "Нет связи с серверами Microsoft/Xbox/Minecraft.",
                "Проверьте подключение к интернету. Ранее подтверждённый аккаунт может играть офлайн.", ex);
        }
    }
}
