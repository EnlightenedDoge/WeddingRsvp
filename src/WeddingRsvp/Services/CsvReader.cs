using System.Text;

namespace WeddingRsvp.Services;

/// <summary>Small RFC 4180 reader (quotes, escaped quotes, newlines in quotes). Good enough for guest lists.</summary>
public static class CsvReader
{
    /// <summary>
    /// Decodes bytes as UTF-8, falling back to Windows-1255 — what Hebrew Excel produces
    /// for "CSV" (non-UTF-8) exports.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1255).GetString(bytes);
        }
    }

    public static char DetectDelimiter(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var candidates = new[] { ',', ';', '\t' };
        return candidates.OrderByDescending(c => firstLine.Count(ch => ch == c)).First();
    }

    public static List<List<string>> Parse(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n')
            {
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = new List<string>();
            }
            else if (c != '\r') field.Append(c);
        }

        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows.Where(r => r.Any(f => !string.IsNullOrWhiteSpace(f))).ToList();
    }

    public static string Escape(string? v)
    {
        v ??= "";
        return v.IndexOfAny(['"', ',', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    /// <summary>Escape for free text (names, notes) in an export: a cell that starts like a formula gets a leading apostrophe so Excel won't run it.</summary>
    public static string EscapeText(string? v)
    {
        v ??= "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return Escape(v);
    }
}
