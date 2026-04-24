using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Tx9501.Models;
using Tx9501.Models.ViewModels;
using Tx9501.Services;

namespace Tx9501.Controllers;

/// <summary>
/// Replaces tx9501cl.clp and tx9501fm.dspf.
///
/// Screen mapping
/// ──────────────
///  YearSelect()         →  TX9500 prompt (tax year entry)
///  MainMenu()           →  SF01 in TX9501FM (main tax-form menu)
///  FormMenu()           →  SF02 in TX9501FM (single-form action menu)
///
/// IBM i program calls are delegated to IIBMiService, so the IBM i
/// system remains the data and processing back-end.
/// </summary>
public sealed class TaxReportingController : Controller
{
    private const int ReportPageSize = 50;
    // Session keys
    private const string SessionKeyControl  = "TaxControl";
    private const string SessionKeyForm     = "SelectedForm";
    private const string SessionKeyFormDesc = "SelectedFormDesc";

    // Form definitions – mirrors IBM i &F_* / &D_* CHGVAR assignments
    // 1099-DIV omitted per MV1 (ITWR-8245)
    private static readonly IReadOnlyDictionary<string, string> FormDescriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["1098"]      = "Mortgage Interest",
            ["1099-INT"]  = "Interest Income",
            ["1099-PATR"] = "Patronages",
            ["1099-A"]    = "Acq./Abandonment of Secured Prop.",
            ["1099-MISC"] = "Miscellaneous Income",
            ["1099-NEC"]  = "Non-Employee Compensation",   // AP1
        };

    private readonly IIBMiService _ibmi;
    private readonly IClearTaxDataService _clearTaxData;
    private readonly IYearSelectService _yearSelect;
    private readonly IBuildTaxDataService _buildTaxData;
    private readonly IValidateTaxService _validateTax;
    private readonly IReportService _reportService;
    private readonly ILogger<TaxReportingController> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TaxReportingController(
        IIBMiService ibmi,
        IClearTaxDataService clearTaxData,
        IYearSelectService yearSelect,
        IBuildTaxDataService buildTaxData,
        IValidateTaxService validateTax,
        IReportService reportService,
        ILogger<TaxReportingController> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _ibmi                = ibmi;
        _clearTaxData        = clearTaxData;
        _yearSelect          = yearSelect;
        _buildTaxData        = buildTaxData;
        _validateTax         = validateTax;
        _reportService       = reportService;
        _logger              = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // YEAR SELECT  (replaces TX9500 program call in tx9501cl.clp)
    // ═══════════════════════════════════════════════════════════════════════

    [HttpGet]
    public async Task<IActionResult> YearSelect()
    {
        return View(await BuildYearSelectModelAsync());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> YearSelect(YearSelectViewModel model)
    {
        if (!ModelState.IsValid)
            return View(await BuildYearSelectModelAsync(model.TaxYear, model.ErrorMessage));

        var taxYear = (model.TaxYear ?? string.Empty).Trim();
        if (taxYear.Length != 4 || !taxYear.All(char.IsDigit))
        {
            model.ErrorMessage = "Tax year must be a 4-digit number (e.g. 2024).";
            return View(await BuildYearSelectModelAsync(model.TaxYear, model.ErrorMessage));
        }

        TaxControlRecord? control;
        try
        {
            control = await _ibmi.GetTaxControlAsync(taxYear);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading tax year {Year}", taxYear);
            model.ErrorMessage =
                "Could not connect to IBM i. Verify the connection settings.";
            return View(await BuildYearSelectModelAsync(model.TaxYear, model.ErrorMessage));
        }

        if (control is null)
        {
            model.ErrorMessage = $"Tax year {taxYear} was not found in the IBM i TXRCTL file.";
            return View(await BuildYearSelectModelAsync(model.TaxYear, model.ErrorMessage));
        }

        // Store control record in session (equivalent to &TXRCTL in CLP)
        HttpContext.Session.SetString(SessionKeyControl,
            JsonSerializer.Serialize(control));

        return RedirectToAction(nameof(MainMenu));
    }

    private async Task<YearSelectViewModel> BuildYearSelectModelAsync(
        string? selectedYear = null,
        string? errorMessage = null)
    {
        var vm = new YearSelectViewModel
        {
            TaxYear = (selectedYear ?? string.Empty).Trim(),
            ErrorMessage = errorMessage
        };

        try
        {
            var years = await _yearSelect.ListYearsAsync();
            vm.AvailableTaxYears = years
                .Select(y => y.TaxYear.ToString())
                .Distinct()
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to load tax year list for dropdown.");
            vm.AvailableTaxYears = new List<string>();
        }

        if (string.IsNullOrEmpty(vm.TaxYear) && vm.AvailableTaxYears.Count > 0)
            vm.TaxYear = vm.AvailableTaxYears[0];

        return vm;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MAIN MENU – SF01  (SNDRCVF RCDFMT(SF01) in tx9501cl.clp)
    // ═══════════════════════════════════════════════════════════════════════

    [HttpGet]
    public IActionResult MainMenu()
    {
        var control = GetSessionControl();
        if (control is null)
            return RedirectToAction(nameof(YearSelect));

        return View(new MainMenuViewModel
        {
            TaxControl    = control,
            SystemName    = Environment.MachineName,
            UserName      = User.Identity?.Name ?? HttpContext.Connection.RemoteIpAddress?.ToString() ?? "UNKNOWN",
            StatusMessage = TempData["StatusMessage"] as string,
            ErrorMessage  = TempData["ErrorMessage"]  as string,
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MainMenu(string selectedForm, string? extract, string? fixTaxes)
    {
        var control = GetSessionControl();
        if (control is null)
            return RedirectToAction(nameof(YearSelect));

        // ── Generate Tax Extract File (O_EXTRACT) ──────────────────────────
        // Mirrors: CALL PGM(TX9560) PARM(&TXRCTL)
        if (!string.IsNullOrEmpty(extract))
        {
            // TX9560 is an interactive program replaced by ExtractController.
            return RedirectToAction("Index", "Extract");
        }

        // ── Fix Tax Data (FIXTAXES) ────────────────────────────────────────
        // Mirrors: CALL PGM(TXFIX1)
        if (!string.IsNullOrEmpty(fixTaxes))
        {
            try
            {
                await _ibmi.ExecuteProgramAsync("TXFIX1", "TTSLIBGJN");
                TempData["StatusMessage"] = "Fix tax data program completed on IBM i.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling TXFIX1");
                TempData["ErrorMessage"] = $"Error fixing tax data: {ex.Message}";
            }
            return RedirectToAction(nameof(MainMenu));
        }

        // ── Form selection ─────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(selectedForm) ||
            !FormDescriptions.ContainsKey(selectedForm))
        {
            TempData["ErrorMessage"] = "Please select a tax form option.";
            return RedirectToAction(nameof(MainMenu));
        }

        HttpContext.Session.SetString(SessionKeyForm, selectedForm);
        HttpContext.Session.SetString(SessionKeyFormDesc, FormDescriptions[selectedForm]);

        return RedirectToAction(nameof(FormMenu));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // FORM MENU – SF02  (SNDRCVF RCDFMT(SF02) in tx9501cl.clp)
    // ═══════════════════════════════════════════════════════════════════════

    [HttpGet]
    public IActionResult FormMenu()
    {
        var control  = GetSessionControl();
        var formName = HttpContext.Session.GetString(SessionKeyForm);

        if (control is null || string.IsNullOrEmpty(formName) ||
            !FormDescriptions.ContainsKey(formName))
        {
            return RedirectToAction(nameof(MainMenu));
        }

        return View(new FormMenuViewModel
        {
            TaxControl      = control,
            FormName        = formName,
            FormDescription = HttpContext.Session.GetString(SessionKeyFormDesc)
                              ?? FormDescriptions[formName],
            // IN81 in IBM i was set only when form 1098 was selected
            ShowLetterOption = string.Equals(formName, "1098",
                                   StringComparison.OrdinalIgnoreCase),
            StatusMessage   = TempData["StatusMessage"] as string,
            ErrorMessage    = TempData["ErrorMessage"]  as string,
            SelectedAssociationsDisplay = BuildAssociationsDisplay(),
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FormMenu(string action, string? backToMain)
    {
        // F12 / Back to Main Menu – mirrors GOTO MAIN_AGAIN in CLP
        if (!string.IsNullOrEmpty(backToMain))
            return RedirectToAction(nameof(MainMenu));

        var control  = GetSessionControl();
        var formName = HttpContext.Session.GetString(SessionKeyForm);

        if (control is null || string.IsNullOrEmpty(formName))
            return RedirectToAction(nameof(MainMenu));

        var ctrl   = control.ToControlString();   // 57-char &TXRCTL parameter
        // &@ASSOCS – built from the web session (replaces the TX9505 interactive call).
        var assocs = BuildAssocsParam();
        var selectedAssociations = GetSelectedAssociationCodes();
        var selectAllAssociations = AreAllAssociationsSelected();

        try
        {
            switch (action?.ToUpperInvariant())
            {
                case "EDIT":
                    // Redirect to association selection, then come back to ValidateAction
                    return RedirectToAction("Index", "AssociationSelect", new
                    {
                        returnAction = "ValidateAction",
                        returnController = "TaxReporting",
                        taxYear = control.TaxYear,
                        formName
                    });

                case "CLEAR":
                    await _clearTaxData.ClearAsync(
                        control.TaxYear, formName, selectedAssociations, selectAllAssociations);
                    TempData["StatusMessage"] = "Tax records cleared in web app and IBM i tables.";
                    break;

                case "BUILD":
                    await _buildTaxData.BuildAsync(
                        control.TaxYear, formName, selectedAssociations, selectAllAssociations);
                    TempData["StatusMessage"] = "Tax records built in web app and staged in SQLite.";
                    break;

                case "SUMMARY":
                    // TX9520 is an interactive program replaced by SummaryController.
                    HttpContext.Session.SetString(SessionKeyForm, formName);
                    return RedirectToAction("Index", "Summary");

                case "PRTDTL":
                    return RedirectToAction(nameof(DetailReport));

                case "PRTEXC":
                    return RedirectToAction(nameof(ExclusionReport));

                case "PRTERR":
                    return RedirectToAction(nameof(ErrorReport));

                case "MAINTAIN":
                    // TX9525 is handled by the web MaintainController — no IBM i call needed.
                    HttpContext.Session.SetString("SelectedForm", formName);
                    return RedirectToAction("Select", "Maintain");

                case "LETTER":
                    // TX9505 (interactive) removed — assocs already in session.
                    await _reportService.PrintLettersAsync(
                        control.TaxYear, selectedAssociations, selectAllAssociations);
                    TempData["StatusMessage"] =
                        "Letter processing completed. If TX9591R is unavailable on IBM i, web fallback was used.";
                    break;

                default:
                    TempData["ErrorMessage"] = "No action was selected.";
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing action {Action} for form {Form}",
                action, formName);
            TempData["ErrorMessage"] = $"IBM i error during {action}: {ex.Message}";
        }

        return RedirectToAction(nameof(FormMenu));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // VALIDATE ACTION – after association selection
    // ═══════════════════════════════════════════════════════════════════════

    [HttpGet]
    public async Task<IActionResult> ValidateAction(string? taxYear, string? formName)
    {
        var control = GetSessionControl();
        var resolvedFormName = HttpContext.Session.GetString(SessionKeyForm);

        if (string.IsNullOrWhiteSpace(resolvedFormName) && !string.IsNullOrWhiteSpace(formName))
        {
            resolvedFormName = formName.Trim();
            HttpContext.Session.SetString(SessionKeyForm, resolvedFormName);
            if (FormDescriptions.TryGetValue(resolvedFormName, out var desc))
                HttpContext.Session.SetString(SessionKeyFormDesc, desc);
        }

        if (control is null && !string.IsNullOrWhiteSpace(taxYear))
        {
            try
            {
                var controlFromYear = await _ibmi.GetTaxControlAsync(taxYear.Trim());
                if (controlFromYear is not null)
                {
                    control = controlFromYear;
                    HttpContext.Session.SetString(SessionKeyControl,
                        JsonSerializer.Serialize(controlFromYear));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to restore tax control context from route tax year {TaxYear}", taxYear);
            }
        }

        if (control is null || string.IsNullOrEmpty(resolvedFormName))
            return RedirectToAction(nameof(MainMenu));

        var selectedAssociations = GetSelectedAssociationCodes();
        var selectAllAssociations = AreAllAssociationsSelected();

        try
        {
            // Run validation on selected associations
            var flaggedCount = await _validateTax.ValidateAsync(
                control.TaxYear, resolvedFormName, selectedAssociations, selectAllAssociations);

            // Build a user-friendly message showing associations validated
            var assnMsg = selectAllAssociations 
                ? "all associations" 
                : $"associations: {string.Join(", ", selectedAssociations)}";

            var completionMsg = $"✓ Validation completed. Tax Year: {control.TaxYear}, Form: {resolvedFormName}, {assnMsg}. " +
                                $"Result: {flaggedCount} record(s) flagged in error.";

            TempData["StatusMessage"] = completionMsg;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during validation for form {Form}", resolvedFormName);
            TempData["ErrorMessage"] = $"Validation failed: {ex.Message}";
        }

        return RedirectToAction(nameof(FormMenu));
    }

    [HttpGet]
    public async Task<IActionResult> DetailReport(int page = 1, string? assoc = null)
    {
        var control = GetSessionControl();
        var formName = HttpContext.Session.GetString(SessionKeyForm);

        if (control is null || string.IsNullOrEmpty(formName))
            return RedirectToAction(nameof(MainMenu));

        try
        {
            var selectedAssociations = GetSelectedAssociationCodes();
            var selectAllAssociations = AreAllAssociationsSelected();
            var selectedAssociationFilter = NormalizeAssociationCode(assoc);
            if (string.IsNullOrEmpty(selectedAssociationFilter))
                selectedAssociationFilter = "S01";

            if (!string.IsNullOrEmpty(selectedAssociationFilter))
            {
                selectedAssociations = new List<string> { selectedAssociationFilter };
                selectAllAssociations = false;
            }

            var paged = await _reportService.GetDetailReportPageAsync(
                control.TaxYear,
                formName,
                selectedAssociations,
                selectAllAssociations,
                page,
                ReportPageSize,
                TaxDetailListMode.Detail);

            return View(BuildPagedTaxDetailViewModel(
                control.TaxYear,
                formName,
                "Tax Reporting System - Detail Report",
                "TX9530",
                paged,
                selectedAssociationFilter,
                Array.Empty<string>()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building detail report for form {Form}", formName);
            TempData["ErrorMessage"] = $"Unable to build detail report: {ex.Message}";
            return RedirectToAction(nameof(FormMenu));
        }
    }

    [HttpGet]
    public async Task<IActionResult> DownloadDetailReport(string? assoc = null)
    {
        return await DownloadReportCsvAsync(TaxDetailListMode.Detail, "DetailReport", assoc);
    }

    [HttpGet]
    public async Task<IActionResult> GetAssociationOptions(string mode, string? currentAssoc = null)
    {
        var control = GetSessionControl();
        var formName = HttpContext.Session.GetString(SessionKeyForm);

        if (control is null || string.IsNullOrEmpty(formName))
            return Json(Array.Empty<string>());

        var reportMode = mode switch
        {
            "Error" => TaxDetailListMode.Error,
            "Exclusion" => TaxDetailListMode.Exclusion,
            _ => TaxDetailListMode.Detail
        };

        var sessionAssociations = GetSelectedAssociationCodes();
        var sessionSelectAll = AreAllAssociationsSelected();

        try
        {
            var allAssociations = await _reportService.GetDistinctAssociationsAsync(
                control.TaxYear, formName, sessionAssociations, sessionSelectAll, reportMode);
            var options = BuildAvailableAssociationFilters(allAssociations, NormalizeAssociationCode(currentAssoc));
            return Json(options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching association options for mode {Mode}", mode);
            return Json(Array.Empty<string>());
        }
    }

    [HttpGet]
    public async Task<IActionResult> ErrorReport(int page = 1, string? assoc = null)
    {
        var control = GetSessionControl();
        var formName = HttpContext.Session.GetString(SessionKeyForm);

        if (control is null || string.IsNullOrEmpty(formName))
            return RedirectToAction(nameof(MainMenu));

        try
        {
            var selectedAssociations = GetSelectedAssociationCodes();
            var selectAllAssociations = AreAllAssociationsSelected();
            var selectedAssociationFilter = NormalizeAssociationCode(assoc);
            if (string.IsNullOrEmpty(selectedAssociationFilter))
                selectedAssociationFilter = "S01";

            if (!string.IsNullOrEmpty(selectedAssociationFilter))
            {
                selectedAssociations = new List<string> { selectedAssociationFilter };
                selectAllAssociations = false;
            }

            var paged = await _reportService.GetDetailReportPageAsync(
                control.TaxYear,
                formName,
                selectedAssociations,
                selectAllAssociations,
                page,
                ReportPageSize,
                TaxDetailListMode.Error);

            return View(BuildPagedTaxDetailViewModel(
                control.TaxYear,
                formName,
                "Tax Reporting System - Error Report",
                "TX9532",
                paged,
                selectedAssociationFilter,
                Array.Empty<string>()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building error report for form {Form}", formName);
            TempData["ErrorMessage"] = $"Unable to build error report: {ex.Message}";
            return RedirectToAction(nameof(FormMenu));
        }
    }

    [HttpGet]
    public async Task<IActionResult> DownloadErrorReport(string? assoc = null)
    {
        return await DownloadReportCsvAsync(TaxDetailListMode.Error, "ErrorReport", assoc);
    }

    [HttpGet]
    public async Task<IActionResult> ExclusionReport(int page = 1, string? assoc = null)
    {
        var control = GetSessionControl();
        var formName = HttpContext.Session.GetString(SessionKeyForm);

        if (control is null || string.IsNullOrEmpty(formName))
            return RedirectToAction(nameof(MainMenu));

        try
        {
            var selectedAssociations = GetSelectedAssociationCodes();
            var selectAllAssociations = AreAllAssociationsSelected();
            var selectedAssociationFilter = NormalizeAssociationCode(assoc);
            if (string.IsNullOrEmpty(selectedAssociationFilter))
                selectedAssociationFilter = "S01";

            if (!string.IsNullOrEmpty(selectedAssociationFilter))
            {
                selectedAssociations = new List<string> { selectedAssociationFilter };
                selectAllAssociations = false;
            }

            var paged = await _reportService.GetDetailReportPageAsync(
                control.TaxYear,
                formName,
                selectedAssociations,
                selectAllAssociations,
                page,
                ReportPageSize,
                TaxDetailListMode.Exclusion);

            return View(BuildPagedTaxDetailViewModel(
                control.TaxYear,
                formName,
                "Tax Reporting System - Exclusion Report",
                "TX9531",
                paged,
                selectedAssociationFilter,
                Array.Empty<string>()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building exclusion report for form {Form}", formName);
            TempData["ErrorMessage"] = $"Unable to build exclusion report: {ex.Message}";
            return RedirectToAction(nameof(FormMenu));
        }
    }

    [HttpGet]
    public async Task<IActionResult> DownloadExclusionReport(string? assoc = null)
    {
        return await DownloadReportCsvAsync(TaxDetailListMode.Exclusion, "ExclusionReport", assoc);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // EXIT  (F3 in IBM i – GOTO END_PGM)
    // ═══════════════════════════════════════════════════════════════════════

    [HttpGet]
    public IActionResult Exit()
    {
        HttpContext.Session.Clear();
        return RedirectToAction(nameof(YearSelect));
    }

    [HttpPost]
    [ActionName("Exit")]
    [ValidateAntiForgeryToken]
    public IActionResult ExitPost()
    {
        HttpContext.Session.Clear();
        return RedirectToAction(nameof(YearSelect));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Error page
    // ═══════════════════════════════════════════════════════════════════════

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private TaxControlRecord? GetSessionControl()
    {
        var json = HttpContext.Session.GetString(SessionKeyControl);
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<TaxControlRecord>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the 500-char &amp;@ASSOCS parameter from the web session.
    /// In the original CLP this value was filled by calling TX9505 interactively;
    /// in the web app the user selects associations via AssociationSelectController
    /// which stores the result in the "SelectedAssociations" session key.
    /// </summary>
    private string BuildAssocsParam()
    {
        var raw = HttpContext.Session.GetString("SelectedAssociations") ?? "ALL";
        bool selectAll = raw.Equals("ALL", StringComparison.OrdinalIgnoreCase);
        var codes = selectAll
            ? new List<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

        var sb = new System.Text.StringBuilder(500);
        sb.Append(selectAll ? "ALL" : "   ");
        foreach (var c in codes)
            sb.Append(c.PadRight(3)[..3]);
        return sb.ToString().PadRight(500)[..500];
    }

    private bool AreAllAssociationsSelected()
        => string.Equals(HttpContext.Session.GetString("SelectedAssociations") ?? "ALL", "ALL", StringComparison.OrdinalIgnoreCase);

    private string BuildAssociationsDisplay()
    {
        var raw = HttpContext.Session.GetString("SelectedAssociations") ?? "ALL";
        if (raw.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            return "All associations";
        }

        var codes = raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(code => code.Trim())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return codes.Count > 0 ? string.Join(", ", codes) : "No associations selected";
    }

    private List<string> GetSelectedAssociationCodes()
    {
        var raw = HttpContext.Session.GetString("SelectedAssociations") ?? "ALL";
        if (raw.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string>();
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(code => code.Trim())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private PagedTaxDetailListViewModel BuildPagedTaxDetailViewModel(
        string taxYear,
        string formName,
        string screenTitle,
        string programName,
        PagedResult<Tx9501.Models.Entities.TaxDetailRecord> paged,
        string selectedAssociationFilter = "",
        IList<string>? availableAssociationFilters = null)
    {
        return new PagedTaxDetailListViewModel
        {
            TaxYear = taxYear,
            FormName = formName,
            FormDescription = HttpContext.Session.GetString(SessionKeyFormDesc)
                ?? FormDescriptions.GetValueOrDefault(formName, formName),
            ScreenTitle = screenTitle,
            ProgramName = programName,
            BackAction = nameof(FormMenu),
            SelectedAssociationFilter = selectedAssociationFilter,
            AvailableAssociationFilters = availableAssociationFilters ?? new List<string>(),
            Rows = paged.Items,
            PageNumber = paged.PageNumber,
            PageSize = paged.PageSize,
            TotalCount = paged.TotalCount
        };
    }

    private static string NormalizeAssociationCode(string? associationCode)
    {
        if (string.IsNullOrWhiteSpace(associationCode))
        {
            return string.Empty;
        }

        var normalized = associationCode.Trim().ToUpperInvariant();
        return normalized.Length > 3 ? normalized[..3] : normalized;
    }

    private static IList<string> BuildAvailableAssociationFilters(
        IEnumerable<string> allAssociations,
        string selectedAssociationFilter)
    {
        var normalizedOptions = allAssociations
            .Select(code => NormalizeAssociationCode(code))
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code)
            .ToList();

        if (!string.IsNullOrEmpty(selectedAssociationFilter)
            && !normalizedOptions.Contains(selectedAssociationFilter, StringComparer.OrdinalIgnoreCase))
        {
            normalizedOptions.Add(selectedAssociationFilter);
            normalizedOptions = normalizedOptions
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code)
                .ToList();
        }

        return normalizedOptions;
    }

    private async Task<IActionResult> DownloadReportCsvAsync(
        TaxDetailListMode mode,
        string fallbackAction,
        string? assoc = null)
    {
        var control = GetSessionControl();
        var formName = HttpContext.Session.GetString(SessionKeyForm);

        if (control is null || string.IsNullOrEmpty(formName))
            return RedirectToAction(nameof(MainMenu));

        try
        {
            var selectedAssociations = GetSelectedAssociationCodes();
            var selectAllAssociations = AreAllAssociationsSelected();
            var selectedAssociationFilter = NormalizeAssociationCode(assoc);

            if (!string.IsNullOrEmpty(selectedAssociationFilter))
            {
                selectedAssociations = new List<string> { selectedAssociationFilter };
                selectAllAssociations = false;
            }

            var rows = await GetAllReportRowsAsync(
                control.TaxYear,
                formName,
                selectedAssociations,
                selectAllAssociations,
                mode);

            var csvBytes = BuildReportCsv(rows);
            var modeName = mode.ToString().ToLowerInvariant();
            var fileName = $"{formName.Trim()}_{modeName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";
            return File(csvBytes, "text/csv", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting {Mode} report for form {Form}", mode, formName);
            TempData["ErrorMessage"] = $"Unable to export report: {ex.Message}";
            return RedirectToAction(fallbackAction);
        }
    }

    private async Task<List<Tx9501.Models.Entities.TaxDetailRecord>> GetAllReportRowsAsync(
        string taxYear,
        string formName,
        IList<string> selectedAssociations,
        bool selectAllAssociations,
        TaxDetailListMode mode)
    {
        const int exportPageSize = 2000;
        var firstPage = await _reportService.GetDetailReportPageAsync(
            taxYear,
            formName,
            selectedAssociations,
            selectAllAssociations,
            1,
            exportPageSize,
            mode);

        var rows = new List<Tx9501.Models.Entities.TaxDetailRecord>(firstPage.Items);
        for (var page = 2; page <= firstPage.TotalPages; page++)
        {
            var nextPage = await _reportService.GetDetailReportPageAsync(
                taxYear,
                formName,
                selectedAssociations,
                selectAllAssociations,
                page,
                exportPageSize,
                mode);
            rows.AddRange(nextPage.Items);
        }

        return rows;
    }

    private static byte[] BuildReportCsv(IList<Tx9501.Models.Entities.TaxDetailRecord> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Association,MemberNumber,MemberSub,TaxpayerType,TaxpayerId,BorrowerName,Address,Address2,City,State,Zip,ReportToIRS,Errors,InterestPaid,Points,Compensation,Rents,MedicalPayments,LegalPayments,Other,Withheld");

        foreach (var row in rows)
        {
            var fields = new[]
            {
                row.Asa,
                row.MbrNo.ToString("0", CultureInfo.InvariantCulture),
                row.MbrSub,
                row.SsiDc,
                row.SsiDn.ToString("0", CultureInfo.InvariantCulture),
                row.BorrName,
                row.BorrAddr,
                row.BorrAddrX,
                row.BorrCity,
                row.BorrState,
                row.BorrZip.ToString("0", CultureInfo.InvariantCulture),
                row.ReportToIrs,
                row.Errors,
                row.IntPd.ToString(CultureInfo.InvariantCulture),
                row.Points.ToString(CultureInfo.InvariantCulture),
                row.Compen.ToString(CultureInfo.InvariantCulture),
                row.Rents.ToString(CultureInfo.InvariantCulture),
                row.MedPay.ToString(CultureInfo.InvariantCulture),
                row.LglPay.ToString(CultureInfo.InvariantCulture),
                row.Other.ToString(CultureInfo.InvariantCulture),
                row.WthHeld.ToString(CultureInfo.InvariantCulture)
            };

            sb.AppendLine(string.Join(",", fields.Select(EscapeCsv)));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string EscapeCsv(string? value)
    {
        var normalized = value ?? string.Empty;
        if (normalized.Contains('"'))
        {
            normalized = normalized.Replace("\"", "\"\"");
        }

        if (normalized.Contains(',') || normalized.Contains('\n') || normalized.Contains('\r') || normalized.Contains('"'))
        {
            return $"\"{normalized}\"";
        }

        return normalized;
    }
}
