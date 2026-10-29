using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Logging;

namespace UltraLauncher.Core.Auth;

/// <summary>Сохранённый Microsoft-аккаунт или локальный профиль. Токены хранятся только в зашифрованном файле.</summary>
public sealed class StoredAccount
{
    /// <summary>Идентификатор аккаунта MSAL (HomeAccountId).</summary>
    public required string Id { get; init; }
    public string? MicrosoftUsername { get; set; }
    public string? MinecraftName { get; set; }
    public string? MinecraftUuid { get; set; }
    /// <summary>Локальный профиль без авторизации Microsoft; не подтверждает покупку игры.</summary>
    public bool IsLocal { get; set; }
    public bool OwnsGame { get; set; }
    /// <summary>Когда последний раз онлайн подтверждено право на игру.</summary>
    public DateTime? OwnershipVerifiedUtc { get; set; }
    public string? MinecraftAccessToken { get; set; }
    public DateTimeOffset? MinecraftTokenExpiresAt { get; set; }

    public bool HasProfile => MinecraftUuid is not null && MinecraftName is not null;
    public string DisplayName => MinecraftName ?? MicrosoftUsername ?? "Аккаунт Microsoft";
}

/// <summary>
/// Хранилище аккаунтов: весь файл шифруется Windows DPAPI (CurrentUser) — расшифровать его может
/// только тот же пользователь Windows на этом компьютере. Собственная криптография не используется.
/// </summary>
public sealed class AccountStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UltraLauncher.Accounts.v1");
    private readonly string _path;
    private readonly ILauncherLog _log;
    private readonly object _lock = new();
    private List<StoredAccount>? _accounts;

    public AccountStore(string path, ILauncherLog log)
    {
        _path = path;
        _log = log;
    }

    public IReadOnlyList<StoredAccount> GetAll()
    {
        lock (_lock) return [.. EnsureLoaded()];
    }

    public StoredAccount? Find(string? id)
    {
        if (id is null) return null;
        lock (_lock) return EnsureLoaded().FirstOrDefault(a => a.Id == id);
    }

    public void Upsert(StoredAccount account)
    {
        lock (_lock)
        {
            var list = EnsureLoaded();
            list.RemoveAll(a => a.Id == account.Id);
            list.Insert(0, account);
            Persist(list);
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            var list = EnsureLoaded();
            list.RemoveAll(a => a.Id == id);
            Persist(list);
        }
    }

    private List<StoredAccount> EnsureLoaded()
    {
        if (_accounts is not null) return _accounts;
        _accounts = [];
        if (!File.Exists(_path)) return _accounts;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            _accounts = JsonSerializer.Deserialize<List<StoredAccount>>(plain) ?? [];
            foreach (var a in _accounts) SecretRedactor.Register(a.MinecraftAccessToken);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // Файл повреждён или создан другим пользователем Windows — начинаем с пустого списка.
            var broken = _path + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Move(_path, broken, overwrite: true); } catch { }
            _log.Warn("Accounts", $"Не удалось прочитать сохранённые аккаунты ({ex.GetType().Name}); потребуется повторный вход.");
        }
        return _accounts;
    }

    private void Persist(List<StoredAccount> list)
    {
        if (list.Count == 0)
        {
            if (File.Exists(_path)) File.Delete(_path);
            return;
        }
        var plain = JsonSerializer.SerializeToUtf8Bytes(list);
        var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plain);
        AtomicFile.WriteAllBytes(_path, protectedBytes);
    }
}
