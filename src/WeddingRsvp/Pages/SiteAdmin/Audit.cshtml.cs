using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Pages.SiteAdmin;

public class AuditModel(AppDbContext db) : PageModel
{
    private const int PageSize = 50;

    [BindProperty(SupportsGet = true)] public int PageNo { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public string? Action { get; set; }
    [BindProperty(SupportsGet = true)] public int? CoupleId { get; set; }

    public List<AuditEntry> Rows { get; private set; } = [];
    public bool HasNext { get; private set; }

    public async Task OnGetAsync()
    {
        var q = db.AuditLog.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(Action)) q = q.Where(a => a.Action.StartsWith(Action.Trim()));
        if (CoupleId is not null) q = q.Where(a => a.CoupleId == CoupleId);
        var skip = (Math.Max(PageNo, 1) - 1) * PageSize;
        // SQLite can't order by DateTime offsets, but AtUtc is a plain DateTime; Id breaks ties.
        var rows = await q.OrderByDescending(a => a.AtUtc).ThenByDescending(a => a.Id).Skip(skip).Take(PageSize + 1).ToListAsync();
        HasNext = rows.Count > PageSize;
        Rows = rows.Take(PageSize).ToList();
    }
}
