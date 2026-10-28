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
public sealed class ImportService(AppDbContext db, IsraelClock clock)
{
    private static readonly string[] NameHeaders = ["name", "שם", "שם מלא", "שם האורח", "מוזמן"];
    private static readonly string[] PhoneHeaders = ["phone", "mobile", "טלפון", "נייד", "מספר", "מספר טלפון", "טלפון נייד"];
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

            db.Guests.Add(new Guest
            {
                Name = name,
                PhoneRaw = phone,
                PhoneE164 = p.E164,
                PhoneError = p.Error,
                InvitedCount = count,
                Token = token,
                CreatedAtUtc = clock.UtcNow,
            });
            report.Added++;
            if (!p.IsValid) report.InvalidPhones.Add(new(line, name, phone, p.Error!));
        }

        await db.SaveChangesAsync(ct);
        return report;
    }

    private static readonly string[] UnlimitedWords = ["ללא הגבלה", "בלי הגבלה", "ללא הגבלת מספר", "unlimited", "no limit", "any", "∞"];

    /// <summary>
    /// The "invited" cell: a positive number is the limit; 0 or a word such as "ללא הגבלה" / "unlimited" means no guest limit (stored as 0);
    /// anything else (empty, text, negative) defaults to 1.
    /// </summary>
    public static int ParseInvitedCount(string? cell)
    {
        var t = (cell ?? "").Trim();
        if (int.TryParse(t, out var n)) return n >= 0 ? n : 1;
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
