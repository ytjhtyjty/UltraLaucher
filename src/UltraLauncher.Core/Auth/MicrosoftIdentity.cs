using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Logging;

namespace UltraLauncher.Core.Auth;

public sealed record MicrosoftToken(string AccessToken, string AccountId, string? Username, DateTimeOffset ExpiresOn);

/// <summary>Получение токена Microsoft (OAuth 2.0 для публичного desktop-клиента, без секрета).</summary>
public interface IMicrosoftIdentity
{
    bool IsConfigured { get; }
    Task<MicrosoftToken> SignInInteractiveAsync(CancellationToken ct);
    /// <summary>Тихое обновление по refresh-токену из защищённого кеша MSAL. null — нужен повторный вход.</summary>
    Task<MicrosoftToken?> AcquireSilentAsync(string accountId, CancellationToken ct);
    Task RemoveAsync(string accountId);
}

/// <summary>
/// Реализация на MSAL.NET: вход через системный браузер (Authorization Code + PKCE, redirect http://localhost),
/// кеш токенов шифруется DPAPI для текущего пользователя Windows (Microsoft.Identity.Client.Extensions.Msal).
/// Пароль лаунчер не видит.
/// </summary>
public sealed class MsalMicrosoftIdentity : IMicrosoftIdentity
{
    public const string Authority = "https://login.microsoftonline.com/consumers";
    public static readonly string[] Scopes = ["XboxLive.signin"];

    private readonly string? _clientId;
    private readonly string _cacheDirectory;
    private readonly string _cacheFileName;
    private readonly ILauncherLog _log;
    private IPublicClientApplication? _app;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public MsalMicrosoftIdentity(string? clientId, string cacheDirectory, string cacheFileName, ILauncherLog log)
    {
        _clientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();
        _cacheDirectory = cacheDirectory;
        _cacheFileName = cacheFileName;
        _log = log;
    }

    public bool IsConfigured => _clientId is not null && Guid.TryParse(_clientId, out _);

    private async Task<IPublicClientApplication> GetAppAsync()
    {
        if (!IsConfigured)
            throw new LauncherException(ErrorCode.AuthNotConfigured,
                "Вход через Microsoft не настроен: не указан Client ID приложения.",
                "Зарегистрируйте приложение в Microsoft Entra и укажите Client ID в «Настройки → Расширенные» (инструкция в README).");

        if (_app is not null) return _app;
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_app is not null) return _app;
            var app = PublicClientApplicationBuilder.Create(_clientId)
                .WithAuthority(Authority)
                .WithRedirectUri("http://localhost")
                .WithClientName("UltraLauncher")
                .Build();

            var storage = new StorageCreationPropertiesBuilder(_cacheFileName, _cacheDirectory).Build(); // Windows: DPAPI CurrentUser
            var helper = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
            helper.VerifyPersistence();
            helper.RegisterCache(app.UserTokenCache);
            _app = app;
            return app;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<MicrosoftToken> SignInInteractiveAsync(CancellationToken ct)
    {
        var app = await GetAppAsync().ConfigureAwait(false);
        try
        {
            var result = await app.AcquireTokenInteractive(Scopes)
                .WithUseEmbeddedWebView(false)
                .WithPrompt(Prompt.SelectAccount)
                .WithSystemWebViewOptions(new SystemWebViewOptions
                {
                    HtmlMessageSuccess = "<html><body style='font-family:Segoe UI;background:#111418;color:#e8eaed;text-align:center;padding-top:15%'><h2>Вход выполнен</h2><p>Вернитесь в UltraLauncher. Эту вкладку можно закрыть.</p></body></html>",
                    HtmlMessageError = "<html><body style='font-family:Segoe UI;background:#111418;color:#e8eaed;text-align:center;padding-top:15%'><h2>Вход не выполнен</h2><p>Вернитесь в UltraLauncher и попробуйте ещё раз.</p></body></html>",
                })
                .ExecuteAsync(ct).ConfigureAwait(false);
            return ToToken(result);
        }
        catch (MsalClientException ex) when (ex.ErrorCode is MsalError.AuthenticationCanceledError)
        {
            throw new LauncherException(ErrorCode.AuthCancelled, "Вход отменён.", null, ex);
        }
        catch (OperationCanceledException)
        {
            throw new LauncherException(ErrorCode.AuthCancelled, "Вход отменён.");
        }
        catch (MsalServiceException ex) when (ex.ErrorCode is "access_denied")
        {
            throw new LauncherException(ErrorCode.AuthCancelled, "Вход отклонён: доступ не предоставлен.",
                "Чтобы войти, подтвердите разрешения в окне Microsoft.", ex);
        }
        catch (MsalServiceException ex)
        {
            _log.Warn("Auth", $"Ошибка Microsoft: {ex.ErrorCode}");
            throw new LauncherException(ErrorCode.AuthFailed, $"Microsoft отклонил вход ({ex.ErrorCode}).",
                ex.ErrorCode is "invalid_client" or "unauthorized_client" or "invalid_request"
                    ? "Проверьте Client ID и настройки регистрации приложения (тип «Мобильные и настольные приложения», redirect http://localhost, учётные записи Microsoft)."
                    : "Повторите попытку позже.", ex);
        }
        catch (MsalException ex)
        {
            throw new LauncherException(ErrorCode.AuthFailed, $"Не удалось выполнить вход ({ex.ErrorCode}).", "Повторите попытку.", ex);
        }
    }

    public async Task<MicrosoftToken?> AcquireSilentAsync(string accountId, CancellationToken ct)
    {
        var app = await GetAppAsync().ConfigureAwait(false);
        var account = await app.GetAccountAsync(accountId).ConfigureAwait(false);
        if (account is null) return null;
        try
        {
            var result = await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
            return ToToken(result);
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
        catch (MsalServiceException ex) when (ex.ErrorCode is "invalid_grant")
        {
            return null;
        }
        catch (MsalClientException ex) when (ex.ErrorCode is "request_timeout" or "service_not_available")
        {
            throw new LauncherException(ErrorCode.Network, "Сервер Microsoft недоступен.", "Проверьте подключение к интернету.", ex);
        }
    }

    public async Task RemoveAsync(string accountId)
    {
        if (!IsConfigured) return;
        var app = await GetAppAsync().ConfigureAwait(false);
        var account = await app.GetAccountAsync(accountId).ConfigureAwait(false);
        if (account is not null) await app.RemoveAsync(account).ConfigureAwait(false);
    }

    private static MicrosoftToken ToToken(AuthenticationResult r)
    {
        SecretRedactor.Register(r.AccessToken);
        return new MicrosoftToken(r.AccessToken, r.Account.HomeAccountId.Identifier, r.Account.Username, r.ExpiresOn);
    }
}
