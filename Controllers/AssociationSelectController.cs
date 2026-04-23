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
    public async Task<IActionResult> Index(string? returnAction, string? returnController)
    {
        var userId    = User.Identity?.Name ?? "";
        var allAssns  = await _assnSvc.GetAuthorisedAssociationsAsync(userId);
        var selected  = GetCurrentSelection();

        var vm = new AssociationSelectViewModel
        {
            Associations    = allAssns,
            SelectedCorps   = selected,
            SelectAll       = selected.Contains("ALL"),
            ReturnAction    = returnAction ?? "Index",
            ReturnController = returnController ?? "TaxReporting"
        };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Index(AssociationSelectViewModel vm)
    {
        if (vm.SelectAll)
        {
            HttpContext.Session.SetString("SelectedAssociations", "ALL");
        }
        else
        {
            var chosen = vm.SelectedCorps ?? new List<string>();
            HttpContext.Session.SetString("SelectedAssociations",
                string.Join(",", chosen));
        }

        return RedirectToAction(vm.ReturnAction, vm.ReturnController);
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
}
