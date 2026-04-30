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
    private readonly IConfiguration _cfg;

    public ExtractController(IExtractService extractSvc, ILogger<ExtractController> logger, IConfiguration cfg)
    {
        _extractSvc = extractSvc;
        _logger = logger;
        _cfg = cfg;
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
            _logger.LogInformation("ExitPressed detected");
            HttpContext.Session.Clear();
            return RedirectToAction("YearSelect", "TaxReporting");
        }

        if (vm.AddPressed)
        {
            _logger.LogInformation("AddPressed detected");
            return RedirectToAction("Create", new { year = vm.TaxYear });
        }

        if (vm.ExecutePressed)
        {
            _logger.LogInformation("ExecutePressed detected, scanning for opt_ fields");
            // Scan the posted form for an opt_SEQ field that has a value.
            string? option = null;
            long? seq   = null;

            foreach (var key in Request.Form.Keys)
            {
                if (!key.StartsWith("opt_", StringComparison.OrdinalIgnoreCase)) continue;
                var raw = Request.Form[key].ToString().Trim().ToUpper();
                _logger.LogInformation("Found opt field: {Key}={Value}", key, raw);
                if (string.IsNullOrEmpty(raw)) continue;
                var seqStr = key.Substring(4);
                if (!long.TryParse(seqStr, out var parsedSeq)) 
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
                TempData["ErrorMessage"] = "Type an option (3, 4, 5, 9, D, Z, X, F) next to a row and press Enter=Execute. Use F6=Add to create extracts.";
                return RedirectToAction("Index");
            }

            _logger.LogInformation("Executing option {Option} for seq {Seq}", option, seq);
            switch (option)
            {
                case "1":
                    // Option 1 disabled - use Define/Setup workflow instead
                    TempData["ErrorMessage"] = "Option 1 (Auto-build) is disabled. Use Define screen to create extracts manually.";
                    return RedirectToAction("Index");

                case "3":
                    // 3 = Delete from SQLite only
                    await _extractSvc.DeleteLocalExtractAsync(vm.TaxYear, seq.Value);
                    TempData["StatusMessage"] = $"Extract {seq} deleted from SQLite local database.";
                    return RedirectToAction("Index");

                case "5":
                    var transmitResult = await _extractSvc.TransmitExtractAsync(vm.TaxYear, seq.Value);
                    if (!transmitResult.Success)
                    {
                        TempData["ErrorMessage"] = 
                            $"Extract file not found for sequence {seq}. " +
                            "Please create (Option 1) and build the extract first, then transmit.";
                    }
                    else if (transmitResult.DirectProgramCallFailed)
                    {
                        TempData["StatusMessage"] =
                            $"Extract {seq} transmit completed with fallback. " +
                            "Direct GOANYWHERE/RUNPROJECT call failed; SSH fallback was attempted.";
                    }
                    else
                    {
                        TempData["StatusMessage"] =
                            $"Extract {seq} transmit completed. " +
                            "If configured, GoAnywhere EXE launch was attempted. " +
                            "If TX9565R is unavailable on IBM i, web fallback was used.";
                    }
                    return RedirectToAction("Index");

                case "9":
                    return RedirectToAction("FileSummary", new { year = vm.TaxYear, seq = seq.Value });

                case "F":
                    // F6 (Edit): show Define screen to select forms/associations
                    return RedirectToAction("Define", new { year = vm.TaxYear, seq = seq.Value });

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
                    TempData["ErrorMessage"] = $"Option '{option}' is not valid. Use 1, 4, 5, 9, F, or X.";
                    return RedirectToAction("Index");
            }
        }

        _logger.LogWarning("No button detected or no action matched - falling through to Index. Buttons: Execute={Execute} Add={Add} Exit={Exit}",
            vm.ExecutePressed, vm.AddPressed, vm.ExitPressed);
        return RedirectToAction("Index");
    }

    // ── Define — add/display extract header (TX9560 DEFINE) ──────────────

    [HttpGet]
    public async Task<IActionResult> Create(string year)
    {
        var vm = await BuildExtractCreateViewModelAsync(year);
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ExtractCreateViewModel vm)
    {
        if (vm.CancelPressed)
            return RedirectToAction("Index");

        if (!ModelState.IsValid)
        {
            var rebound = await BuildExtractCreateViewModelAsync(vm.TaxYear);
            rebound.Description = vm.Description;
            rebound.SelectDate = vm.SelectDate;
            rebound.MediaNumber = vm.MediaNumber;
            rebound.ReplaceCode = vm.ReplaceCode;
            rebound.ReplaceFile = vm.ReplaceFile;
            rebound.TestFile = vm.TestFile;
            rebound.PriorYear = vm.PriorYear;
            return View(rebound);
        }

        var seq = await _extractSvc.CreateExtractAsync(
            vm.TaxYear,
            vm.Description,
            vm.SelectDate.ToString("yyyy-MM-dd"),
            vm.TransmitterName,
            vm.CompanyName2);

        TempData["StatusMessage"] = $"Extract {seq} created.";
        return RedirectToAction("Setup", new { year = vm.TaxYear, seq });
    }

    [HttpGet]
    public async Task<IActionResult> Define(string year, long? seq, bool display = false)
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
            FormOptions = AllForms(),
            AssocOptions = (await _extractSvc.GetAvailableAssociationsAsync(year))
                .Select(a => a.CorpCode)
                .OrderBy(a => a)
                .ToList(),
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

        // Store selected forms and associations in TempData to pass to Setup
        TempData["SelectedForms"] = string.Join(",", vm.SelectedForms ?? new List<string>());
        TempData["SelectedAssocs"] = string.Join(",", vm.SelectedAssocs ?? new List<string>());

        TempData["StatusMessage"] = $"Extract {seq} created.";
        return RedirectToAction("Setup", new { year = vm.TaxYear, seq });
    }

    [HttpGet]
    public async Task<IActionResult> FileSummary(string year, long seq)
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
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
        }

        return View(new ExtractFileSummaryViewModel
        {
            TaxYear = year,
            ExtSeq = seq,
            RunDescription = existing?.ExtDesc ?? string.Empty,
            RunDate = existing?.ExtDate ?? string.Empty,
            FileName = fileName,
            Summaries = BuildFormSummaries(lines),
            ErrorMessage = errorMessage
        });
    }

    [HttpGet]
    public async Task<IActionResult> FileViewer(string year, long seq)
    {
        var all = await _extractSvc.ListExtractsAsync(year);
        var existing = all.FirstOrDefault(r => r.ExtSeq == seq);

        var selectedForm = (Request.Query["form"].ToString() ?? string.Empty).Trim();
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
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (!string.IsNullOrWhiteSpace(selectedForm))
            {
                lines = FilterLinesForForm(lines, selectedForm);
                if (lines.Count == 0)
                {
                    errorMessage = $"No extract records found for form {selectedForm}.";
                }
            }
        }

        return View(new ExtractFileViewerViewModel
        {
            TaxYear = year,
            ExtSeq = seq,
            RunDescription = existing?.ExtDesc ?? string.Empty,
            RunDate = existing?.ExtDate ?? string.Empty,
            FileName = fileName,
            Lines = lines,
            SelectedForm = selectedForm,
            ErrorMessage = errorMessage
        });
    }

    // ── Setup — select forms & associations (TX9562) ──────────────────────

    [HttpGet]
    public async Task<IActionResult> Setup(string year, long seq)
    {
        var ctrl = LoadControl();
        
        // Load associations for the year
        var assocList = await _extractSvc.GetAvailableAssociationsAsync(year);
        var assocOptions = assocList
            .Select(a => a.CorpCode)
            .OrderBy(a => a)
            .ToList();

        // Get selections from TempData (passed from Define), or use defaults
        var selectedFormsStr = TempData["SelectedForms"] as string ?? string.Empty;
        var selectedAssocsStr = TempData["SelectedAssocs"] as string ?? string.Empty;

        var selectedForms = selectedFormsStr
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrEmpty(f))
            .ToList();

        var selectedAssocs = selectedAssocsStr
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => !string.IsNullOrEmpty(a))
            .ToList();

        return View(new ExtractSetupViewModel
        {
            TaxYear     = year,
            ExtSeq      = seq,
            FormOptions = AllForms(),
            SelectedForms = selectedForms,
            AssocOptions = assocOptions,
            SelectedAssocs = selectedAssocs
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

    // ── BuildDirect — create new extract and build with all forms/associations ──

    [HttpGet]
    public async Task<IActionResult> BuildDirect(string year)
    {
        try
        {
            // Create new extract with default values
            var description = $"Auto-build {DateTime.Now:yyyy-MM-dd HH:mm}";
            var selectDate = DateTime.Now.ToString("yyyy-MM-dd");
            var seq = await _extractSvc.CreateExtractAsync(year, description, selectDate, "", "");

            // Get all available forms and associations
            var allForms = new List<string> { "1098", "1099-A", "1099-MISC", "1099-NEC", "1099-INT", "1099-DIV" };
            var allAssocs = (await _extractSvc.GetAvailableAssociationsAsync(year))
                .Select(a => a.CorpCode)
                .ToList();

            // Build IRS file with all forms and associations
            await _extractSvc.BuildIrsFileAsync(year, seq, allForms, allAssocs);

            TempData["StatusMessage"] =
                $"Extract {seq} created and built successfully with all forms and associations.";
            return RedirectToAction("Index");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in BuildDirect");
            TempData["ErrorMessage"] = $"Error creating/building extract: {ex.Message}";
            return RedirectToAction("Index");
        }
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

    private async Task<ExtractCreateViewModel> BuildExtractCreateViewModelAsync(string year)
    {
        var rows = await _extractSvc.ListExtractsAsync(year);
        var nextSeq = rows.Count == 0 ? 1L : rows.Max(r => r.ExtSeq) + 1L;
        var lastExtract = rows
            .OrderByDescending(r => r.ExtSeq)
            .FirstOrDefault();

        var defaultSelectDate = DateTime.Today;
        if (lastExtract is not null && TryParseExtractDate(lastExtract.ExtSelDat, out var parsedSelectDate))
        {
            defaultSelectDate = parsedSelectDate;
        }

        var transmitterName = ResolvePrefill(
            _cfg["Transmitter:CompanyName"],
            lastExtract?.XmtrName);

        var companyName2 = ResolvePrefill(
            _cfg["Transmitter:CompanyName2"],
            lastExtract?.XmtrName2,
            transmitterName);

        var taxId = ResolvePrefill(_cfg["Transmitter:TransmitterTIN"]);
        var controlCode = ResolvePrefill(_cfg["Transmitter:TransmitterFEI"]);

        return new ExtractCreateViewModel
        {
            TaxYear = year,
            NextExtSeq = nextSeq,
            Description = string.Empty,
            SelectDate = defaultSelectDate,
            ExtractedAtDisplay = "0001-01-01-00.00.00.000000",
            TransmitterName = transmitterName,
            CompanyName = transmitterName,
            CompanyName2 = companyName2,
            MailAddress = ResolvePrefill(_cfg["Transmitter:Address"]),
            MailCity = ResolvePrefill(_cfg["Transmitter:City"]),
            MailState = ResolvePrefill(_cfg["Transmitter:State"]),
            MailZip = (_cfg["Transmitter:Zip"] ?? string.Empty).Replace("-", string.Empty),
            ContactName = ResolvePrefill(_cfg["Transmitter:ContactName"]),
            ContactPhone = (_cfg["Transmitter:ContactPhone"] ?? string.Empty).Replace("-", string.Empty),
            ContactEmail = ResolvePrefill(_cfg["Transmitter:ContactEmail"]),
            TaxId = taxId,
            ControlCode = controlCode,
            MediaNumber = string.Empty,
            ReplaceCode = string.Empty,
            ReplaceFile = string.Empty,
            TestFile = string.Empty,
            PriorYear = string.Empty
        };
    }

    private static string ResolvePrefill(params string?[] candidates)
    {
        foreach (var value in candidates)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    private static bool TryParseExtractDate(string? raw, out DateTime parsed)
    {
        parsed = DateTime.MinValue;
        var value = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (DateTime.TryParseExact(value, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out parsed))
        {
            return true;
        }

        if (DateTime.TryParse(value, out parsed))
        {
            return true;
        }

        return false;
    }

    private static List<ExtractFormSummaryRow> BuildFormSummaries(IList<string> lines)
    {
        var map = new Dictionary<string, ExtractFormSummaryRow>(StringComparer.OrdinalIgnoreCase);
        string currentForm = string.Empty;

        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
                continue;

            var recType = line[0];
            switch (recType)
            {
                case 'A':
                    currentForm = ReadFormFromARecord(line);
                    if (string.IsNullOrWhiteSpace(currentForm))
                    {
                        currentForm = "UNKNOWN";
                    }

                    if (!map.TryGetValue(currentForm, out var row))
                    {
                        row = new ExtractFormSummaryRow { FormType = currentForm };
                        map[currentForm] = row;
                    }

                    row.ARecords++;
                    row.TotalRecords++;
                    break;

                case 'B':
                    if (!string.IsNullOrWhiteSpace(currentForm) && map.TryGetValue(currentForm, out var bRow))
                    {
                        bRow.BRecords++;
                        bRow.TotalRecords++;
                    }
                    break;

                case 'C':
                    if (!string.IsNullOrWhiteSpace(currentForm) && map.TryGetValue(currentForm, out var cRow))
                    {
                        cRow.CRecords++;
                        cRow.TotalRecords++;
                    }
                    break;
            }
        }

        return map.Values
            .OrderBy(v => v.FormType, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> FilterLinesForForm(IList<string> lines, string selectedForm)
    {
        var targetForm = selectedForm.Trim();
        var filtered = new List<string>();
        var includeCurrentGroup = false;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var recType = line[0];
            if (recType == 'T' || recType == 'F')
            {
                filtered.Add(line);
                continue;
            }

            if (recType == 'A')
            {
                var form = ReadFormFromARecord(line);
                includeCurrentGroup = string.Equals(form, targetForm, StringComparison.OrdinalIgnoreCase);
                if (includeCurrentGroup)
                {
                    filtered.Add(line);
                }
                continue;
            }

            if ((recType == 'B' || recType == 'C') && includeCurrentGroup)
            {
                filtered.Add(line);
            }
        }

        return filtered;
    }

    private static string ReadFormFromARecord(string line)
    {
        const int startIndex = 47; // 1-based 48, length 40
        const int length = 40;

        if (line.Length <= startIndex)
            return string.Empty;

        var safeLength = Math.Min(length, line.Length - startIndex);
        return line.Substring(startIndex, safeLength).Trim();
    }
}
