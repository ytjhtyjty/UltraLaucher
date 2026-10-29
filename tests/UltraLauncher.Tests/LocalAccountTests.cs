using UltraLauncher.Core.Auth;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Logging;

namespace UltraLauncher.Tests;

public class LocalAccountTests
{
    private static AccountService Create(string path)
    {
        var log = new MemoryLog();
        var handler = new FakeHttpHandler((Func<HttpRequestMessage, int, HttpResponseMessage>)
            ((_, _) => throw new InvalidOperationException("Сеть не должна использоваться")));
        return new AccountService(
            () => throw new InvalidOperationException("Microsoft не должен использоваться"),
            new XboxMinecraftClient(new HttpClient(handler), log),
            new AccountStore(path, log), log, new OperationGuard());
    }

    [Fact]
    public async Task Create_WithoutMicrosoft_PersistsAndWorksInBothNetworkModes()
    {
        using var dir = new TempDir();
        var path = dir.Combine("accounts.dat");
        var service = Create(path);
        var account = service.CreateLocalAccount("  Steve  ");
        Assert.True(account.IsLocal);
        Assert.False(account.OwnsGame);
        Assert.Null(account.MinecraftAccessToken);
        Assert.Null(account.OwnershipVerifiedUtc);
        Assert.Equal(AccountStatus.Local, AccountService.StatusOf(account));
        Assert.Equal("Steve", account.MinecraftName);
        // Эталон Minecraft: UUID.nameUUIDFromBytes("OfflinePlayer:Steve".getBytes(UTF_8)).
        Assert.Equal("5627dd98e6be3c21b8a8e92344183641", account.MinecraftUuid);
        Assert.StartsWith("offline:", account.Id);

        var reloaded = Create(path);
        Assert.True(reloaded.Find(account.Id)!.IsLocal);
        foreach (var offline in new[] { false, true })
        {
            var player = await reloaded.GetPlayerAsync(account.Id, offline, CancellationToken.None);
            Assert.Equal("Steve", player.Name);
            Assert.Equal(account.MinecraftUuid, player.Uuid);
            Assert.Equal("0", player.AccessToken);
            Assert.Equal("legacy", player.UserType);
            Assert.False(player.IsDemo);
        }
    }

    [Fact]
    public async Task RefreshAndDelete_DoNotUseMicrosoft()
    {
        using var dir = new TempDir();
        var path = dir.Combine("accounts.dat");
        var service = Create(path);
        var account = service.CreateLocalAccount("Alex");
        Assert.Same(account, await service.RefreshAsync(account.Id, CancellationToken.None));
        await service.SignOutAsync(account.Id);
        Assert.Empty(service.Accounts);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    [InlineData("12345678901234567")]
    [InlineData("Player Name")]
    [InlineData("../Steve")]
    [InlineData("Стив")]
    [InlineData("Steve\nAlex")]
    public void InvalidName_IsRejected(string? name)
    {
        using var dir = new TempDir();
        var service = Create(dir.Combine("accounts.dat"));
        Assert.False(AccountService.IsValidLocalName(name));
        var ex = Assert.Throws<LauncherException>(() => service.CreateLocalAccount(name));
        Assert.Equal(ErrorCode.InvalidSettings, ex.Code);
        Assert.Empty(service.Accounts);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1234567890123456")]
    [InlineData("Player_123")]
    public void ValidName_IsAccepted(string name)
    {
        using var dir = new TempDir();
        var service = Create(dir.Combine("accounts.dat"));
        Assert.True(AccountService.IsValidLocalName(name));
        Assert.Equal(name, service.CreateLocalAccount(name).MinecraftName);
    }

    [Fact]
    public void DuplicateName_SelectsExistingProfile_AndDifferentNameGetsDifferentUuid()
    {
        using var dir = new TempDir();
        var service = Create(dir.Combine("accounts.dat"));
        var steve = service.CreateLocalAccount("Steve");
        Assert.Same(steve, service.CreateLocalAccount(" steve "));
        Assert.Single(service.Accounts);
        var alex = service.CreateLocalAccount("Alex");
        Assert.NotEqual(steve.Id, alex.Id);
        Assert.NotEqual(steve.MinecraftUuid, alex.MinecraftUuid);
    }

    [Fact]
    public void ExistingMicrosoftAccount_RemainsMicrosoft()
    {
        using var dir = new TempDir();
        var path = dir.Combine("accounts.dat");
        var store = new AccountStore(path, new MemoryLog());
        store.Upsert(new StoredAccount { Id = "msa-1", MinecraftName = "Steve", OwnsGame = true });
        var service = Create(path);
        var local = service.CreateLocalAccount("Steve");
        var microsoft = service.Find("msa-1")!;
        Assert.False(microsoft.IsLocal);
        Assert.True(microsoft.OwnsGame);
        Assert.Equal(AccountStatus.Licensed, AccountService.StatusOf(microsoft));
        Assert.NotEqual(microsoft.Id, local.Id);
        Assert.Equal(2, service.Accounts.Count);
    }
}
