using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tx9501.Models.ViewModels;
using Tx9501.Services;

namespace Tx9501.Controllers;

public sealed class AccountController : Controller
{
    private readonly ILdapAuthenticationService _ldapAuthentication;

    public AccountController(ILdapAuthenticationService ldapAuthentication)
    {
        _ldapAuthentication = ldapAuthentication;
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToLocal(returnUrl);
        }

        return View(new LoginViewModel
        {
            ReturnUrl = returnUrl ?? string.Empty
        });
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var authResult = await _ldapAuthentication.AuthenticateAsync(model.Username, model.Password, cancellationToken);
        if (!authResult.Succeeded)
        {
            // When LDAP is disabled, allow any non-empty credentials (dev/bypass mode)
            if (authResult.ErrorMessage?.Contains("disabled", StringComparison.OrdinalIgnoreCase) == true
                && !string.IsNullOrWhiteSpace(model.Username)
                && !string.IsNullOrWhiteSpace(model.Password))
            {
                // fall through — treat as successful login
            }
            else
            {
                ModelState.AddModelError(
                    string.Empty,
                    authResult.ErrorMessage ?? "Authentication failed. Please verify your credentials.");
                model.Password = string.Empty;
                return View(model);
            }
        }

        var displayName = string.IsNullOrWhiteSpace(authResult.DisplayName)
            ? model.Username
            : authResult.DisplayName;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, model.Username),
            new(ClaimTypes.Name, displayName)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });

        return RedirectToLocal(model.ReturnUrl);
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction("YearSelect", "TaxReporting");
    }
}
