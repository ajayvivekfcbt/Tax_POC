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
    private readonly ILogger<ExtractController> _logger;

    public ExtractController(IExtractService extractSvc, ILogger<ExtractController> logger)
    {
        _extractSvc = extractSvc;
        _logger = logger;
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
        // Log EVERYTHING posted
        var formDump = string.Join(", ", Request.Form.Keys.Select(k => $"{k}={Request.Form[k]}"));
        _logger.LogWarning("Extract POST dump: {FormData}", formDump);
        _logger.LogWarning("VM state: TaxYear={TaxYear} ExecutePressed={Execute} AddPressed={Add} ExitPressed={Exit}",
            vm.TaxYear, vm.ExecutePressed, vm.AddPressed, vm.ExitPressed);

        if (vm.ExitPressed)
        {
            _logger.LogInformation("ExitPressed detected");
            HttpContext.Session.Clear();
            return RedirectToAction("YearSelect", "TaxReporting");
        }

        if (vm.AddPressed)
        {
            _logger.LogInformation("AddPressed detected");
            return RedirectToAction("Define", new { year = vm.TaxYear });
        }

        if (vm.ExecutePressed)
        {
            _logger.LogInformation("ExecutePressed detected, scanning for opt_ fields");
            // Scan the posted form for an opt_SEQ field that has a value.
            string? option = null;
            decimal? seq   = null;

            foreach (var key in Request.Form.Keys)
            {
                if (!key.StartsWith("opt_", StringComparison.OrdinalIgnoreCase)) continue;
                var raw = Request.Form[key].ToString().Trim().ToUpper();
                _logger.LogInformation("Found opt field: {Key}={Value}", key, raw);
                if (string.IsNullOrEmpty(raw)) continue;
                var seqStr = key.Substring(4);
                if (!decimal.TryParse(seqStr, out var parsedSeq)) 
                {
                    _logger.LogWarning("Failed to parse seq from {Key}", key);
                    continue;
                }
                option = raw;
                seq    = parsedSeq;
                _logger.LogInformation("Selected: Option={Option} Seq={Seq}", option, seq);
                break;
            }

            if (option is null || seq is null)
            {
                _logger.LogWarning("No valid option selected");
                TempData["ErrorMessage"] = "Type an option (1, 4, 5, 9, X) next to a row and press Enter=Execute.";
                return RedirectToAction("Index");
            }

            _logger.LogInformation("Executing option {Option} for seq {Seq}", option, seq);
            switch (option)
            {
                case "1":
                    return RedirectToAction("Setup", new { year = vm.TaxYear, seq = seq.Value });

                case "4":
                    await _extractSvc.ClearExtractAsync(vm.TaxYear, seq.Value);
                    TempData["StatusMessage"] = $"Extract {seq} cleared.";
                    break;

                case "5":
                    await _extractSvc.TransmitExtractAsync(vm.TaxYear, seq.Value);
                    TempData["StatusMessage"] =
                        $"Extract {seq} transmit completed. " +
                        "If TX9565R is unavailable on IBM i, web fallback was used.";
                    break;

                case "9":
                    return RedirectToAction("FileViewer", new { year = vm.TaxYear, seq = seq.Value });

                case "X":
                    var dl = await _extractSvc.DownloadExtractAsync(vm.TaxYear, seq.Value);
                    if (dl is null)
                    {
                        TempData["StatusMessage"] =
                            $"No generated IRS file found for extract {seq}. Run option 1 (Build) first.";
                        break;
                    }
                    return File(dl.Value.Content, "text/plain", dl.Value.FileName);

                default:
                    TempData["ErrorMessage"] = $"Option '{option}' is not valid. Use 1, 4, 5, 9, or X.";
                    break;
            }
        }

        _logger.LogWarning("No button detected or no action matched - falling through to Index. Buttons: Execute={Execute} Add={Add} Exit={Exit}",
            vm.ExecutePressed, vm.AddPressed, vm.ExitPressed);
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
