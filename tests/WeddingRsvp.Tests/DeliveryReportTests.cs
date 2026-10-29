using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Services;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Tests;

/// <summary>Provider delivery reports (form fields to, status, msgId) against a real SQLite DB.</summary>
public sealed class DeliveryReportTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly int _couple;

    public DeliveryReportTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        using var db = new AppDbContext(_options, new TenantContext());
        db.Database.EnsureCreated();
        var c = new Couple { DisplayName = "A" };
        db.Couples.Add(c);
        db.SaveChanges();
        _couple = c.Id;
        using var t = Db();
        t.Messages.Add(new MessageLog { Kind = MessageKind.Test, Status = MessageStatus.Sent, ToE164 = "+972501234567", Body = "x" });
        t.SaveChanges();
    }

    private AppDbContext Db()
    {
        var t = new TenantContext();
        t.Use(_couple);
        return new AppDbContext(_options, t);
    }

    private async Task<DeliveryReportResult> ReportAsync(string? msgId, string? to, string? status)
    {
        // The webhook has no tenant: the message id decides the row.
        using var db = new AppDbContext(_options, new TenantContext());
        var reports = new DeliveryReports(db, new IsraelClock(TimeProvider.System), NullLogger<DeliveryReports>.Instance);
        return await reports.ApplyAsync(msgId, to, status);
    }

    private async Task<MessageLog> MessageAsync()
    {
        using var db = Db();
        return await db.Messages.SingleAsync();
    }

    [Theory]
    [InlineData("0501234567")]
    [InlineData("972501234567")]
    [InlineData("+972501234567")]
    public async Task Status_1_marks_a_sent_message_delivered_whatever_the_phone_format(string to)
    {
        var id = (await MessageAsync()).Id;
        Assert.Equal(DeliveryReportResult.Delivered, await ReportAsync(id.ToString(), to, "1"));
        var m = await MessageAsync();
        Assert.Equal(MessageStatus.Delivered, m.Status);
        Assert.NotNull(m.DeliveredAtUtc);
    }

    [Fact]
    public async Task Status_5_marks_a_sent_message_failed()
    {
        var id = (await MessageAsync()).Id;
        Assert.Equal(DeliveryReportResult.NotDelivered, await ReportAsync(id.ToString(), "0501234567", "5"));
        var m = await MessageAsync();
        Assert.Equal(MessageStatus.Failed, m.Status);
        Assert.Contains("Not delivered", m.Error);
    }

    [Fact]
    public async Task A_late_report_never_downgrades_an_opened_message()
    {
        var id = (await MessageAsync()).Id;
        using (var db = Db())
        {
            var m = await db.Messages.SingleAsync();
            m.Status = MessageStatus.Opened;
            await db.SaveChangesAsync();
        }
        await ReportAsync(id.ToString(), "0501234567", "5");
        await ReportAsync(id.ToString(), "0501234567", "1");
        Assert.Equal(MessageStatus.Opened, (await MessageAsync()).Status);
    }

    [Theory]
    [InlineData("6")]    // kosher-phone voice call: not a delivery verdict
    [InlineData("99")]
    [InlineData(null)]
    public async Task Other_statuses_change_nothing(string? status)
    {
        var id = (await MessageAsync()).Id;
        Assert.Equal(DeliveryReportResult.Ignored, await ReportAsync(id.ToString(), "0501234567", status));
        Assert.Equal(MessageStatus.Sent, (await MessageAsync()).Status);
    }

    [Fact]
    public async Task A_report_without_a_recipient_changes_nothing()
    {
        var id = (await MessageAsync()).Id;
        Assert.Equal(DeliveryReportResult.UnknownMessage, await ReportAsync(id.ToString(), null, "5"));
        Assert.Equal(MessageStatus.Sent, (await MessageAsync()).Status);
    }

    [Theory]
    [InlineData("nope", "0501234567")]
    [InlineData("9999", "0501234567")]
    [InlineData(null, null)]
    public async Task Unknown_message_ids_are_ignored(string? msgId, string? to)
    {
        Assert.Equal(DeliveryReportResult.UnknownMessage, await ReportAsync(msgId, to, "1"));
        Assert.Equal(MessageStatus.Sent, (await MessageAsync()).Status);
    }

    [Fact]
    public async Task A_report_whose_phone_does_not_match_the_message_is_ignored()
    {
        var id = (await MessageAsync()).Id;
        Assert.Equal(DeliveryReportResult.UnknownMessage, await ReportAsync(id.ToString(), "0509999999", "1"));
        Assert.Equal(MessageStatus.Sent, (await MessageAsync()).Status);
    }

    public void Dispose() => _conn.Dispose();
}
