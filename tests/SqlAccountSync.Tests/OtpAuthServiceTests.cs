using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Secrets;
using SqlAccountSync.Services.Tenants;

namespace SqlAccountSync.Tests;

public sealed class OtpAuthServiceTests
{
    [Fact]
    public async Task SendOtp_UnknownEmail_DoesNotEnumerate()
    {
        var sut = CreateSut(user: null, isDevelopment: false);
        var (ok, msg, debug) = await sut.SendOtpAsync("nobody@example.com", "127.0.0.1");
        Assert.True(ok);
        Assert.Contains("If that email is registered", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Null(debug);
    }

    [Fact]
    public async Task GenerateOtp_IgnoresMockSignInCode_EvenInDev()
    {
        var user = ActiveUser();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 8; i++)
        {
            var sut = CreateSut(user, isDevelopment: true, appMode: "dev", allowReveal: true, mockCode: "123456");
            var (_, _, debug) = await sut.SendOtpAsync(user.Email, "127.0.0.1");
            Assert.False(string.IsNullOrWhiteSpace(debug));
            Assert.NotEqual("123456", debug!);
            Assert.Equal(6, debug!.Length);
            codes.Add(debug);
        }

        Assert.True(codes.Count >= 2, "Expected cryptographically random OTPs, not a fixed mock.");
    }

    [Fact]
    public async Task ProductionHosting_NeverReturnsDebugOtp()
    {
        var user = ActiveUser();
        var sut = CreateSut(user, isDevelopment: false, appMode: "dev", allowReveal: true);
        var (_, _, debug) = await sut.SendOtpAsync(user.Email, "127.0.0.1");
        Assert.Null(debug);
    }

    [Fact]
    public async Task PublicClientIp_NeverReturnsDebugOtp_EvenInDev()
    {
        var user = ActiveUser();
        var sut = CreateSut(user, isDevelopment: true, appMode: "dev", allowReveal: true);
        var (_, _, debug) = await sut.SendOtpAsync(user.Email, "203.0.113.10");
        Assert.Null(debug);
        Assert.False(sut.CanRevealDebugOtp("203.0.113.10"));
    }

    [Fact]
    public async Task DevReveal_RequiresExplicitAllowLocalOtpReveal()
    {
        var user = ActiveUser();
        var sut = CreateSut(user, isDevelopment: true, appMode: "dev", allowReveal: false);
        var (_, _, debug) = await sut.SendOtpAsync(user.Email, "127.0.0.1");
        Assert.Null(debug);
    }

    [Fact]
    public async Task Verify_SucceedsOnce_ThenFails()
    {
        var user = ActiveUser();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = CreateSut(user, isDevelopment: true, appMode: "dev", allowReveal: true, cache: cache);
        var (_, _, code) = await sut.SendOtpAsync(user.Email, "127.0.0.1");
        Assert.False(string.IsNullOrWhiteSpace(code));

        var first = await sut.VerifyAsync(user.Email, code!, "127.0.0.1");
        Assert.True(first.Ok);
        Assert.NotNull(first.User);

        var second = await sut.VerifyAsync(user.Email, code!, "127.0.0.1");
        Assert.False(second.Ok);
        Assert.Null(second.User);
    }

    [Fact]
    public async Task Verify_BruteForce_InvalidatesOtp()
    {
        var user = ActiveUser();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = CreateSut(user, isDevelopment: true, appMode: "dev", allowReveal: true, cache: cache);
        var (_, _, code) = await sut.SendOtpAsync(user.Email, "127.0.0.1");
        Assert.False(string.IsNullOrWhiteSpace(code));

        for (var i = 0; i < OtpAuthService.MaxVerifyFailures - 1; i++)
        {
            var fail = await sut.VerifyAsync(user.Email, "000000", "127.0.0.1");
            Assert.False(fail.Ok);
        }

        var lastFail = await sut.VerifyAsync(user.Email, "000000", "127.0.0.1");
        Assert.False(lastFail.Ok);
        Assert.Contains("Too many", lastFail.Message, StringComparison.OrdinalIgnoreCase);

        var after = await sut.VerifyAsync(user.Email, code!, "127.0.0.1");
        Assert.False(after.Ok);
    }

