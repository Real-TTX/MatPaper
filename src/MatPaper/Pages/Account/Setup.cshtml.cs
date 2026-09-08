using System.ComponentModel.DataAnnotations;
using MatPaper.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatPaper.Pages.Account;

public class SetupModel : PageModel
{
    private readonly SignInService _signInService;
    private readonly SetupState _setupState;

    public SetupModel(SignInService signInService, SetupState setupState)
    {
        _signInService = signInService;
        _setupState = setupState;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (await _signInService.AnyUsersExistAsync())
        {
            return RedirectToPage("/Index");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await _signInService.AnyUsersExistAsync())
        {
            return RedirectToPage("/Index");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var admin = await _signInService.CreateUserAsync(
                Input.Username,
                Input.DisplayName,
                Input.Email,
                Input.Password,
                "Admin",
                isActive: true,
                actingUserId: null);

            _setupState.HasUsers = true;

            await _signInService.SignInAsync(admin, isPersistent: true);

            return Redirect("/");
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public class InputModel
    {
        [Required]
        [Display(Name = "Username")]
        public string Username { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [EmailAddress]
        [Display(Name = "Email")]
        public string? Email { get; set; }

        [Required]
        [MinLength(8, ErrorMessage = "The password must be at least 8 characters long.")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm password")]
        [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
