using Microsoft.AspNetCore.Mvc;
using Tx9501.Models;
using Tx9501.Models.ViewModels;
using Tx9501.Services;

namespace Tx9501.Controllers;

/// <summary>
/// Replaces tx9511cl.clp + tx9511fm.dspf — Audit mode main menu.
/// Also hosts the audit-specific year-select (TX9512 / tx9512.rpgle).
/// </summary>
public class AuditMenuController : Controller
{
    private readonly IYearSelectService _yearSvc;

    public AuditMenuController(IYearSelectService yearSvc)
    {
        _yearSvc = yearSvc;
    }

    // ── TX9512 – year select for audit ────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> YearSelect()
    {
        var rows = await _yearSvc.ListYearsAsync();
        // Only option allowed is 1=Process; present as a simple list
        var vm = new YearSelectListViewModel { Years = rows, IsAuditMode = true };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult YearSelect(YearSelectListViewModel vm)
    {
        if (!vm.SelectedYear.HasValue)
        {
            TempData["StatusMessage"] = "Please select a year.";
            return RedirectToAction("YearSelect");
        }

        var ctrl = new TaxControlRecord { TaxYear = vm.SelectedYear.Value.ToString() };
        HttpContext.Session.SetString("AuditTaxControl",
            System.Text.Json.JsonSerializer.Serialize(ctrl));
        return RedirectToAction("MainMenu");
    }

    // ── TX9511 SF01 — Audit main menu (all forms) ─────────────────────────

    [HttpGet]
    public IActionResult MainMenu()
    {
        var ctrl = LoadAuditControl();
        if (ctrl is null) return RedirectToAction("YearSelect");

        ViewBag.StatusMessage = TempData["StatusMessage"] as string;
        return View(new AuditMainMenuViewModel
        {
            TaxYear     = ctrl.TaxYear,
            FormOptions = AllForms()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult MainMenu(AuditMainMenuViewModel vm)
    {
        if (vm.ExitPressed)
        {
            HttpContext.Session.Remove("AuditTaxControl");
            return RedirectToAction("YearSelect");
        }

        if (string.IsNullOrEmpty(vm.SelectedForm))
        {
            ModelState.AddModelError("", "Select a form.");
            vm.FormOptions = AllForms();
            return View(vm);
        }

        HttpContext.Session.SetString("AuditSelectedForm", vm.SelectedForm);
        return RedirectToAction("FormMenu");
    }

    // ── TX9511 SF02 — Audit form action menu ─────────────────────────────

    [HttpGet]
    public IActionResult FormMenu()
    {
        var ctrl = LoadAuditControl();
        if (ctrl is null) return RedirectToAction("YearSelect");

        var form = HttpContext.Session.GetString("AuditSelectedForm") ?? "";
        ViewBag.StatusMessage = TempData["StatusMessage"] as string;
        return View(new AuditFormMenuViewModel
        {
            TaxYear      = ctrl.TaxYear,
            SelectedForm = form,
            Actions      = AuditActions(form)
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult FormMenu(AuditFormMenuViewModel vm)
    {
        if (vm.ExitPressed)
        {
            HttpContext.Session.Remove("AuditTaxControl");
            return RedirectToAction("YearSelect");
        }
        if (vm.BackPressed)
            return RedirectToAction("MainMenu");

        if (string.IsNullOrWhiteSpace(vm.SelectedAction))
        {
            TempData["ErrorMessage"] = "Select an action.";
            return RedirectToAction("FormMenu");
        }

        // Audit mode only shows Print and Summary
        switch (vm.SelectedAction)
        {
            case "P":  // Print audit report
                TempData["StatusMessage"] = "Print Audit Report submitted to IBM i job queue.";
                break;
            case "S":  // Display summary
                return RedirectToAction("Index", "Summary");
            default:
                TempData["ErrorMessage"] = $"Action '{vm.SelectedAction}' is not supported from this menu.";
                break;
        }
        return RedirectToAction("FormMenu");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private TaxControlRecord? LoadAuditControl()
    {
        var json = HttpContext.Session.GetString("AuditTaxControl");
        return json is null ? null
            : System.Text.Json.JsonSerializer.Deserialize<TaxControlRecord>(json);
    }

    private static FormOption[] AllForms() => new[]
    {
        new FormOption { FormCode = "1098",      Description = "1098   - Mortgage Interest" },
        new FormOption { FormCode = "1099-A",    Description = "1099-A  - Acquisition/Abandonment" },
        new FormOption { FormCode = "1099-MISC", Description = "1099-MISC - Miscellaneous Income" },
        new FormOption { FormCode = "1099-NEC",  Description = "1099-NEC  - Non-Employee Compensation" },
        new FormOption { FormCode = "1099-INT",  Description = "1099-INT  - Interest Income" },
        new FormOption { FormCode = "1099-DIV",  Description = "1099-DIV  - Dividend Income" },
    };

    private static FormAction[] AuditActions(string form)
    {
        var acts = new List<FormAction>
        {
            new() { ActionCode = "P", Description = "1=Print Audit Report" },
            new() { ActionCode = "S", Description = "2=Display Summary" }
        };
        if (form == "1098")
            acts.Add(new FormAction { ActionCode = "L", Description = "3=Print Letters" });
        return acts.ToArray();
    }
}
