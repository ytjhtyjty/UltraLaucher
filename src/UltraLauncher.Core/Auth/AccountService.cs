using System.Security.Cryptography;
using System.Text;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Logging;
using UltraLauncher.Core.Minecraft;

namespace UltraLauncher.Core.Auth;

public enum AccountStatus
{
    /// <summary>Право на игру подтверждено.</summary>
    Licensed,
    /// <summary>Вход выполнен, но Minecraft Java Edition не куплен — доступна только демо-версия.</summary>
    DemoOnly,
}

/// <summary>Сценарии работы с аккаунтами: вход, обновление, выход, данные игрока для запуска.</summary>
public sealed class AccountService
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(10);

    private readonly Func<IMicrosoftIdentity> _identityFactory;
    private readonly XboxMinecraftClient _xbox;
    private readonly AccountStore _store;
    private readonly ILauncherLog _log;
    private readonly OperationGuard _guard;

    public AccountService(Func<IMicrosoftIdentity> identityFactory, XboxMinecraftClient xbox, AccountStore store, ILauncherLog log, OperationGuard guard)
    {
        _identityFactory = identityFactory;
        _xbox = xbox;
        _store = store;
        _log = log;
        _guard = guard;
    }

    public bool IsMicrosoftConfigured => _identityFactory().IsConfigured;

    public IReadOnlyList<StoredAccount> Accounts => _store.GetAll();

    public StoredAccount? Find(string? id) => _store.Find(id);

    public static AccountStatus StatusOf(StoredAccount a) => a.OwnsGame ? AccountStatus.Licensed : AccountStatus.DemoOnly;

    public async Task<StoredAccount> SignInAsync(CancellationToken ct)
    {
        using var operation = _guard.Begin("auth:signin", "Вход уже выполняется.");
        var identity = _identityFactory();
        _log.Info("Auth", "Начат вход через Microsoft (системный браузер)");
        var ms = await identity.SignInInteractiveAsync(ct).ConfigureAwait(false);
        var account = await CompleteChainAsync(ms, ct).ConfigureAwait(false);
        _log.Info("Auth", $"Вход выполнен: {account.DisplayName}, игра {(account.OwnsGame ? "куплена" : "не куплена (демо)")}");
        return account;
    }

    /// <summary>Повторно проверяет аккаунт онлайн (обновляет токены и право на игру).</summary>
    public async Task<StoredAccount> RefreshAsync(string accountId, CancellationToken ct)
    {
        using var operation = _guard.Begin("auth:refresh:" + accountId, "Обновление аккаунта уже выполняется.");
        var identity = _identityFactory();
        var ms = await identity.AcquireSilentAsync(accountId, ct).ConfigureAwait(false)
                 ?? throw new LauncherException(ErrorCode.AuthExpired, "Сессия Microsoft истекла.", "Войдите в аккаунт заново.");
        return await CompleteChainAsync(ms, ct).ConfigureAwait(false);
    }

    public async Task SignOutAsync(string accountId)
    {
        try
        {
            await _identityFactory().RemoveAsync(accountId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Auth", "Не удалось удалить аккаунт из кеша MSAL: " + ex.Message);
        }
        _store.Remove(accountId);
        _log.Info("Auth", "Аккаунт удалён, локальные данные авторизации стёрты");
    }

    /// <summary>
    /// Данные игрока для запуска. Онлайн — с актуальным токеном (обновляется при необходимости).
    /// Офлайн — только для аккаунта, у которого ранее подтверждено право на игру.
    /// </summary>
    public async Task<PlayerIdentity> GetPlayerAsync(string? accountId, bool offline, CancellationToken ct)
    {
        var account = Find(accountId) ?? throw new LauncherException(ErrorCode.AccountRequired,
            "Для запуска нужен аккаунт Microsoft.", "Войдите на вкладке «Аккаунты».");

        if (offline)
        {
            if (!account.OwnsGame || !account.HasProfile)
                throw new LauncherException(ErrorCode.AccountRequired,
                    "Офлайн-режим доступен только аккаунту, у которого ранее онлайн подтверждена покупка Minecraft.",
                    "Подключитесь к интернету и выполните вход.");
            _log.Info("Auth", $"Офлайн-запуск для {account.DisplayName} (подтверждено {account.OwnershipVerifiedUtc:yyyy-MM-dd})");
            return ToIdentity(account);
        }

        if (account.MinecraftAccessToken is null || account.MinecraftTokenExpiresAt is null
            || account.MinecraftTokenExpiresAt.Value - DateTimeOffset.UtcNow < RefreshMargin)
        {
            _log.Info("Auth", "Токен Minecraft истекает — обновление");
            account = await RefreshAsync(account.Id, ct).ConfigureAwait(false);
        }
        return ToIdentity(account);
    }

    private async Task<StoredAccount> CompleteChainAsync(MicrosoftToken ms, CancellationToken ct)
    {
        var xbl = await _xbox.AuthenticateXboxAsync(ms.AccessToken, ct).ConfigureAwait(false);
        var xsts = await _xbox.AuthorizeXstsAsync(xbl.Token, ct).ConfigureAwait(false);
        var mc = await _xbox.LoginMinecraftAsync(xsts, ct).ConfigureAwait(false);
        var ownsGame = await _xbox.HasGameAsync(mc, ct).ConfigureAwait(false);
        var profile = await _xbox.GetProfileAsync(mc, ct).ConfigureAwait(false);

        if (ownsGame && profile is null)
            throw new LauncherException(ErrorCode.AuthNoProfile,
                "Игра куплена, но профиль Minecraft ещё не создан.",
                "Выберите никнейм на https://www.minecraft.net/msaprofile и войдите снова.");

        var existing = _store.Find(ms.AccountId);
        var account = new StoredAccount
        {
            Id = ms.AccountId,
            MicrosoftUsername = ms.Username,
            MinecraftName = profile?.Name,
            MinecraftUuid = profile?.Id,
            OwnsGame = ownsGame && profile is not null,
            OwnershipVerifiedUtc = ownsGame ? DateTime.UtcNow : existing?.OwnershipVerifiedUtc,
            MinecraftAccessToken = mc.AccessToken,
            MinecraftTokenExpiresAt = mc.ExpiresAt,
        };
        _store.Upsert(account);
        return account;
    }

    private static PlayerIdentity ToIdentity(StoredAccount a)
    {
        if (a.OwnsGame && a.HasProfile)
            return new PlayerIdentity(a.MinecraftName!, a.MinecraftUuid!, a.MinecraftAccessToken ?? "0", "0", "msa", IsDemo: false);
        // Демо-режим (официальная бесплатная пробная версия) для аккаунта без игры.
        return new PlayerIdentity(a.MinecraftName ?? "Player", a.MinecraftUuid ?? DemoUuid(a.Id), a.MinecraftAccessToken ?? "0", "0", "msa", IsDemo: true);
    }

    private static string DemoUuid(string accountId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("UltraLauncherDemo:" + accountId));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return Convert.ToHexStringLower(hash);
    }
}
