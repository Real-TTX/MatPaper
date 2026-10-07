using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatPaper.Pages.Documents;

/// <summary>Old address of the camera scan; it is part of "Add document" now and opens the scanner right away.</summary>
public class ScanModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("Upload", new { camera = true });
}
