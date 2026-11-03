using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;
using WeddingRsvp.Services.Sms;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Tests;

public sealed class PhoneVerificationTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeSms : ISmsProvider
    {
        public List<SmsMessage> Sent = [];
        public SmsSendResult Result = SmsSendResult.Ok("x");
        public string Name => "Fake";
        public Task<SmsSendResult> SendAsync(SmsMessage m, CancellationToken ct) { if (Result.Success) Sent.Add(m); return Task.FromResult(Result); }
        public Task<int?> GetBalanceAsync(CancellationToken ct) => Task.FromResult<int?>(null);
        public SmsCostRule CostRule(bool unicode) => unicode ? new(70, 67) : new(160, 153);
    }

    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Clock _time = new();
    private readonly FakeSms _sms = new();
    private readonly IDataProtectionProvider _dp = new EphemeralDataProtectionProvider();
    private readonly int _a;
    private const string Phone = "+972501234567";

    public PhoneVerificationTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using var db = new AppDbContext(_options, new TenantContext());
        db.Database.EnsureCreated();
        var a = new Couple { DisplayName = "A" };
        db.Couples.Add(a);
        db.SaveChanges();
        _a = a.Id;
        Grant(10);
    }

    public void Dispose() => _conn.Dispose();

    private void Grant(int credits)
    {
        var t = new TenantContext(); t.Use(_a);
        using var db = new AppDbContext(_options, t);
        db.SmsAllocations.Add(new SmsAllocationEntry { Delta = credits, Reason = "grant", AtUtc = DateTime.UtcNow });
        db.SaveChanges();
    }

    private (PhoneVerificationService Svc, AppDbContext Db, AllocationService Alloc, ProviderCircuitBreaker Breaker) Make()
    {
        var t = new TenantContext(); t.Use(_a);
        var db = new AppDbContext(_options, t);
        var clock = new IsraelClock(_time);
        var audit = new AuditLog(db, clock, new HttpContextAccessor());
        var alloc = new AllocationService(db, clock, audit);
        var breaker = new ProviderCircuitBreaker(db, clock, audit);
        var svc = new PhoneVerificationService(db, _sms, new SettingsService(db, t), alloc, breaker, clock, t, _dp, NullLogger<PhoneVerificationService>.Instance);
        return (svc, db, alloc, breaker);
    }

    private string LastCode() => Regex.Match(_sms.Sent[^1].Body, @"\d{6}").Value;

    [Fact]
    public async Task A_code_is_sent_costs_one_credit_and_the_right_code_gives_a_proof_for_that_phone_only()
    {
        var (svc, db, alloc, _) = Make();
        using var _ = db;
        Assert.Equal(VerifySendKind.Sent, (await svc.SendAsync(Phone, he: false)).Kind);
        Assert.Single(_sms.Sent);
        Assert.Equal(9, await alloc.BalanceAsync());

        var wrong = await svc.CheckAsync(Phone, LastCode() == "000000" ? "111111" : "000000");
        Assert.Equal(VerifyCheckKind.Wrong, wrong.Kind);
        var ok = await svc.CheckAsync(Phone, LastCode());
        Assert.Equal(VerifyCheckKind.Ok, ok.Kind);
        Assert.True(svc.ProofIsValid(ok.Proof, Phone));
        Assert.False(svc.ProofIsValid(ok.Proof, "+972509999999"));
        Assert.False(svc.ProofIsValid("garbage", Phone));
        Assert.Equal(VerifyCheckKind.Expired, (await svc.CheckAsync(Phone, LastCode())).Kind); // a code works once
    }

    [Fact]
    public async Task The_code_text_follows_the_page_language_and_the_stored_value_is_only_a_hash()
    {
        var (svc, db, _, _) = Make();
        using var _ = db;
        await svc.SendAsync(Phone, he: true);
        Assert.Contains("קוד האימות", _sms.Sent[0].Body);
        var row = await db.PhoneVerifications.SingleAsync();
        Assert.DoesNotContain(LastCode(), row.CodeHash);
    }

    [Fact]
    public async Task Five_wrong_tries_lock_the_code_and_it_expires_after_ten_minutes()
    {
        var (svc, db, _, _) = Make();
        using var _ = db;
        await svc.SendAsync(Phone, false);
        var good = LastCode(); var bad = good == "123456" ? "654321" : "123456";
        for (var i = 0; i < 4; i++) Assert.Equal(VerifyCheckKind.Wrong, (await svc.CheckAsync(Phone, bad)).Kind);
        Assert.Equal(VerifyCheckKind.Locked, (await svc.CheckAsync(Phone, bad)).Kind);
        Assert.Equal(VerifyCheckKind.Locked, (await svc.CheckAsync(Phone, good)).Kind); // even the right code, once locked

        _time.Now = _time.Now.AddMinutes(2);
        Assert.Equal(VerifySendKind.Sent, (await svc.SendAsync(Phone, false)).Kind); // a new code resets it
        _time.Now = _time.Now.AddMinutes(11);
        Assert.Equal(VerifyCheckKind.Expired, (await svc.CheckAsync(Phone, LastCode())).Kind);
    }

    [Fact]
    public async Task Resending_waits_a_minute_and_a_number_gets_at_most_five_codes_an_hour()
    {
        var (svc, db, _, _) = Make();
        using var _ = db;
        Assert.Equal(VerifySendKind.Sent, (await svc.SendAsync(Phone, false)).Kind);
        var again = await svc.SendAsync(Phone, false);
        Assert.Equal(VerifySendKind.Cooldown, again.Kind);
        Assert.InRange(again.WaitSeconds, 1, 60);
        Assert.Single(_sms.Sent);

        for (var i = 0; i < 4; i++) { _time.Now = _time.Now.AddSeconds(61); Assert.Equal(VerifySendKind.Sent, (await svc.SendAsync(Phone, false)).Kind); }
        _time.Now = _time.Now.AddSeconds(61);
        Assert.Equal(VerifySendKind.TooMany, (await svc.SendAsync(Phone, false)).Kind);
    }

    [Fact]
    public async Task With_no_credits_left_or_a_failing_provider_nothing_is_sent_or_charged_and_verification_is_unavailable()
    {
        var (svc, db, alloc, breaker) = Make();
        using var _ = db;
        var t = new TenantContext(); t.Use(_a);
        db.SmsAllocations.Add(new SmsAllocationEntry { Delta = -10, Reason = "spent", AtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Assert.Equal(VerifySendKind.Unavailable, (await svc.SendAsync(Phone, false)).Kind);
        Assert.Empty(_sms.Sent);

        Grant(5);
        _sms.Result = SmsSendResult.AccountLevel("no credit at provider");
        Assert.Equal(VerifySendKind.Unavailable, (await svc.SendAsync(Phone, false)).Kind);
        Assert.True(await breaker.IsOpenAsync());
        Assert.Equal(5, await alloc.BalanceAsync()); // nothing charged
        Assert.Empty(await db.PhoneVerifications.ToListAsync());
    }

    [Fact]
    public async Task Group_link_verification_mode_defaults_off_and_is_saved_per_link()
    {
        var t = new TenantContext(); t.Use(_a);
        using var db = new AppDbContext(_options, t);
        var clock = new IsraelClock(_time);
        var settings = new SettingsService(db, t);
        var svc = new GroupLinkService(db, settings, new RsvpService(db, settings, clock, t), clock, t);
        var plain = await svc.CreateAsync("A", 4, null);
        var strict = await svc.CreateAsync("B", 4, null, GroupVerifyMode.Always);
        Assert.Equal(GroupVerifyMode.Off, plain.VerifyMode);
        Assert.Equal(GroupVerifyMode.Always, strict.VerifyMode);
        await svc.UpdateAsync(plain.Id, "A", 4, null, GroupVerifyMode.OnEdit);
        Assert.Equal(GroupVerifyMode.OnEdit, (await svc.GetAsync(plain.Id))!.VerifyMode);
        Assert.Equal(GroupVerifyMode.Always, (await svc.GetAsync(strict.Id))!.VerifyMode);
    }
}
