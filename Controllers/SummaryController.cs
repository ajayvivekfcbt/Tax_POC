using Microsoft.AspNetCore.Mvc;
using Tx9501.Models;
using Tx9501.Models.ViewModels;
using Tx9501.Services;

namespace Tx9501.Controllers;

/// <summary>
/// Replaces tx9520.sqlrpgle + tx9520fm.dspf — Tax record summary subfile.
/// </summary>
public class SummaryController : Controller
{
    private readonly ISummaryService _sumSvc;

    public SummaryController(ISummaryService sumSvc)
    {
        _sumSvc = sumSvc;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var ctrl = LoadControl();
        if (ctrl is null) return RedirectToAction("YearSelect", "TaxReporting");

        var form = HttpContext.Session.GetString("SelectedForm") ?? "";
        var rows = await _sumSvc.GetSummaryAsync(ctrl.TaxYear, form);

        return View(new SummaryViewModel
        {
            TaxYear  = ctrl.TaxYear,
            FormName = form,
            Rows     = rows
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private TaxControlRecord? LoadControl()
    {
        var json = HttpContext.Session.GetString("TaxControl");
        return json is null ? null
            : System.Text.Json.JsonSerializer.Deserialize<TaxControlRecord>(json);
    }
}
