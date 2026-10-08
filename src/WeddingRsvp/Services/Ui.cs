using System.Globalization;

namespace WeddingRsvp.Services;

/// <summary>
/// Interface language for the current request (Hebrew RTL or English LTR), from the culture cookie.
/// Views write both texts side by side: <c>@Ui.T("שמירה", "Save")</c>.
/// </summary>
public sealed class Ui
{
    public bool IsHebrew => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "en";
    public string Lang => IsHebrew ? "he" : "en";
    public string Dir => IsHebrew ? "rtl" : "ltr";
    public string T(string he, string en) => IsHebrew ? he : en;
}