    [Fact]
    public async Task Verify_Concurrent_OnlyOneSucceeds()
    {
        var user = ActiveUser();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = CreateSut(user, isDevelopment: true, appMode: "dev", allowReveal: true, cache: cache);
        var (_, _, code) = await sut.SendOtpAsync(user.Email, "127.0.0.1");
        Assert.False(string.IsNullOrWhiteSpace(code));

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => sut.VerifyAsync(user.Email, code!, "127.0.0.1"))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r.Ok));
    }

    [Fact]
    public async Task Send_Cooldown_BlocksRapidResend()
    {
        var user = ActiveUser();
        var sut = CreateSut(user, isDevelopment: true, appMode: "dev", allowReveal: true);
        var first = await sut.SendOtpAsync(user.Email, "127.0.0.1");
        Assert.True(first.Ok);
        var second = await sut.SendOtpAsync(user.Email, "127.0.0.1");
        Assert.False(second.Ok);
        Assert.Contains("wait", second.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HmacPepper_ProducesDifferentDigestThanPlainSha256()
    {
        var pepper = new OtpHashPepper([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
        var hmac = pepper.HashOtp("123456");
        var plain = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("123456")));
        Assert.NotEqual(plain, hmac);
        Assert.Equal(hmac, pepper.HashOtp("123456"));
    }

    [Fact]
    public void HostHardening_RejectsStarInProduction()
    {
        var env = new FakeHostEnv(development: false);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "*" })
            .Build();
        Assert.Throws<InvalidOperationException>(() =>
            HostHardening.EnsureProductionAllowedHosts(env, config, NullLogger.Instance));
    }

    [Fact]
    public void HostHardening_AllowsExplicitProductionHosts()
    {
        var env = new FakeHostEnv(development: false);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "sync.example.com" })
            .Build();
        HostHardening.EnsureProductionAllowedHosts(env, config, NullLogger.Instance);
    }

    [Fact]
    public void ForwardedHeaders_ParsesCidr()
    {
        Assert.True(ForwardedHeadersSetup.TryParseCidr("10.0.0.0/8", out var network));
        Assert.Equal(System.Net.IPAddress.Parse("10.0.0.0"), network.Prefix);
        Assert.Equal(8, network.PrefixLength);
    }

    private static AppUser ActiveUser() => new()
    {
        UserId = 1,
        Email = "tester@example.com",
        DisplayName = "Tester",
        Role = "Admin",
        IsActive = true
    };

    private static OtpAuthService CreateSut(
        AppUser? user,
        bool isDevelopment,
        string appMode = "prod",
        bool allowReveal = false,
        string mockCode = "123456",
        IMemoryCache? cache = null)
    {
        var sync = Microsoft.Extensions.Options.Options.Create(new SyncOptions
        {
            AppMode = appMode,
            MockSignInCode = mockCode,
            DebugOtpToConsole = true,
            AllowLocalOtpReveal = allowReveal,
            OtpMinutes = 5
        });
        var smtp = Microsoft.Extensions.Options.Options.Create(new SmtpOptions());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var mem = cache ?? new MemoryCache(new MemoryCacheOptions());
        return new OtpAuthService(
            new FakeLoginUsers(user),
            mem,
            new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())),
            new OtpHashPepper([9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9]),
            new FakeSecrets(),
            new FakeTenantEmail(),
            config,
            smtp,
            sync,
            new FakeHostEnv(isDevelopment),
            NullLogger<OtpAuthService>.Instance);
    }

    private sealed class FakeLoginUsers(AppUser? user) : ILoginUserResolver
    {
        public Task<AppUser?> ResolveForLoginAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult(user);
    }

    private sealed class FakeSecrets : ISecretResolver
    {
        public Task<string?> ResolveAsync(string? secretRef, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FakeTenantEmail : ITenantEmailBootstrap
    {
        public Task<TenantEmailOverride?> GetEmailOverrideAsync(string tenantCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<TenantEmailOverride?>(null);
    }

    private sealed class FakeHostEnv(bool development) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = development ? Environments.Development : Environments.Production;
        public string ApplicationName { get; set; } = "SqlAccountSync.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
