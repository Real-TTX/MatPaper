using System.ComponentModel.DataAnnotations;
using MatPaper.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatPaper.Pages.Account;

public class LoginModel : PageModel
{
    private readonly SignInService _signInService;
    private readonly IStringLocalizer<SharedResource> _l;

    public LoginModel(SignInService signInService, IStringLocalizer<SharedResource> l)
    {
        _signInService = signInService;
        _l = l;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await _signInService.ValidateCredentialsAsync(Input.Username, Input.Password);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, _l["Invalid username or password."]);
            return Page();
        }

        await _signInService.SignInAsync(user, Input.RememberMe);

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return Redirect("/");
    }

    public class InputModel
    {
        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Username")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "The {0} field is required.")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Remember me")]
        public bool RememberMe { get; set; }
    }
}
