using Microsoft.AspNetCore.Mvc;
using Tx9501.Models;
using Tx9501.Models.ViewModels;
using Tx9501.Services;

namespace Tx9501.Controllers;

/// <summary>
/// Replaces tx9503cl.clp + tx9503fm.dspf — Association-level menu.
/// TX9503 is displayed after association selection when a sub-set of forms
/// is available (1099-A / 1099-MISC / 1099-NEC only).
/// </summary>
public class AssociationMenuController : Controller
{
    // ── SF01 — Association main menu ──────────────────────────────────────

    [HttpGet]
    public IActionResult MainMenu()
    {
        var ctrl = LoadControl();
        if (ctrl is null) return RedirectToAction("YearSelect", "TaxReporting");

        var vm = BuildMainMenuVm(ctrl);
        ViewBag.StatusMessage = TempData["StatusMessage"] as string;
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult MainMenu(AssocMainMenuViewModel vm)
    {
        if (vm.ExitPressed)
        {
            HttpContext.Session.Clear();
            return RedirectToAction("YearSelect", "TaxReporting");
        }
        if (vm.BackPressed)
            return RedirectToAction("AssociationSelect", "AssociationSelect");

        if (!ModelState.IsValid || string.IsNullOrEmpty(vm.SelectedForm))
        {
            if (string.IsNullOrEmpty(vm.SelectedForm))
                ModelState.AddModelError("", "Select a form.");

            var ctrl2 = LoadControl()!;
            return View(BuildMainMenuVm(ctrl2));
        }

        HttpContext.Session.SetString("SelectedForm", vm.SelectedForm);
        HttpContext.Session.SetString("SelectedFormDesc", vm.SelectedFormDesc ?? "");
        return RedirectToAction("FormMenu");
    }

    // ── SF02 — Action menu for selected form ─────────────────────────────

    [HttpGet]
    public IActionResult FormMenu()
    {
        var ctrl = LoadControl();
        if (ctrl is null) return RedirectToAction("YearSelect", "TaxReporting");

        var form = HttpContext.Session.GetString("SelectedForm") ?? "";
        var vm   = BuildFormMenuVm(ctrl, form);
        ViewBag.StatusMessage = TempData["StatusMessage"] as string;
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult FormMenu(AssocFormMenuViewModel vm)
    {
        if (vm.ExitPressed)
        {
            HttpContext.Session.Clear();
            return RedirectToAction("YearSelect", "TaxReporting");
        }
        if (vm.BackPressed)
            return RedirectToAction("MainMenu");

        if (string.IsNullOrWhiteSpace(vm.SelectedAction))
        {
            TempData["ErrorMessage"] = "Select an action.";
            return RedirectToAction("FormMenu");
        }

        var selectedForm = HttpContext.Session.GetString("SelectedForm") ?? vm.SelectedForm ?? string.Empty;
        var taxYear = LoadControl()?.TaxYear ?? vm.TaxYear ?? string.Empty;

        if (string.Equals(vm.SelectedAction, "B", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Session.SetString("BuildSelectionPending", "1");
            HttpContext.Session.Remove("BuildSelectionConfirmed");
            HttpContext.Session.Remove("SelectedAssociations");
            return RedirectToAction("Index", "AssociationSelect", new
            {
                returnAction = "BuildAction",
                returnController = "TaxReporting",
                taxYear,
                formName = selectedForm
            });
        }

        // Route to TaxReportingController FormMenu POST logic for the same actions
        return RedirectToAction("FormMenu", "TaxReporting");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private TaxControlRecord? LoadControl()
    {
        var json = HttpContext.Session.GetString("TaxControl");
        return json is null ? null
            : System.Text.Json.JsonSerializer.Deserialize<TaxControlRecord>(json);
    }

    private AssocMainMenuViewModel BuildMainMenuVm(TaxControlRecord ctrl)
    {
        return new AssocMainMenuViewModel
        {
            TaxYear        = ctrl.TaxYear,
            FormOptions    = new[]
            {
                new FormOption { FormCode = "1099-A",    Description = "1099-A  - Acquisition/Abandonment" },
                new FormOption { FormCode = "1099-MISC", Description = "1099-MISC - Miscellaneous Income" },
                new FormOption { FormCode = "1099-NEC",  Description = "1099-NEC  - Non-Employee Compensation" },
            }
        };
    }

    private AssocFormMenuViewModel BuildFormMenuVm(TaxControlRecord ctrl, string form)
    {
        return new AssocFormMenuViewModel
        {
            TaxYear      = ctrl.TaxYear,
            SelectedForm = form,
            // Association menu has fewer actions than the main TX9501 menu
            Actions      = new[]
            {
                new FormAction { ActionCode = "E", Description = "1=Edit Reporting Data" },
                new FormAction { ActionCode = "C", Description = "2=Clear Data for Form" },
                new FormAction { ActionCode = "B", Description = "3=Build Data for Form" },
                new FormAction { ActionCode = "S", Description = "4=Display Summary" },
                new FormAction { ActionCode = "D", Description = "5=Detail Report" },
                new FormAction { ActionCode = "X", Description = "6=Exclusion Report" },
                new FormAction { ActionCode = "R", Description = "7=Error Report" },
                new FormAction { ActionCode = "M", Description = "8=Maintain Records" },
            }
        };
    }
}
