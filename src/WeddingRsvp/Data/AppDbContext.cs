using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WeddingRsvp.Domain;
using WeddingRsvp.Tenancy;

namespace WeddingRsvp.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant)
    : IdentityUserContext<AppUser>(options)
{
    public DbSet<Couple> Couples => Set<Couple>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<Guest> Guests => Set<Guest>();
    public DbSet<MessageLog> Messages => Set<MessageLog>();
    public DbSet<EventSettings> EventSettings => Set<EventSettings>();
    public DbSet<PlatformState> PlatformStates => Set<PlatformState>();
    public DbSet<SmsAllocationEntry> SmsAllocations => Set<SmsAllocationEntry>();

    // Read by the global query filters; EF re-evaluates it per context instance.
    private int? CurrentCoupleId => tenant.CoupleId;

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AppUser>(e =>
        {
            e.HasIndex(u => u.CoupleId);
            e.HasOne<Couple>().WithMany().HasForeignKey(u => u.CoupleId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Guest>(e =>
        {
            e.HasIndex(g => g.Token).IsUnique();
            e.HasIndex(g => new { g.CoupleId, g.PhoneE164 });
            e.Property(g => g.Name).HasMaxLength(200);
            e.Property(g => g.Token).HasMaxLength(32);
            e.Ignore(g => g.HasValidPhone);
        });

        b.Entity<MessageLog>(e =>
        {
            e.HasIndex(m => new { m.Status, m.NotBeforeUtc });
            e.HasIndex(m => m.ProviderMessageId);
            e.HasOne(m => m.Guest).WithMany(g => g.Messages)
                .HasForeignKey(m => m.GuestId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<EventSettings>(e =>
        {
            e.HasIndex(s => s.CoupleId).IsUnique();
            e.Ignore(s => s.VenueDisplay);
            e.HasIndex(s => s.PreviewToken).IsUnique().HasFilter("\"PreviewToken\" <> ''");
        });

        // Tenant isolation: every query on a couple-owned table is scoped to the current tenant.
        // Pages and services never filter by CoupleId themselves. The only bypass is IgnoreQueryFilters(),
        // used for token lookups on the public pages and for the background worker's couple list.
        b.Entity<Guest>().HasQueryFilter(x => x.CoupleId == CurrentCoupleId);
        b.Entity<MessageLog>().HasQueryFilter(x => x.CoupleId == CurrentCoupleId);
        b.Entity<EventSettings>().HasQueryFilter(x => x.CoupleId == CurrentCoupleId);
        b.Entity<SmsAllocationEntry>().HasQueryFilter(x => x.CoupleId == CurrentCoupleId);
        b.Entity<SmsAllocationEntry>().HasIndex(x => x.CoupleId);
        b.Entity<SmsAllocationEntry>().HasOne<Couple>().WithMany().HasForeignKey(x => x.CoupleId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<Guest>().HasOne<Couple>().WithMany().HasForeignKey(x => x.CoupleId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MessageLog>().HasOne<Couple>().WithMany().HasForeignKey(x => x.CoupleId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<EventSettings>().HasOne<Couple>().WithMany().HasForeignKey(x => x.CoupleId).OnDelete(DeleteBehavior.Cascade);

        // SQLite hands DateTimes back as Unspecified. Every property named *Utc is UTC by convention,
        // so mark it as such on read. *Local properties are intentionally left Unspecified.
        var utc = new ValueConverter<DateTime, DateTime>(
            v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(
            v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var prop in entity.GetProperties())
        {
            if (!prop.Name.EndsWith("Utc", StringComparison.Ordinal)) continue;
            if (prop.ClrType == typeof(DateTime)) prop.SetValueConverter(utc);
            else if (prop.ClrType == typeof(DateTime?)) prop.SetValueConverter(utcNullable);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        StampTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    /// <summary>
    /// New couple-owned rows get the current tenant. A row that names a different couple than the current
    /// tenant is a bug (or an attack), so it throws instead of silently writing across tenants.
    /// </summary>
    private void StampTenant()
    {
        foreach (var e in ChangeTracker.Entries<ITenantOwned>())
        {
            if (e.State == EntityState.Added)
            {
                if (e.Entity.CoupleId == 0)
                    e.Entity.CoupleId = CurrentCoupleId
                        ?? throw new InvalidOperationException("No tenant is set; cannot create couple-owned data.");
                else if (CurrentCoupleId is { } cur && e.Entity.CoupleId != cur)
                    throw new InvalidOperationException("Cross-tenant write blocked.");
            }
            else if ((e.State == EntityState.Modified || e.State == EntityState.Deleted)
                     && CurrentCoupleId is { } cur && e.Entity.CoupleId != cur)
            {
                throw new InvalidOperationException("Cross-tenant write blocked.");
            }
        }
    }
}
