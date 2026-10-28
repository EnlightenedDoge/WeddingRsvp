using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;
using WeddingRsvp.Services;
using WeddingRsvp.Services.Sms;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Tests;

/// <summary>What the dispatcher does with each provider outcome: queue state, attempts, ledger, circuit breaker.</summary>
public sealed class DispatcherProviderTests : IDisposable
{
    private sealed class FixedTime : TimeProvider
    {
        // Wednesday 2026-10-28 09:00 UTC = 11:00 in Israel, inside the sending window.
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeProvider : ISmsProvider
    {
        public Queue<SmsSendResult> Results { get; } = new();
        public int Calls { get; private set; }
        public string Name => "Fake";
        public Task<SmsSendResult> SendAsync(SmsMessage message, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : SmsSendResult.Ok());
        }
        public Task<int?> GetBalanceAsync(CancellationToken ct) => Task.FromResult<int?>(100);
    }

    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly FakeProvider _provider = new();
    private readonly int _a, _b;

    public DispatcherProviderTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using var db = new AppDbContext(_options, new TenantContext());
        db.Database.EnsureCreated();
        var a = new Couple { DisplayName = "A" };
        var b = new Couple { DisplayName = "B" };
        db.Couples.AddRange(a, b);
        db.SaveChanges();
        _a = a.Id;
        _b = b.Id;
        foreach (var id in new[] { _a, _b })
        {
            using var t = As(id);
            t.SmsAllocations.Add(new SmsAllocationEntry { Delta = 10, Reason = "granted", AtUtc = DateTime.UtcNow });
            t.Messages.Add(new MessageLog { Kind = MessageKind.Test, ToE164 = "+972501234567", Body = "hi" });
            t.Messages.Add(new MessageLog { Kind = MessageKind.Test, ToE164 = "+972501234568", Body = "hi" });
            t.SaveChanges();
        }
    }

    private AppDbContext As(int coupleId) => As(coupleId, out _);

    private AppDbContext As(int coupleId, out TenantContext tenant)
    {
        tenant = new TenantContext();
        tenant.Use(coupleId);
        return new AppDbContext(_options, tenant);
    }

    private static readonly IsraelClock Clock = new(new FixedTime());

    private static (MessageDispatcher d, ProviderCircuitBreaker breaker) Build(AppDbContext db, TenantContext tenant, ISmsProvider provider)
    {
        var sendOpts = Microsoft.Extensions.Options.Options.Create(new SendingOptions());
        var audit = new AuditLog(db, Clock, new HttpContextAccessor());
        var breaker = new ProviderCircuitBreaker(db, Clock, audit);
        var d = new MessageDispatcher(db, new SettingsService(db, tenant), provider, new SendingWindow(sendOpts),
            breaker, new AllocationService(db, Clock, audit), Clock, sendOpts, NullLogger<MessageDispatcher>.Instance);
        return (d, breaker);
    }

    private async Task<int> RunAsync(int coupleId)
    {
        using var db = As(coupleId, out var tenant);
        return await Build(db, tenant, _provider).d.DispatchAsync();
    }

    [Fact]
    public async Task Sent_marks_sent_and_debits_the_ledger()
    {
        Assert.Equal(2, await RunAsync(_a));
        using var db = As(_a);
        Assert.All(await db.Messages.ToListAsync(), m => Assert.Equal(MessageStatus.Sent, m.Status));
        Assert.Equal(8, await db.SmsAllocations.SumAsync(e => e.Delta));
        Assert.Equal(2, await db.SmsAllocations.CountAsync(e => e.MessageLogId != null));
    }

    [Fact]
    public async Task Rejected_fails_without_retry_or_debit()
    {
        _provider.Results.Enqueue(SmsSendResult.Rejected("bad number"));
        await RunAsync(_a);
        using var db = As(_a);
        var first = await db.Messages.OrderBy(m => m.Id).FirstAsync();
        Assert.Equal(MessageStatus.Failed, first.Status);
        Assert.Equal(9, await db.SmsAllocations.SumAsync(e => e.Delta)); // only the second message was debited
    }

    [Fact]
    public async Task Unknown_fails_and_is_never_retried()
    {
        _provider.Results.Enqueue(SmsSendResult.Unknown("timeout"));
        await RunAsync(_a);
        var callsAfterFirst = _provider.Calls;
        await RunAsync(_a);
        Assert.Equal(callsAfterFirst, _provider.Calls); // nothing left queued to retry
        using var db = As(_a);
        var first = await db.Messages.OrderBy(m => m.Id).FirstAsync();
        Assert.Equal(MessageStatus.Failed, first.Status);
        Assert.Contains("timeout", first.Error);
    }

    [Fact]
    public async Task Transient_stays_queued_with_backoff_and_consumes_an_attempt()
    {
        _provider.Results.Enqueue(SmsSendResult.Transient("dns"));
        await RunAsync(_a);
        using var db = As(_a);
        var first = await db.Messages.OrderBy(m => m.Id).FirstAsync();
        Assert.Equal(MessageStatus.Queued, first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.True(first.NotBeforeUtc > new DateTime(2026, 10, 28, 9, 0, 0));
    }

    [Fact]
    public async Task AccountLevel_keeps_the_message_queued_untouched_and_stops_everyone()
    {
        _provider.Results.Enqueue(SmsSendResult.AccountLevel("-4: no balance"));
        Assert.Equal(0, await RunAsync(_a));
        using (var db = As(_a))
        {
            var msgs = await db.Messages.OrderBy(m => m.Id).ToListAsync();
            Assert.All(msgs, m => { Assert.Equal(MessageStatus.Queued, m.Status); Assert.Equal(0, m.Attempts); });
            Assert.Equal(10, await db.SmsAllocations.SumAsync(e => e.Delta));
            Assert.True((await db.PlatformStates.SingleAsync()).ProviderOpen);
        }
        Assert.Equal(1, _provider.Calls); // the second message was not even tried

        Assert.Equal(0, await RunAsync(_b)); // another couple is stopped too
        Assert.Equal(1, _provider.Calls);

        using (var db = As(_a, out var tenant))
            await Build(db, tenant, _provider).breaker.ResumeAsync();
        Assert.Equal(2, await RunAsync(_b));
    }

    [Fact]
    public async Task Nothing_is_sent_without_allocation()
    {
        using (var db = As(_a))
        {
            db.SmsAllocations.Add(new SmsAllocationEntry { Delta = -10, Reason = "reduced", AtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, await RunAsync(_a));
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task Only_as_many_messages_as_the_allocation_covers_are_sent()
    {
        using (var db = As(_a))
        {
            db.SmsAllocations.Add(new SmsAllocationEntry { Delta = -9, Reason = "reduced", AtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await RunAsync(_a));
        using var check = As(_a);
        Assert.Equal(1, await check.Messages.CountAsync(m => m.Status == MessageStatus.Queued));
    }

    [Fact]
    public async Task Couples_cannot_see_each_others_ledger()
    {
        using var db = As(_a);
        Assert.Equal(10, await new AllocationService(db, Clock, new AuditLog(db, Clock, new HttpContextAccessor())).BalanceAsync());
        Assert.All(await db.SmsAllocations.ToListAsync(), e => Assert.Equal(_a, e.CoupleId));
    }

    public void Dispose() => _conn.Dispose();
}
