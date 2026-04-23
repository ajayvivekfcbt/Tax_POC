using Microsoft.AspNetCore.Mvc;
using Tx9501.Models;
using Tx9501.Models.Entities;
using Tx9501.Models.ViewModels;
using Tx9501.Services;

namespace Tx9501.Controllers;

/// <summary>
/// Replaces tx9560.sqlrpgle / tx9560fm.dspf / tx9562.rpgle / tx9562fm.dspf /
///          tx9563.rpgle / tx9565r.rpgle / tx9565d.dspf — IRS extract workflow.
/// </summary>
public class ExtractController : Controller
{
    private readonly IExtractService _extractSvc;

    public ExtractController(IExtractService extractSvc)
    {
        _extractSvc = extractSvc;
    }

    // ── Index — extract list subfile (TX9560 SFLEXTC) ─────────────────────

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var ctrl = LoadControl();
        if (ctrl is null) return RedirectToAction("YearSelect", "TaxReporting");

        var rows = await _extractSvc.ListExtractsAsync(ctrl.TaxYear);
        ViewBag.StatusMessage = TempData["StatusMessage"] as string;

        return View(new ExtractListViewModel
        {
            TaxYear  = ctrl.TaxYear,
            Extracts = rows
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(ExtractListViewModel vm)
    {
        if (vm.ExitPressed)
        {
            HttpContext.Session.Clear();
            return RedirectToAction("YearSelect", "TaxReporting");
        }

        if (vm.AddPressed)
            return RedirectToAction("Define", new { year = vm.TaxYear });

        if (!string.IsNullOrEmpty(vm.SelectedOption) && vm.SelectedSeq.HasValue)
        {
            switch (vm.SelectedOption)
            {
                case "1":  // Select / Build
                    return RedirectToAction("Setup", new { year = vm.TaxYear, seq = vm.SelectedSeq.Value });

                case "4":  // Clear
                    await _extractSvc.ClearExtractAsync(vm.TaxYear, vm.SelectedSeq.Value);
                    TempData["StatusMessage"] = $"Extract {vm.SelectedSeq} cleared.";
                    break;

                case "5":  // Transmit
                    await _extractSvc.TransmitExtractAsync(vm.TaxYear, vm.SelectedSeq.Value);
                    TempData["StatusMessage"] =
                        $"Extract {vm.SelectedSeq} transmit completed. " +
                        "If TX9565R is unavailable on IBM i, web fallback was used.";
                    break;

                case "9":  // Display detail
                    return RedirectToAction("FileViewer", new
                    {
                        year = vm.TaxYear,
                        seq  = vm.SelectedSeq.Value
                    });

                case "X":  // Download IRS file
                    var dl = await _extractSvc.DownloadExtractAsync(vm.TaxYear, vm.SelectedSeq.Value);
                    if (dl is null)
                    {
                        TempData["StatusMessage"] =
                            $"No generated IRS file found for extract {vm.SelectedSeq}. " +
                            "Run option 1 (Build) first.";
                        break;
                    }

                    return File(dl.Value.Content, "text/plain", dl.Value.FileName);
            }
        }

        return RedirectToAction("Index");
    }

    // ── Define — add/display extract header (TX9560 DEFINE) ──────────────

    [HttpGet]
    public async Task<IActionResult> Define(string year, decimal? seq, bool display = false)
    {
        ExtractControlRecord? existing = null;
        if (seq.HasValue)
        {
            var all = await _extractSvc.ListExtractsAsync(year);
            existing = all.FirstOrDefault(r => r.ExtSeq == seq.Value);
        }

        var vm = new ExtractDefineViewModel
        {
            TaxYear     = year,
            ExtSeq      = seq,
            Description = existing?.ExtDesc ?? "",
            SelectDate  = existing?.ExtSelDat ?? DateTime.Now.ToString("yyyy-MM-dd"),
            XmtrName    = existing?.XmtrName ?? "",
            XmtrName2   = existing?.XmtrName2 ?? "",
            IsReadOnly  = display
        };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Define(ExtractDefineViewModel vm)
    {
        if (vm.CancelPressed)
            return RedirectToAction("Index");

        if (!ModelState.IsValid) return View(vm);

        var seq = await _extractSvc.CreateExtractAsync(
            vm.TaxYear, vm.Description, vm.SelectDate, vm.XmtrName, vm.XmtrName2);

        TempData["StatusMessage"] = $"Extract {seq} created.";
        return RedirectToAction("Setup", new { year = vm.TaxYear, seq });
    }

    [HttpGet]
    public async Task<IActionResult> FileViewer(string year, decimal seq)
    {
        var all = await _extractSvc.ListExtractsAsync(year);
        var existing = all.FirstOrDefault(r => r.ExtSeq == seq);

        var dl = await _extractSvc.DownloadExtractAsync(year, seq);
        var lines = new List<string>();
        var fileName = string.Empty;
        var errorMessage = string.Empty;

        if (dl is null)
        {
            errorMessage = "Generated IRS extract file not found. Run option 1 (Build) first.";
        }
        else
        {
            fileName = dl.Value.FileName;
            var content = System.Text.Encoding.UTF8.GetString(dl.Value.Content);
            lines = content
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .ToList();
        }

        return View(new ExtractFileViewerViewModel
        {
            TaxYear = year,
            ExtSeq = seq,
            RunDescription = existing?.ExtDesc ?? string.Empty,
            RunDate = existing?.ExtDate ?? string.Empty,
            FileName = fileName,
            Lines = lines,
            ErrorMessage = errorMessage
        });
    }

    // ── Setup — select forms & associations (TX9562) ──────────────────────

    [HttpGet]
    public IActionResult Setup(string year, decimal seq)
    {
        var ctrl = LoadControl();
        return View(new ExtractSetupViewModel
        {
            TaxYear     = year,
            ExtSeq      = seq,
            FormOptions = AllForms(),
            AssocOptions = new List<string>()  // loaded client-side via JSON or from session
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Setup(ExtractSetupViewModel vm)
    {
        if (vm.CancelPressed)
            return RedirectToAction("Index");

        if (!ModelState.IsValid) return View(vm);

        var forms = vm.SelectedForms  ?? new List<string>();
        var assns = vm.SelectedAssocs ?? new List<string>();

        await _extractSvc.BuildIrsFileAsync(vm.TaxYear, vm.ExtSeq, forms, assns);
        TempData["StatusMessage"] =
            $"IRS file build completed for extract {vm.ExtSeq}. " +
            "If TX9563 is unavailable on IBM i, local web fallback was used.";
        return RedirectToAction("Index");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private TaxControlRecord? LoadControl()
    {
        var json = HttpContext.Session.GetString("TaxControl");
        return json is null ? null
            : System.Text.Json.JsonSerializer.Deserialize<TaxControlRecord>(json);
    }

    private static string[] AllForms() => new[]
    {
        "1098", "1099-A", "1099-MISC", "1099-NEC", "1099-INT", "1099-DIV"
    };
}
