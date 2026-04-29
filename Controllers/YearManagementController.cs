using Microsoft.AspNetCore.Mvc;
using Tx9501.Models;
using Tx9501.Models.ViewModels;
using Tx9501.Services;

namespace Tx9501.Controllers;

/// <summary>
/// Replaces tx9500.rpgle + tx9500fm.dspf — Tax Year subfile (list, add, change).
/// </summary>
public class YearManagementController : Controller
{
    private readonly IYearSelectService _yearSvc;

    public YearManagementController(IYearSelectService yearSvc)
    {
        _yearSvc = yearSvc;
    }

    // ── Index — subfile list ───────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var rows = await _yearSvc.ListYearsAsync();
        var vm   = new YearSelectListViewModel { Years = rows };
        ViewBag.StatusMessage = TempData["StatusMessage"] as string;
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(YearSelectListViewModel vm)
    {
        // The user may select option 1=Process, 2=Change, 9=Customize
        if (vm.SelectedOption == "1" && vm.SelectedYear.HasValue)
        {
            // Load into session and go to main menu — same as TX9501 YearSelect POST
            var ctrl = new TaxControlRecord { TaxYear = vm.SelectedYear.Value.ToString() };
            HttpContext.Session.SetString("TaxControl", System.Text.Json.JsonSerializer.Serialize(ctrl));
            return RedirectToAction("MainMenu", "TaxReporting");
        }

        if (vm.SelectedOption == "2" && vm.SelectedYear.HasValue)
            return RedirectToAction("Change", new { year = vm.SelectedYear.Value });

        if (vm.SelectedOption == "9" && vm.SelectedYear.HasValue)
        {
            // Customize (TXRASN) not implemented in this release – show message
            TempData["StatusMessage"] = "Customize associations: not yet available in web version.";
        }

        return RedirectToAction("Index");
    }

    // ── Add ───────────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult Add(bool returnToYearSelect = false)
        => View(new TaxYearAddChangeViewModel { ReturnToYearSelect = returnToYearSelect });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(TaxYearAddChangeViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);
        await _yearSvc.AddYearAsync(vm.TaxYear, vm.Description!);
        TempData["StatusMessage"] = $"Tax year {vm.TaxYear} added.";
        if (vm.ReturnToYearSelect)
            return RedirectToAction("YearSelect", "TaxReporting");
        return RedirectToAction("Index");
    }

    // ── Change ────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Change(int year)
    {
        var rows = await _yearSvc.ListYearsAsync();
        var row  = rows.FirstOrDefault(r => r.TaxYear == year);
        if (row is null) return NotFound();
        return View(new TaxYearAddChangeViewModel { TaxYear = year, Description = row.Description });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Change(TaxYearAddChangeViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);
        await _yearSvc.UpdateYearAsync(vm.TaxYear, vm.Description!);
        TempData["StatusMessage"] = $"Tax year {vm.TaxYear} updated.";
        return RedirectToAction("Index");
    }
}
