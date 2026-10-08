using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;
using WeddingRsvp.Options;

namespace WeddingRsvp.Auth;

public static class BootstrapAdmin
{
    /// <summary>Creates the first site admin from config, only while no site admin exists.</summary>
    public static async Task EnsureAsync(IServiceProvider sp, AppOptions app, ILogger log)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        if (await db.Users.AnyAsync(u => u.Role == UserRole.SiteAdmin)) return;

        if (string.IsNullOrWhiteSpace(app.BootstrapAdminEmail) || string.IsNullOrEmpty(app.BootstrapAdminPassword))
        {
            log.LogWarning("No site admin exists. Set App__BootstrapAdminEmail and App__BootstrapAdminPassword to create one.");
            return;
        }

        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var email = app.BootstrapAdminEmail.Trim();
        var result = await users.CreateAsync(new AppUser
        {
            UserName = email, Email = email, EmailConfirmed = true,
            Role = UserRole.SiteAdmin, CreatedAtUtc = DateTime.UtcNow,
        }, app.BootstrapAdminPassword);

        if (result.Succeeded) log.LogInformation("Created the first site admin {Email}. Remove the bootstrap password from config.", email);
        else log.LogError("Could not create the bootstrap site admin: {Errors}", string.Join("; ", result.Errors.Select(e => e.Code)));
    }
}
