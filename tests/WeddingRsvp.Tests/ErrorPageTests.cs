using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WeddingRsvp.Pages;

namespace WeddingRsvp.Tests;

/// <summary>
/// The public error page. Every empty-bodied status re-executes it, so it must only say "link not found" for a 404: a guest whose
/// answer hit a 400 or a 429 was told their link was wrong and stopped trying (seen live 2026-11-09).
/// </summary>
public sealed class ErrorPageTests
{
    private static NotFoundModel Page(int code) => new()
    {
        Code = code,
        PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
    };

    [Fact]
    public void A_404_is_a_missing_link()
    {
        var p = Page(404);
        p.OnGet();
        Assert.True(p.LinkMissing);
        Assert.Equal(404, p.PageContext.HttpContext.Response.StatusCode);
    }

    [Fact]
    public void The_page_reached_with_no_code_is_a_missing_link()
    {
        var p = Page(0);
        p.OnGet();
        Assert.True(p.LinkMissing);
        Assert.Equal(404, p.PageContext.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(500)]
    public void Any_other_error_is_ours_and_keeps_its_status(int code)
    {
        var p = Page(code);
        p.OnGet();
        Assert.False(p.LinkMissing);
        Assert.Equal(code == 429, p.TooBusy);
        Assert.Equal(code, p.PageContext.HttpContext.Response.StatusCode);
    }

    [Fact]
    public void A_nonsense_code_falls_back_to_the_missing_link_page()
    {
        var p = Page(42);
        p.OnGet();
        Assert.True(p.LinkMissing);
        Assert.Equal(404, p.PageContext.HttpContext.Response.StatusCode);
    }
}
