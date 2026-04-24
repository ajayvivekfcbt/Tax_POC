using Microsoft.AspNetCore.Mvc;
using Tx9501.Models;
using Tx9501.Models.Entities;
using Tx9501.Models.ViewModels;
using Tx9501.Services;

namespace Tx9501.Controllers;

/// <summary>
/// Replaces tx9525.sqlrpgle + tx9525fm.dspf — Maintain tax records (SELECT / MAINTAIN formats).
/// </summary>
public class MaintainController : Controller
{
    private const int PageSize = 50;
    private readonly IMaintainService _maintainSvc;

    public MaintainController(IMaintainService maintainSvc)
    {
        _maintainSvc = maintainSvc;
    }

    // ── SELECT format — look up a record ──────────────────────────────────

    [HttpGet]
    public IActionResult Select()
    {
        var ctrl = LoadControl();
        var selectedForm = HttpContext.Session.GetString("SelectedForm") ?? "";
        if (ctrl is null) { return RedirectToAction("YearSelect", "TaxReporting"); }

        return View(new MaintainSelectViewModel
        {
            TaxYear  = ctrl.TaxYear,
            FormName = selectedForm
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Select(MaintainSelectViewModel vm)
    {
        if (!ModelState.IsValid)
        {
            return View(vm);
        }

        TaxDetailRecord? record;
        try
        {
            record = await _maintainSvc.GetRecordAsync(
                vm.TaxYear, vm.FormName, vm.Assoc, vm.MemberNo, vm.MemberSub);
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Lookup error: {ex.Message}";
            return View(vm);
        }

        if (record is null)
        {
            if (!vm.AddPressed)
            {
                ModelState.AddModelError(nameof(vm.MemberNo), "Account not found.");
                return View(vm);
            }

            TempData["MaintainMode"] = "ADD";
            TempData["MaintainKey"]  = $"{vm.Assoc}|{vm.MemberNo}|{vm.MemberSub}";
            return RedirectToAction("Record", new
            {
                asa    = vm.Assoc,
                mbrNo  = vm.MemberNo,
                mbrSub = vm.MemberSub,
                mode   = "ADD"
            });
        }

        if (vm.AddPressed)
        {
            ModelState.AddModelError(nameof(vm.MemberNo), "Account already exists - add not allowed.");
            return View(vm);
        }

        TempData["MaintainMode"] = "CHANGE";
        // Pass the actual MbrSub from the found record (not from user input),
        // so that Record GET can locate the same row on IBM i.
        return RedirectToAction("Record", new
        {
            asa    = record.Asa,
            mbrNo  = record.MbrNo,
            mbrSub = record.MbrSub,
            mode   = "CHANGE"
        });
    }

    // ── MAINTAIN format — view / edit the record ──────────────────────────

    [HttpGet]
    public async Task<IActionResult> Record(string asa, decimal mbrNo, string mbrSub, string mode)
    {
        var ctrl = LoadControl();
        if (ctrl is null) return RedirectToAction("YearSelect", "TaxReporting");

        var form = HttpContext.Session.GetString("SelectedForm") ?? "";
        TaxDetailRecord rec;

        if (mode.Equals("ADD", StringComparison.OrdinalIgnoreCase))
        {
            rec = new TaxDetailRecord
            {
                TaxYear = ctrl.TaxYear,
                Form    = form,
                Asa     = asa,
                MbrNo   = mbrNo,
                MbrSub  = mbrSub
            };
        }
        else
        {
            var existing = await _maintainSvc.GetRecordAsync(ctrl.TaxYear, form, asa, mbrNo, mbrSub);
            if (existing is null) return NotFound();
            rec = existing;
        }

        return View(new MaintainRecordViewModel
        {
            Mode    = mode,
            Record  = rec,
            FormName = form
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Record(MaintainRecordViewModel vm)
    {
        // Check F12 Cancel FIRST – allow user to cancel regardless of validation
        if (vm.ExitPressed)
        {
            return RedirectToAction("Select");
        }

        // Handle delete before validation; delete only applies to staged SQLite records.
        if (vm.DeletePressed)
        {
            var deleted = await _maintainSvc.DeleteRecordAsync(vm.Record);
            TempData["StatusMessage"] = deleted
                ? "Record deleted from local staging data."
                : "Delete applies only to staged SQLite records. Record was not found in local staging data.";
            return RedirectToAction("Select");
        }

        // Clear validation errors for optional fields
        ModelState.Remove("Record.CorrIn");
        ModelState.Remove("Record.MbrSub");
        ModelState.Remove("Record.SecAddr");
        ModelState.Remove("Record.SecDesc");
        ModelState.Remove("Record.BorrAddrX");

        ValidateMandatoryFields(vm);
        
        if (!ModelState.IsValid) 
        {
            return View(vm);
        }

        if (vm.Mode.Equals("ADD", StringComparison.OrdinalIgnoreCase))
            await _maintainSvc.AddRecordAsync(vm.Record);
        else
            await _maintainSvc.UpdateRecordAsync(vm.Record);
        TempData["StatusMessage"] = vm.Mode == "ADD" ? "Record added." : "Record updated.";
        return RedirectToAction("Select");
    }

    private void ValidateMandatoryFields(MaintainRecordViewModel vm)
    {
        if (vm.Record is null)
        {
            ModelState.AddModelError(string.Empty, "Record data is required.");
            return;
        }

        if (vm.Record.SsiDn <= 0)
            ModelState.AddModelError("Record.SsiDn", "SSN/EIN is required.");

        if (string.IsNullOrWhiteSpace(vm.Record.SsiDc))
            ModelState.AddModelError("Record.SsiDc", "Type is required.");

        if (string.IsNullOrWhiteSpace(vm.Record.BorrName))
            ModelState.AddModelError("Record.BorrName", "Name is required.");

        if (string.IsNullOrWhiteSpace(vm.Record.BorrAddr))
            ModelState.AddModelError("Record.BorrAddr", "Address is required.");

        if (string.IsNullOrWhiteSpace(vm.Record.BorrCity))
            ModelState.AddModelError("Record.BorrCity", "City is required.");

        if (string.IsNullOrWhiteSpace(vm.Record.BorrState))
            ModelState.AddModelError("Record.BorrState", "State is required.");

        if (vm.Record.BorrZip <= 0)
            ModelState.AddModelError("Record.BorrZip", "Zip is required.");

        if (string.IsNullOrWhiteSpace(vm.Record.ReportToIrs))
            ModelState.AddModelError("Record.ReportToIrs", "Report to IRS is required.");

        if (string.Equals(vm.Record.ReportToIrs, "N", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(vm.Record.NonRptReason))
        {
            ModelState.AddModelError("Record.NonRptReason", "Non-Report Reason is required when Report to IRS is N.");
        }
    }

    // ── Error list (F10) ──────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Errors(string asa, int page = 1)
    {
        var ctrl = LoadControl();
        if (ctrl is null) return RedirectToAction("YearSelect", "TaxReporting");

        var form  = HttpContext.Session.GetString("SelectedForm") ?? "";
        var paged = await _maintainSvc.GetErrorRecordsPageAsync(ctrl.TaxYear, form, asa, page, PageSize);

        return View(new PagedTaxDetailListViewModel
        {
            TaxYear = ctrl.TaxYear,
            FormName = form,
            Assoc = asa,
            ScreenTitle = "TX9525 - Error Records",
            BackAction = "Select",
            Rows = paged.Items,
            PageNumber = paged.PageNumber,
            PageSize = paged.PageSize,
            TotalCount = paged.TotalCount
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
