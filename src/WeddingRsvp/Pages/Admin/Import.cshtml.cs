using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeddingRsvp.Services;

namespace WeddingRsvp.Pages.Admin;

public class ImportModel(ImportService importer, Ui ui) : PageModel
{
    [BindProperty] public IFormFile? File { get; set; }
    public ImportReport? Report { get; private set; }
    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (File is null || File.Length == 0)
        {
            Error = ui.T("בחרו קובץ CSV", "Choose a CSV file");
            return Page();
        }
        if (File.Length > 2_000_000)
        {
            Error = ui.T("הקובץ גדול מדי", "The file is too large");
            return Page();
        }

        await using var stream = File.OpenReadStream();
        Report = await importer.ImportAsync(stream);
        return Page();
    }
}
