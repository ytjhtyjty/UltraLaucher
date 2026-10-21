using System.Net;
using System.Text;
using System.Text.Json;
using UltraLauncher.Core.Auth;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Logging;

namespace UltraLauncher.Tests;

/// <summary>
/// Тесты разбора ответов и сценариев авторизации на ПОДСТАВНЫХ ответах серверов.
/// Они проверяют логику лаунчера, а не реальный вход Microsoft.
/// </summary>
public class AuthFlowMockTests
{
    private sealed class FakeIdentity : IMicrosoftIdentity
    {
        public bool IsConfigured => true;
        public bool SilentAvailable { get; set; } = true;
        public List<string> Removed { get; } = [];

        public Task<MicrosoftToken> SignInInteractiveAsync(CancellationToken ct)
            => Task.FromResult(new MicrosoftToken("ms-access-token-abcdef", "acc-1", "player@example.com", DateTimeOffset.UtcNow.AddHours(1)));

        public Task<MicrosoftToken?> AcquireSilentAsync(string accountId, CancellationToken ct)
            => Task.FromResult(SilentAvailable ? new MicrosoftToken("ms-access-token-2", accountId, "player@example.com", DateTimeOffset.UtcNow.AddHours(1)) : null);

        public Task RemoveAsync(string accountId)
        {
            Removed.Add(accountId);
            return Task.CompletedTask;
        }
    }

