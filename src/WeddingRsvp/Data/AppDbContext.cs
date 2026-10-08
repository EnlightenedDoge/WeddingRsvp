using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Guest> Guests => Set<Guest>();
    public DbSet<MessageLog> Messages => Set<MessageLog>();
    public DbSet<EventSettings> EventSettings => Set<EventSettings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Guest>(e =>
        {
            e.HasIndex(g => g.Token).IsUnique();
            e.HasIndex(g => g.PhoneE164);
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
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Ignore(s => s.VenueDisplay);
        });

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
}
