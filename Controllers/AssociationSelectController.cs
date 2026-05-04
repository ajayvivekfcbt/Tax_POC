using Microsoft.AspNetCore.Mvc;
using Tx9501.Models;
using Tx9501.Models.ViewModels;
using Tx9501.Services;

namespace Tx9501.Controllers;

/// <summary>
/// Replaces tx9505.rpgle + tx9505fm.dspf — Association select pop-up subfile.
/// Also serves as the pre-clear warning shown by tx9506.rpgle / tx9506fm.dspf.
/// </summary>
public class AssociationSelectController : Controller
{
    private readonly IAssociationService _assnSvc;

    public AssociationSelectController(IAssociationService assnSvc)
    {
        _assnSvc = assnSvc;
    }

    // ── Select – show subfile ──────────────────────────────────────────────

    /// <param name="returnAction">Where to redirect after selection (e.g. "Clear").</param>
    /// <param name="returnController">Controller to redirect to.</param>
    [HttpGet]
    public async Task<IActionResult> Index(
        string? returnAction,
        string? returnController,
        string? taxYear,
        string? formName,
        string? cancelReturnController,
        string? cancelReturnAction)
    {
        var userId    = User.Identity?.Name ?? "";
        var allAssns  = await _assnSvc.GetAuthorisedAssociationsAsync(userId);
        var selected  = GetCurrentSelection();
        var cancelTarget = ResolveCancelTarget(
            returnController,
            returnAction,
            cancelReturnController,
            cancelReturnAction);

        // For BUILD and EDIT/VALIDATE flows, start with a fresh selection UI
        // instead of inheriting stale session state (e.g., ALL).
        var isBuildSelection = string.Equals(returnController, "TaxReporting", StringComparison.OrdinalIgnoreCase)
            && string.Equals(returnAction, "BuildAction", StringComparison.OrdinalIgnoreCase);
        var isEditSelection = string.Equals(returnController, "TaxReporting", StringComparison.OrdinalIgnoreCase)
            && string.Equals(returnAction, "ValidateAction", StringComparison.OrdinalIgnoreCase);
        
        if (isBuildSelection || isEditSelection)
        {
            selected = new List<string>();
        }

        var vm = new AssociationSelectViewModel
        {
            Associations    = allAssns,
            SelectedCorps   = selected,
            SelectAll       = selected.Contains("ALL"),
            ReturnAction    = returnAction ?? "Index",
            ReturnController = returnController ?? "TaxReporting",
            ReturnTaxYear   = (taxYear ?? string.Empty).Trim(),
            ReturnFormName  = (formName ?? string.Empty).Trim(),
            CancelController = cancelTarget.controller,
            CancelAction     = cancelTarget.action,
            CancelReturnController = (cancelReturnController ?? string.Empty).Trim(),
            CancelReturnAction     = (cancelReturnAction ?? string.Empty).Trim()
        };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Index(AssociationSelectViewModel vm)
    {
        var targetController = string.IsNullOrWhiteSpace(vm.ReturnController)
            ? "TaxReporting"
            : vm.ReturnController.Trim();

        var targetAction = string.IsNullOrWhiteSpace(vm.ReturnAction)
            ? "FormMenu"
            : vm.ReturnAction.Trim();

        // Avoid redirecting to a non-existent TaxReporting/Index action.
        if (targetController.Equals("TaxReporting", StringComparison.OrdinalIgnoreCase)
            && targetAction.Equals("Index", StringComparison.OrdinalIgnoreCase))
        {
            targetAction = "FormMenu";
        }

        var routeValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(vm.ReturnTaxYear))
            routeValues["taxYear"] = vm.ReturnTaxYear.Trim();
        if (!string.IsNullOrWhiteSpace(vm.ReturnFormName))
            routeValues["formName"] = vm.ReturnFormName.Trim();

        var selectAll = vm.SelectAll;
        if (Request.HasFormContentType)
        {
            // Checkbox boolean binding can vary across browsers/forms; trust posted key presence as fallback.
            selectAll = selectAll || Request.Form.ContainsKey("SelectAll");
        }

        if (selectAll)
        {
            HttpContext.Session.SetString("SelectedAssociations", "ALL");
        }
        else
        {
            var chosen = (vm.SelectedCorps ?? new List<string>())
                .Select(code => code?.Trim() ?? string.Empty)
                .Where(code => code.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (chosen.Count == 0 && Request.HasFormContentType)
            {
                var postedSelected = Request.Form["SelectedCorps"]
                    .Concat(Request.Form["SelectedCorps[]"])
                    .Select(code => code?.Trim() ?? string.Empty)
                    .Where(code => code.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (postedSelected.Count > 0)
                {
                    chosen = postedSelected;
                }
            }

            if (chosen.Count == 0)
            {
                TempData["ErrorMessage"] = "Please select at least one association or choose ALL.";
                return RedirectToAction(nameof(Index), new
                {
                    returnAction = targetAction,
                    returnController = targetController,
                    taxYear = vm.ReturnTaxYear,
                    formName = vm.ReturnFormName,
                    cancelReturnController = vm.CancelReturnController,
                    cancelReturnAction = vm.CancelReturnAction
                });
            }

            HttpContext.Session.SetString("SelectedAssociations",
                string.Join(",", chosen));
        }

        var isBuildFlow = targetController.Equals("TaxReporting", StringComparison.OrdinalIgnoreCase)
            && targetAction.Equals("BuildAction", StringComparison.OrdinalIgnoreCase);
        if (isBuildFlow)
        {
            HttpContext.Session.SetString("BuildSelectionConfirmed", "1");
        }

        return RedirectToAction(targetAction, targetController, routeValues);
    }

    // ── Pre-Clear Warning (tx9506) ─────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> PreClearWarning(string returnController, string returnAction)
    {
        var assnCsv = HttpContext.Session.GetString("SelectedAssociations") ?? "";
        var isAll   = assnCsv.Equals("ALL", StringComparison.OrdinalIgnoreCase);
        var codes   = isAll ? new List<string>() : assnCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

        var assnRows = isAll
            ? await _assnSvc.GetAuthorisedAssociationsAsync(User.Identity?.Name ?? "")
            : await _assnSvc.GetSelectedAssociationsAsync(codes);

        var ctrl = LoadTaxControl();
        var form = HttpContext.Session.GetString("SelectedForm") ?? "";

        var vm = new PreClearWarningViewModel
        {
            TaxYear          = ctrl?.TaxYear ?? "",
            FormName         = form,
            Associations     = assnRows,
            IsAll            = isAll,
            ReturnController = returnController,
            ReturnAction     = returnAction
        };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult PreClearWarning(PreClearWarningViewModel vm)
    {
        if (vm.Confirmed)
            return RedirectToAction(vm.ReturnAction, vm.ReturnController, new { confirmed = true });

        return RedirectToAction("MainMenu", "TaxReporting");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private List<string> GetCurrentSelection()
    {
        var raw = HttpContext.Session.GetString("SelectedAssociations") ?? "";
        if (raw.Equals("ALL", StringComparison.OrdinalIgnoreCase)) return new() { "ALL" };
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private TaxControlRecord? LoadTaxControl()
    {
        var json = HttpContext.Session.GetString("TaxControl");
        return json is null ? null
            : System.Text.Json.JsonSerializer.Deserialize<TaxControlRecord>(json);
    }

    private static (string controller, string action) ResolveCancelTarget(
        string? returnController,
        string? returnAction,
        string? cancelReturnController,
        string? cancelReturnAction)
    {
        if (!string.IsNullOrWhiteSpace(cancelReturnController)
            && !string.IsNullOrWhiteSpace(cancelReturnAction))
        {
            return (cancelReturnController.Trim(), cancelReturnAction.Trim());
        }

        if (string.Equals(returnController, "TaxReporting", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(returnAction, "BuildAction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(returnAction, "ValidateAction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(returnAction, "FormMenu", StringComparison.OrdinalIgnoreCase))
            {
                return ("TaxReporting", "FormMenu");
            }

            return ("TaxReporting", "MainMenu");
        }

        if (string.Equals(returnController, "AssociationMenu", StringComparison.OrdinalIgnoreCase))
        {
            return ("AssociationMenu", "FormMenu");
        }

        return ("TaxReporting", "MainMenu");
    }
}
