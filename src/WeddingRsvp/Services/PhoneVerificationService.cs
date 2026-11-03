using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Services.Sms;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Services;

public enum VerifySendKind
{
    /// <summary>A new code went out.</summary>
    Sent,
    /// <summary>A code was sent a moment ago: wait <see cref="VerifySend.WaitSeconds"/> before asking for another.</summary>
    Cooldown,
    /// <summary>Too many codes for this number in the last hour.</summary>
    TooMany,
    /// <summary>Nothing could be sent (provider down, no credits left, or the provider refused): the visitor is told to contact the couple.</summary>
    Unavailable,
}

public sealed record VerifySend(VerifySendKind Kind, int WaitSeconds = 0);

public enum VerifyCheckKind { Ok, Wrong, Expired, Locked }

/// <param name="Proof">On success: a signed, short-lived token saying this phone was verified (carried by the form, checked again on submit).</param>
public sealed record VerifyCheck(VerifyCheckKind Kind, string? Proof = null);

/// <summary>
/// SMS code check for group links. A code is 6 digits, valid 10 minutes, 5 tries. It goes out through the active SMS provider and costs the
/// couple 1 credit from their allocation (only when the provider accepted it). Nothing is sent while the provider is down or the allocation
/// is used up: verification then fails closed and the visitor is sent to the couple.
/// </summary>
public sealed class PhoneVerificationService(
    AppDbContext db, ISmsProvider sender, SettingsService settings, AllocationService allocation, ProviderCircuitBreaker breaker,
    IsraelClock clock, ITenantContext tenant, IDataProtectionProvider dataProtection, ILogger<PhoneVerificationService> log)
{
    public const int CodeLength = 6;
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ResendAfter = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ProofLifetime = TimeSpan.FromMinutes(30);
    public const int MaxAttempts = 5;
    public const int MaxCodesPerHour = 5;

    private ITimeLimitedDataProtector Protector => dataProtection.CreateProtector("group-phone-proof").ToTimeLimitedDataProtector();

    public static string Body(string code, bool he) =>
        he ? $"קוד האימות שלך: {code}" : $"Your verification code: {code}";

    public async Task<VerifySend> SendAsync(string e164, bool he, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var recent = await db.PhoneVerifications.Where(v => v.PhoneE164 == e164 && v.SentAtUtc > now.AddHours(-1))
            .OrderByDescending(v => v.Id).ToListAsync(ct);
        if (recent.Count > 0 && now - recent[0].SentAtUtc < ResendAfter)
            return new(VerifySendKind.Cooldown, (int)Math.Ceiling((ResendAfter - (now - recent[0].SentAtUtc)).TotalSeconds));
        if (recent.Count >= MaxCodesPerHour) return new(VerifySendKind.TooMany);

        if (await breaker.IsOpenAsync(ct)) return new(VerifySendKind.Unavailable);
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var body = Body(code, he);
        var credits = sender.Credits(body);
        if (await allocation.BalanceAsync(ct) < credits) return new(VerifySendKind.Unavailable);

        var s = await settings.GetAsync(ct);
        var result = await sender.SendAsync(new SmsMessage(e164, body, string.IsNullOrWhiteSpace(s.SmsSenderName) ? null : s.SmsSenderName), ct);
        if (result.Kind == SmsFailureKind.AccountLevel)
        {
            await breaker.TripAsync(result.Error ?? "Provider refused the account", ct);
            return new(VerifySendKind.Unavailable);
        }
        if (!result.Success)
        {
            log.LogWarning("Verification code not sent ({Kind})", result.Kind);
            return new(VerifySendKind.Unavailable);
        }

        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        db.PhoneVerifications.Add(new PhoneVerification
        {
            PhoneE164 = e164, Salt = salt, CodeHash = Hash(code, salt), SentAtUtc = now, ExpiresAtUtc = now + CodeLifetime,
        });
        db.SmsAllocations.Add(new SmsAllocationEntry { Delta = -credits, Reason = "verification code", AtUtc = now });
        await db.SaveChangesAsync(ct);
        return new(VerifySendKind.Sent, (int)ResendAfter.TotalSeconds);
    }

    /// <summary>Seconds until another code may be requested for this number (0 = now).</summary>
    public async Task<int> WaitSecondsAsync(string e164, CancellationToken ct = default)
    {
        var last = await db.PhoneVerifications.Where(v => v.PhoneE164 == e164).OrderByDescending(v => v.Id).Select(v => (DateTime?)v.SentAtUtc).FirstOrDefaultAsync(ct);
        if (last is null) return 0;
        return Math.Max(0, (int)Math.Ceiling((ResendAfter - (clock.UtcNow - last.Value)).TotalSeconds));
    }

    public async Task<VerifyCheck> CheckAsync(string e164, string? code, CancellationToken ct = default)
    {
        var v = await db.PhoneVerifications.Where(x => x.PhoneE164 == e164 && x.ConsumedAtUtc == null)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (v is null || clock.UtcNow >= v.ExpiresAtUtc) return new(VerifyCheckKind.Expired);
        if (v.Attempts >= MaxAttempts) return new(VerifyCheckKind.Locked);
        v.Attempts++;
        var given = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
        var ok = given.Length == CodeLength && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(given, v.Salt)), Encoding.ASCII.GetBytes(v.CodeHash));
        if (ok) v.ConsumedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        if (!ok) return new(v.Attempts >= MaxAttempts ? VerifyCheckKind.Locked : VerifyCheckKind.Wrong);
        return new(VerifyCheckKind.Ok, MakeProof(e164));
    }

    /// <summary>A token that says "this phone was verified for this couple", good for <see cref="ProofLifetime"/>.</summary>
    public string MakeProof(string e164) => Protector.Protect($"{tenant.CoupleId}|{e164}", ProofLifetime);

    public bool ProofIsValid(string? proof, string e164)
    {
        if (string.IsNullOrEmpty(proof)) return false;
        try { return Protector.Unprotect(proof) == $"{tenant.CoupleId}|{e164}"; }
        catch (CryptographicException) { return false; }
    }

    private static string Hash(string code, string salt) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + code)));
}
