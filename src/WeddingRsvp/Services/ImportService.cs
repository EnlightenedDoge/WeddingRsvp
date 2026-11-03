using Microsoft.EntityFrameworkCore;
using WeddingRsvp.Data;
using WeddingRsvp.Domain;

namespace WeddingRsvp.Services;

public sealed record ImportIssue(int Line, string Name, string Phone, string Problem);

public sealed class ImportReport
{
    public int Rows { get; set; }
    public int Added { get; set; }
    public List<ImportIssue> InvalidPhones { get; } = new();   // imported, but need fixing before sending
    public List<ImportIssue> Skipped { get; } = new();         // not imported (duplicates, empty rows)
}

/// <summary>
/// Imports the client's list: name, phone, optional invited count. Append-only; duplicates
/// (by normalized phone) are skipped and reported. Headers in Hebrew or English are recognized;
/// without a header row, columns are assumed to be name, phone, count.
/// </summary>
public sealed class ImportService(AppDbContext db, IsraelClock clock, FamilyService families)
{
    private static readonly string[] NameHeaders = ["name", "שם", "שם מלא", "שם האורח", "מוזמן"];
    private static readonly string[] PhoneHeaders = ["phone", "mobile", "טלפון", "נייד", "מספר", "מספר טלפון", "טלפון נייד"];
    private static readonly string[] FamilyNameHeaders = ["family", "family name", "משפחה", "שם משפחה"];
    private static readonly string[] FamilyIdHeaders = ["family id", "family identifier", "family key", "מזהה משפחה", "קוד משפחה"];
    private static readonly string[] CountHeaders = ["count", "invited", "כמות", "מוזמנים", "מספר מוזמנים", "כמה", "מספר אנשים"];

    public async Task<ImportReport> ImportAsync(Stream csv, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await csv.CopyToAsync(ms, ct);
        var text = CsvReader.Decode(ms.ToArray());
        var rows = CsvReader.Parse(text, CsvReader.DetectDelimiter(text));

        var report = new ImportReport();
        if (rows.Count == 0) return report;

        var (nameCol, phoneCol, countCol, hasHeader) = MapColumns(rows[0]);
        var famNameCol = hasHeader ? FindCol(rows[0], FamilyNameHeaders) : -1;
        var famIdCol = hasHeader ? FindCol(rows[0], FamilyIdHeaders) : -1;
        var existingFamilies = await db.Families.ToListAsync(ct);
        var familyByKey = new Dictionary<string, Family>(StringComparer.OrdinalIgnoreCase); // identifiers seen in this file or already stored
        foreach (var f in existingFamilies.Where(f => f.ExternalId != null)) familyByKey[f.ExternalId!] = f;
        var familyByName = new Dictionary<string, Family>(StringComparer.OrdinalIgnoreCase);  // names without an identifier, within this file
        var existingPhones = (await db.Guests.Where(g => g.PhoneE164 != null).Select(g => g.PhoneE164!).ToListAsync(ct))
            .ToHashSet();
        var existingTokens = (await db.Guests.Select(g => g.Token).ToListAsync(ct)).ToHashSet();

        for (var i = hasHeader ? 1 : 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var line = i + 1;
            var name = Cell(r, nameCol);
            var phone = Cell(r, phoneCol);
            report.Rows++;

            if (string.IsNullOrWhiteSpace(name))
            {
                report.Skipped.Add(new(line, name, phone, "חסר שם"));
                continue;
            }

            var p = PhoneNormalizer.Normalize(phone);
            if (p.IsValid && !existingPhones.Add(p.E164!))
            {
                report.Skipped.Add(new(line, name, phone, "מספר כפול"));
                continue;
            }

            var count = countCol >= 0 ? ParseInvitedCount(Cell(r, countCol)) : 1;

            string token;
            do token = TokenGenerator.New(); while (!existingTokens.Add(token));

            var family = FamilyFor(Cell(r, famIdCol), Cell(r, famNameCol), existingFamilies, familyByKey, familyByName);
            var guest = new Guest
            {
                Name = name,
                PhoneRaw = phone,
                PhoneE164 = p.E164,
                PhoneError = p.Error,
                InvitedCount = count,
                Token = token,
                CreatedAtUtc = clock.UtcNow,
            };
            if (family is not null)
            {
                guest.Family = family;
                if (family.Id != 0) await families.AdoptAnswerAsync(guest, family.Id, ct); // joins a family that has already answered
            }
            db.Guests.Add(guest);
            report.Added++;
            if (!p.IsValid) report.InvalidPhones.Add(new(line, name, phone, p.Error!));
        }

        await db.SaveChangesAsync(ct);
        return report;
    }

    /// <summary>
    /// The family a row joins. An identifier decides (rows with the same identifier are one family, even across imports; the name only
    /// labels it, so two families may share a name). With a name and no identifier the row joins the one existing family of that name,
    /// or one created earlier in this file, else a new family is created. Nothing in either column = no family.
    /// </summary>
    private Family? FamilyFor(string id, string name, List<Family> existing, Dictionary<string, Family> byKey, Dictionary<string, Family> byName)
    {
        if (id.Length > Family.ExternalIdMax) id = id[..Family.ExternalIdMax];
        if (name.Length > Family.NameMax) name = name[..Family.NameMax];
        if (id.Length > 0)
        {
            if (byKey.TryGetValue(id, out var known)) return known;
            var f = db.Families.Add(new Family { Name = name, ExternalId = id, CreatedAtUtc = clock.UtcNow }).Entity;
            byKey[id] = f;
            return f;
        }
        if (name.Length == 0) return null;
        if (byName.TryGetValue(name, out var inFile)) return inFile;
        var same = existing.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        var chosen = same.Count == 1 ? same[0] : db.Families.Add(new Family { Name = name, CreatedAtUtc = clock.UtcNow }).Entity;
        byName[name] = chosen;
        return chosen;
    }

    private static int FindCol(List<string> header, string[] names) => header.FindIndex(h => names.Contains(h.Trim().ToLowerInvariant()));

    private static readonly string[] UnlimitedWords = ["ללא הגבלה", "בלי הגבלה", "ללא הגבלת מספר", "unlimited", "no limit", "any", "∞"];

    /// <summary>
    /// The "invited" cell: a positive number is the limit; 0 or a word such as "ללא הגבלה" / "unlimited" means no guest limit (stored as 0);
    /// anything else (empty, text, negative) defaults to 1.
    /// </summary>
    public static int ParseInvitedCount(string? cell)
    {
        var t = (cell ?? "").Trim();
        // 0 = no limit; 1..50 is the invitation size (same ceiling the edit page enforces); clamp out-of-range and junk.
        if (int.TryParse(t, out var n)) return n <= 0 ? (n == 0 ? 0 : 1) : Math.Min(n, 50);
        return UnlimitedWords.Contains(t.ToLowerInvariant()) ? 0 : 1;
    }

    private static (int name, int phone, int count, bool hasHeader) MapColumns(List<string> first)
    {
        int Find(string[] names) => first.FindIndex(h => names.Contains(h.Trim().ToLowerInvariant()));
        int n = Find(NameHeaders), p = Find(PhoneHeaders), c = Find(CountHeaders);
        return n >= 0 && p >= 0 ? (n, p, c, true) : (0, 1, first.Count > 2 ? 2 : -1, false);
    }

    private static string Cell(List<string> row, int col) =>
        col >= 0 && col < row.Count ? row[col].Trim() : "";
}