    private static FakeHttpHandler Services(bool ownsGame = true, bool hasProfile = true, long? xstsError = null, HttpStatusCode mcLogin = HttpStatusCode.OK)
        => new((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url == XboxMinecraftClient.XboxUserAuthUrl)
                return FakeHttpHandler.Json("""{"Token":"xbl-token-12345678","DisplayClaims":{"xui":[{"uhs":"userhash"}]}}""");
            if (url == XboxMinecraftClient.XstsAuthUrl)
                return xstsError is null
                    ? FakeHttpHandler.Json("""{"Token":"xsts-token-12345678","DisplayClaims":{"xui":[{"uhs":"userhash"}]}}""")
                    : FakeHttpHandler.Json($$"""{"Identity":"0","XErr":{{xstsError}},"Message":""}""", HttpStatusCode.Unauthorized);
            if (url == XboxMinecraftClient.MinecraftLoginUrl)
                return mcLogin == HttpStatusCode.OK
                    ? FakeHttpHandler.Json("""{"access_token":"mc-access-token-0123456789","expires_in":86400}""")
                    : FakeHttpHandler.Status(mcLogin);
            if (url == XboxMinecraftClient.EntitlementsUrl)
                return FakeHttpHandler.Json(ownsGame ? """{"items":[{"name":"product_minecraft"},{"name":"game_minecraft"}]}""" : """{"items":[]}""");
            if (url == XboxMinecraftClient.ProfileUrl)
                return hasProfile ? FakeHttpHandler.Json("""{"id":"0123456789abcdef0123456789abcdef","name":"Steve"}""") : FakeHttpHandler.Status(HttpStatusCode.NotFound);
            return FakeHttpHandler.Status(HttpStatusCode.NotFound);
        });

    private static (AccountService service, AccountStore store, FakeIdentity identity) Create(TempDir dir, FakeHttpHandler handler)
    {
        var log = new MemoryLog();
        var store = new AccountStore(dir.Combine("accounts.dat"), log);
        var identity = new FakeIdentity();
        var service = new AccountService(() => identity, new XboxMinecraftClient(new HttpClient(handler), log), store, log, new OperationGuard());
        return (service, store, identity);
    }

    [Fact]
    public async Task SignIn_WithGame_IsLicensedAndUsableOffline()
    {
        using var dir = new TempDir();
        var (service, _, _) = Create(dir, Services());

        var acc = await service.SignInAsync(CancellationToken.None);

        Assert.Equal(AccountStatus.Licensed, AccountService.StatusOf(acc));
        Assert.Equal("Steve", acc.MinecraftName);
        var online = await service.GetPlayerAsync(acc.Id, offline: false, CancellationToken.None);
        Assert.False(online.IsDemo);
        Assert.Equal("mc-access-token-0123456789", online.AccessToken);
        var offline = await service.GetPlayerAsync(acc.Id, offline: true, CancellationToken.None);
        Assert.Equal("Steve", offline.Name);
    }

    [Fact]
    public async Task SignIn_WithoutGame_IsDemoOnly_AndOfflineIsRefused()
    {
        using var dir = new TempDir();
        var (service, _, _) = Create(dir, Services(ownsGame: false, hasProfile: false));

        var acc = await service.SignInAsync(CancellationToken.None);

        Assert.Equal(AccountStatus.DemoOnly, AccountService.StatusOf(acc));
        var player = await service.GetPlayerAsync(acc.Id, offline: false, CancellationToken.None);
        Assert.True(player.IsDemo);
        var ex = await Assert.ThrowsAsync<LauncherException>(() => service.GetPlayerAsync(acc.Id, offline: true, CancellationToken.None));
        Assert.Equal(ErrorCode.AccountRequired, ex.Code);
    }

    [Fact]
    public async Task SignIn_GameWithoutProfile_ExplainsWhatToDo()
    {
        using var dir = new TempDir();
        var (service, _, _) = Create(dir, Services(ownsGame: true, hasProfile: false));
        var ex = await Assert.ThrowsAsync<LauncherException>(() => service.SignInAsync(CancellationToken.None));
        Assert.Equal(ErrorCode.AuthNoProfile, ex.Code);
        Assert.Contains("minecraft.net", ex.Hint);
    }

    [Theory]
    [InlineData(2148916233L, ErrorCode.AuthNoXboxAccount)]
    [InlineData(2148916238L, ErrorCode.AuthChildAccount)]
    [InlineData(2148916235L, ErrorCode.AuthRegionBlocked)]
    [InlineData(123L, ErrorCode.AuthFailed)]
    public async Task XstsErrors_AreMapped(long xerr, ErrorCode expected)
    {
        using var dir = new TempDir();
        var (service, _, _) = Create(dir, Services(xstsError: xerr));
        var ex = await Assert.ThrowsAsync<LauncherException>(() => service.SignInAsync(CancellationToken.None));
        Assert.Equal(expected, ex.Code);
    }

    [Fact]
    public async Task UnapprovedApp_403_IsExplained()
    {
        using var dir = new TempDir();
        var (service, _, _) = Create(dir, Services(mcLogin: HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<LauncherException>(() => service.SignInAsync(CancellationToken.None));
        Assert.Equal(ErrorCode.AuthAppNotApproved, ex.Code);
    }

    [Fact]
    public async Task ExpiredSession_RequiresRelogin()
    {
        using var dir = new TempDir();
        var (service, store, identity) = Create(dir, Services());
        var acc = await service.SignInAsync(CancellationToken.None);
        acc.MinecraftTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        store.Upsert(acc);
        identity.SilentAvailable = false;

        var ex = await Assert.ThrowsAsync<LauncherException>(() => service.GetPlayerAsync(acc.Id, offline: false, CancellationToken.None));
        Assert.Equal(ErrorCode.AuthExpired, ex.Code);
    }

    [Fact]
    public async Task ExpiringToken_IsRefreshedSilently()
    {
        using var dir = new TempDir();
        var handler = Services();
        var (service, store, _) = Create(dir, handler);
        var acc = await service.SignInAsync(CancellationToken.None);
        acc.MinecraftTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        store.Upsert(acc);

        await service.GetPlayerAsync(acc.Id, offline: false, CancellationToken.None);

        Assert.Equal(2, handler.CountFor(XboxMinecraftClient.MinecraftLoginUrl));
    }

    [Fact]
    public async Task SignOut_RemovesLocalData()
    {
        using var dir = new TempDir();
        var (service, store, identity) = Create(dir, Services());
        var acc = await service.SignInAsync(CancellationToken.None);
        await service.SignOutAsync(acc.Id);
        Assert.Empty(store.GetAll());
        Assert.Contains(acc.Id, identity.Removed);
        Assert.False(File.Exists(dir.Combine("accounts.dat")));
    }

    [Fact]
    public async Task NoAccount_LaunchRequiresSignIn()
    {
        using var dir = new TempDir();
        var (service, _, _) = Create(dir, Services());
        var ex = await Assert.ThrowsAsync<LauncherException>(() => service.GetPlayerAsync(null, false, CancellationToken.None));
        Assert.Equal(ErrorCode.AccountRequired, ex.Code);
    }

    [Fact]
    public async Task Tokens_DoNotAppearInLogs()
    {
        using var dir = new TempDir();
        var log = new MemoryLog();
        var store = new AccountStore(dir.Combine("accounts.dat"), log);
        var service = new AccountService(() => new FakeIdentity(), new XboxMinecraftClient(new HttpClient(Services()), log), store, log, new OperationGuard());
        await service.SignInAsync(CancellationToken.None);
        log.Write(LogLevel.Info, "test", "token mc-access-token-0123456789 xbl-token-12345678");
        Assert.All(log.Entries, e =>
        {
            Assert.DoesNotContain("mc-access-token-0123456789", e.Message);
            Assert.DoesNotContain("xbl-token-12345678", e.Message);
        });
    }

    [Fact]
    public void Entitlements_Parsing()
    {
        using var yes = JsonDocument.Parse("""{"items":[{"name":"game_minecraft","signature":"x"}]}""");
        using var bedrockOnly = JsonDocument.Parse("""{"items":[{"name":"product_minecraft_bedrock"}]}""");
        using var none = JsonDocument.Parse("""{"items":[]}""");
        Assert.True(XboxMinecraftClient.HasJavaEntitlement(yes.RootElement));
        Assert.False(XboxMinecraftClient.HasJavaEntitlement(bedrockOnly.RootElement));
        Assert.False(XboxMinecraftClient.HasJavaEntitlement(none.RootElement));
    }

    [Fact]
    public void Msal_NotConfigured_WithoutClientId()
    {
        Assert.False(new MsalMicrosoftIdentity(null, Path.GetTempPath(), "x", new MemoryLog()).IsConfigured);
        Assert.False(new MsalMicrosoftIdentity("not-a-guid", Path.GetTempPath(), "x", new MemoryLog()).IsConfigured);
        Assert.True(new MsalMicrosoftIdentity(Guid.NewGuid().ToString(), Path.GetTempPath(), "x", new MemoryLog()).IsConfigured);
    }
}

public class AccountStoreTests
{
    [Fact]
    public void File_IsEncrypted_AndRoundTrips()
    {
        using var dir = new TempDir();
        var path = dir.Combine("accounts.dat");
        var store = new AccountStore(path, new MemoryLog());
        store.Upsert(new StoredAccount { Id = "a1", MinecraftName = "Алекс", MinecraftAccessToken = "very-secret-token-value", OwnsGame = true, MinecraftUuid = "u" });

        var raw = File.ReadAllBytes(path);
        Assert.DoesNotContain("very-secret-token-value", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("Алекс", Encoding.UTF8.GetString(raw));

        var loaded = new AccountStore(path, new MemoryLog()).Find("a1");
        Assert.NotNull(loaded);
        Assert.Equal("very-secret-token-value", loaded!.MinecraftAccessToken);
        Assert.Equal("Алекс", loaded.MinecraftName);
    }

    [Fact]
    public void CorruptFile_IsMovedAside()
    {
        using var dir = new TempDir();
        var path = dir.Combine("accounts.dat");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var store = new AccountStore(path, new MemoryLog());
        Assert.Empty(store.GetAll());
        Assert.Single(Directory.GetFiles(dir.Path, "accounts.dat.broken-*"));
    }
}

public class ErrorTests
{
    [Fact]
    public void Translate_MapsCommonFailures()
    {
        Assert.Equal(ErrorCode.Network, ErrorTranslator.Translate(new HttpRequestException("no host")).Code);
        Assert.Equal(ErrorCode.AccessDenied, ErrorTranslator.Translate(new UnauthorizedAccessException()).Code);
        Assert.Equal(ErrorCode.DiskFull, ErrorTranslator.Translate(new IOException("disk full", unchecked((int)0x80070070))).Code);
        var le = new LauncherException(ErrorCode.JavaNotFound, "x");
        Assert.Same(le, ErrorTranslator.Translate(le));
        Assert.NotNull(ErrorTranslator.Translate(new InvalidOperationException("boom")).Hint);
    }

    [Fact]
    public void IsTransient_Classification()
    {
        Assert.True(ErrorTranslator.IsTransient(new HttpRequestException("reset")));
        Assert.True(ErrorTranslator.IsTransient(new HttpRequestException("x", null, HttpStatusCode.BadGateway)));
        Assert.True(ErrorTranslator.IsTransient(new HttpRequestException("x", null, HttpStatusCode.TooManyRequests)));
        Assert.False(ErrorTranslator.IsTransient(new HttpRequestException("x", null, HttpStatusCode.NotFound)));
        Assert.False(ErrorTranslator.IsTransient(new UnauthorizedAccessException()));
    }

    [Fact]
    public void OperationGuard_BlocksDuplicateOperations()
    {
        var guard = new OperationGuard();
        using (guard.Begin("install:1.21", "busy"))
        {
            var ex = Assert.Throws<LauncherException>(() => guard.Begin("install:1.21", "уже идёт"));
            Assert.Equal(ErrorCode.OperationInProgress, ex.Code);
            using (guard.Begin("install:1.20", "busy")) { } // другая операция — можно
        }
        using (guard.Begin("install:1.21", "busy")) { } // после завершения — снова можно
    }

    [Fact]
    public void Redactor_RemovesTokensJwtAndBearer()
    {
        SecretRedactor.Register("registered-secret-42");
        var text = SecretRedactor.Redact("a registered-secret-42 --accessToken abc.def Bearer xyz123 eyJhbGciOi.eyJzdWIiOiIx.c2lnbmF0dXJl");
        Assert.DoesNotContain("registered-secret-42", text);
        Assert.DoesNotContain("abc.def", text);
        Assert.DoesNotContain("xyz123", text);
        Assert.DoesNotContain("eyJhbGciOi", text);
    }
}
