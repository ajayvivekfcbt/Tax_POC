using Microsoft.AspNetCore.Mvc;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
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
    private readonly IValidationStateService _validationState;
    private readonly IBuildStateService _buildStateService;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public TaxReportingController(
        IIBMiService ibmi,
        IClearTaxDataService clearTaxData,
        IYearSelectService yearSelect,
        IBuildTaxDataService buildTaxData,
        IValidateTaxService validateTax,
        IReportService reportService,
        ILogger<TaxReportingController> logger,
        IHttpContextAccessor httpContextAccessor,
        IValidationStateService validationState,
        IBuildStateService buildStateService,
        IServiceScopeFactory serviceScopeFactory)
    {
        _ibmi                = ibmi;
        _clearTaxData        = clearTaxData;
        _yearSelect          = yearSelect;
        _buildTaxData        = buildTaxData;
        _validateTax         = validateTax;
        _reportService       = reportService;
        _logger              = logger;
        _httpContextAccessor = httpContextAccessor;
        _validationState     = validationState;
        _buildStateService   = buildStateService;
        _serviceScopeFactory = serviceScopeFactory;
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

        // Call IBM i program SS1000 when 1099-MISC income option is selected (AP1099 parameter).
        if (string.Equals(selectedForm, "1099-MISC", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // TODO: Enable SS1000 call when ready
                // await _ibmi.ExecuteProgramAsync("SS1000", "AP1099");
                _logger.LogInformation("SS1000 placeholder for 1099-MISC (not yet enabled).");
                TempData["StatusMessage"] = "1099-MISC processing prepared (SS1000 pending).";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling SS1000 for 1099-MISC selection");
                TempData["ErrorMessage"] = $"Error calling IBM i program SS1000: {ex.Message}";
            }
        }

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

        // Get session ID for state lookup
        var sessionId = HttpContext.Session.Id;
        _logger.LogInformation("FormMenu GET: SessionId={SessionId}, Form={Form}", sessionId, formName);

        var validationState = _validationState.GetState(sessionId);
        var buildState = _buildStateService.GetState(sessionId);

        _logger.LogInformation("FormMenu GET state: IsValidationRunning={IsValidation}, IsBuildRunning={IsBuild}",
            validationState.IsRunning, buildState.IsRunning);

        // Async completion messages are shown client-side on the current page.
        // On refresh, clear terminal states without replaying old messages.
        if (!validationState.IsRunning && !string.IsNullOrEmpty(validationState.ResultMessage))
        {
            _validationState.ClearValidation(sessionId);
        }
        else if (!validationState.IsRunning && !string.IsNullOrEmpty(validationState.ErrorMessage))
        {
            _validationState.ClearValidation(sessionId);
        }

        if (!buildState.IsRunning && !string.IsNullOrEmpty(buildState.ResultMessage))
        {
            _logger.LogInformation("Build completed with result: {Result}", buildState.ResultMessage);
            _buildStateService.ClearBuild(sessionId);
        }
        else if (!buildState.IsRunning && !string.IsNullOrEmpty(buildState.ErrorMessage))
        {
            _logger.LogInformation("Build completed with error: {Error}", buildState.ErrorMessage);
            _buildStateService.ClearBuild(sessionId);
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
            IsValidationRunning = validationState.IsRunning,
            IsBuildRunning = buildState.IsRunning,
            BuildProgress = buildState.Progress,
            IsBuildDisabled = IsInactiveYear(control),
            BuildDisabledReason = IsInactiveYear(control)
                ? "Build Tax Records is not available when the selected tax year is inactive."
                : null,
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
                    // Check if validation is already running - prevent starting duplicate validation
                    var sessionId = HttpContext.Session.Id;
                    var validationState = _validationState.GetState(sessionId);
                    if (validationState.IsRunning)
                    {
                        // Validation already in progress, return to FormMenu to see progress
                        TempData["StatusMessage"] = "Validation is already in progress. Please wait for it to complete.";
                        return RedirectToAction(nameof(FormMenu));
                    }

                    // Redirect to association selection, then come back to ValidateAction
                    return RedirectToAction("Index", "AssociationSelect", new
                    {
                        returnAction = "ValidateAction",
                        returnController = "TaxReporting",
                        taxYear = control.TaxYear,
                        formName
                    });

                case "CLEAR":
                    var localDeletedRows = await _clearTaxData.ClearAsync(
                        control.TaxYear, formName, selectedAssociations, selectAllAssociations);
                    if (localDeletedRows > 0)
                    {
                        TempData["StatusMessage"] = $"Tax records cleared in web app. Local rows deleted: {localDeletedRows}.";
                    }
                    else
                    {
                        TempData["ErrorMessage"] = "No local SQLite tax records found to clear.";
                    }
                    TempData.Keep();  // Explicitly keep TempData for redirect
                    break;

                case "BUILD":
                    if (IsInactiveYear(control))
                    {
                        TempData["ErrorMessage"] = "Build Tax Records is disabled for inactive tax years.";
                        TempData.Keep();
                        break;
                    }

                    // Force a fresh association selection for each build request
                    // so we do not silently reuse stale ALL/session state.
                    HttpContext.Session.SetString("BuildSelectionPending", "1");
                    HttpContext.Session.Remove("BuildSelectionConfirmed");
                    HttpContext.Session.Remove("SelectedAssociations");

                    // Always route BUILD through association selection screen.
                    return RedirectToAction("Index", "AssociationSelect", new
                    {
                        returnAction = "BuildAction",
                        returnController = "TaxReporting",
                        taxYear = control.TaxYear,
                        formName
                    });

                case "SUMMARY":
                    // TX9520 is an interactive program replaced by SummaryController.
                    HttpContext.Session.SetString(SessionKeyForm, formName);
                    return RedirectToAction("Index", "Summary");

                case "ADMINCOUNT":
                    return RedirectToAction("Compare", "Admin", new
                    {
                        taxYear = control.TaxYear,
                        formName,
                        returnController = "TaxReporting",
                        returnAction = "FormMenu"
                    });

                case "PRTDTL":
                    return RedirectToAction(nameof(DetailReportSummary));

                case "PRTEXC":
                    return RedirectToAction(nameof(ExclusionReport));

                case "PRTERR":
                    return RedirectToAction(nameof(ErrorReport));

                case "MAINTAIN":
                    // TX9525 is handled by the web MaintainController — no IBM i call needed.
                    HttpContext.Session.SetString("SelectedForm", formName);
                    return RedirectToAction("Select", "Maintain");

                case "LETTER":
                    // TX9591R is rendered natively in web flow.
                    return RedirectToAction(nameof(Letters));

                default:
                    TempData["ErrorMessage"] = "No action was selected.";
                    TempData.Keep();  // Explicitly keep TempData for redirect
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing action {Action} for form {Form}",
                action, formName);
            TempData["ErrorMessage"] = $"IBM i error during {action}: {ex.Message}";
            TempData.Keep();  // Explicitly keep TempData for redirect
        }

        return RedirectToAction(nameof(FormMenu));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // VALIDATE ACTION – after association selection
    // ═══════════════════════════════════════════════════════════════════════

    [HttpGet]
    public async Task<IActionResult> ValidateAction(string? taxYear, string? formName)
    {
        // This now starts background validation instead of running it inline
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
        {
            _logger.LogWarning("ValidateAction: Missing context. Control={Control}, FormName={FormName}", 
                control?.TaxYear ?? "NULL", resolvedFormName ?? "NULL");
            return RedirectToAction(nameof(MainMenu));
        }

        // Start background validation task
        var selectedAssociations = GetSelectedAssociationCodes();
        var selectAllAssociations = AreAllAssociationsSelected();
        var sessionId = HttpContext.Session.Id;

        // Mark validation as running
        _validationState.StartValidation(sessionId);

        // Start background task (fire and forget)
        _ = Task.Run(async () =>
        {
            // Create a new service scope for the background task
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                try
                {
                    _logger.LogInformation("ValidateAction: Background task started. TaxYear={TaxYear}, Form={Form}, SessionId={SessionId}",
                        control.TaxYear, resolvedFormName, sessionId);

                    // Get a new instance of IValidateTaxService with its own scoped context
                    var validateTaxService = scope.ServiceProvider.GetRequiredService<IValidateTaxService>();

                    // Create progress reporter that updates state service (thread-safe)
                    var progress = new Progress<int>(percent =>
                    {
                        _validationState.UpdateProgress(sessionId, percent);
                    });

                    // Run validation on selected associations with progress reporting
                    var flaggedCount = await validateTaxService.ValidateAsync(
                        control.TaxYear, resolvedFormName, selectedAssociations, selectAllAssociations, progress);

                    // Build result message
                    var assnMsg = selectAllAssociations 
                        ? "all associations" 
                        : $"associations: {string.Join(", ", selectedAssociations)}";

                    var completionMsg = $"✓ Validation completed. Tax Year: {control.TaxYear}, Form: {resolvedFormName}, {assnMsg}. " +
                                        $"Result: {flaggedCount} record(s) flagged in error.";

                    _validationState.CompleteValidation(sessionId, completionMsg);
                    _logger.LogInformation("ValidateAction: Background task completed successfully. SessionId={SessionId}, Result: {Message}", 
                        sessionId, completionMsg);
                }
                catch (Exception ex)
                {
                    // Log full exception chain including inner exceptions
                    var exceptionChain = BuildExceptionChain(ex);
                    _logger.LogError(ex, "Error during background validation for form {Form}, SessionId={SessionId}. Exception chain: {ExceptionChain}", 
                        resolvedFormName, sessionId, exceptionChain);
                    _validationState.FailValidation(sessionId, $"Validation failed: {ex.Message}\n{BuildExceptionMessage(ex)}");
                }
            }
        });

        // Return to FormMenu immediately (progress bar will show on the page via polling)
        return RedirectToAction(nameof(FormMenu));
    }

    [HttpGet]
    public async Task<IActionResult> BuildAction(string? taxYear, string? formName)
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
        {
            _logger.LogWarning("BuildAction: Missing context. Control={Control}, FormName={FormName}",
                control?.TaxYear ?? "NULL", resolvedFormName ?? "NULL");
            return RedirectToAction(nameof(MainMenu));
        }

        // BuildAction must only execute after a fresh confirmation from AssociationSelect screen.
        var buildSelectionConfirmed = string.Equals(
            HttpContext.Session.GetString("BuildSelectionConfirmed") ?? string.Empty,
            "1",
            StringComparison.OrdinalIgnoreCase);
        if (!buildSelectionConfirmed)
        {
            HttpContext.Session.SetString("BuildSelectionPending", "1");
            TempData["ErrorMessage"] = "Please select associations before starting Build Tax Records.";
            return RedirectToAction("Index", "AssociationSelect", new
            {
                returnAction = "BuildAction",
                returnController = "TaxReporting",
                taxYear = control.TaxYear,
                formName = resolvedFormName
            });
        }

        // Consume confirmation so stale selections cannot auto-run future builds.
        HttpContext.Session.Remove("BuildSelectionConfirmed");
        HttpContext.Session.Remove("BuildSelectionPending");

        // Build must use an explicit selection from AssociationSelect screen.
        var selectedRaw = HttpContext.Session.GetString("SelectedAssociations");
        if (string.IsNullOrWhiteSpace(selectedRaw))
        {
            TempData["ErrorMessage"] = "Please select one or more associations (or ALL) before starting build.";
            return RedirectToAction("Index", "AssociationSelect", new
            {
                returnAction = "BuildAction",
                returnController = "TaxReporting",
                taxYear = control.TaxYear,
                formName = resolvedFormName
            });
        }

        if (IsInactiveYear(control))
        {
            TempData["ErrorMessage"] = "Build Tax Records is disabled for inactive tax years.";
            return RedirectToAction(nameof(FormMenu));
        }

        var selectedAssociations = GetSelectedAssociationCodes();
        var selectAllAssociations = AreAllAssociationsSelected();
        var sessionId = HttpContext.Session.Id;
        var currentState = _buildStateService.GetState(sessionId);

        if (currentState.IsRunning)
        {
            TempData["StatusMessage"] = "Build is already in progress. Please wait for completion.";
            return RedirectToAction(nameof(FormMenu));
        }

        _logger.LogInformation("BuildAction: Starting background build for TaxYear={TaxYear}, Form={Form}, SessionId={SessionId}, SelectAll={SelectAll}, AssocCount={AssocCount}",
            control.TaxYear, resolvedFormName, sessionId, selectAllAssociations, selectedAssociations.Count);

        var scopeDisplay = selectAllAssociations
            ? "All associations"
            : string.Join(", ", selectedAssociations);

        _buildStateService.StartBuild(sessionId, scopeDisplay);

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var scopedBuildTaxData = scope.ServiceProvider.GetRequiredService<IBuildTaxDataService>();
                var scopedBuildState = scope.ServiceProvider.GetRequiredService<IBuildStateService>();

                var progress = new Progress<int>(percent =>
                {
                    scopedBuildState.UpdateProgress(sessionId, percent);
                });

                var stagedCount = await scopedBuildTaxData.BuildAsync(
                    control.TaxYear,
                    resolvedFormName,
                    selectedAssociations,
                    selectAll: selectAllAssociations,
                    progress);

                if (stagedCount > 0)
                {
                    var resultMsg = $"Build completed. {stagedCount} tax record(s) staged in SQLite.";
                    scopedBuildState.CompleteBuild(sessionId, resultMsg);
                }
                else
                {
                    var failMsg = "Build completed with 0 staged records. Check source data, selected form, and IBM i connectivity.";
                    scopedBuildState.FailBuild(sessionId, failMsg);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BuildAction background task failed for TaxYear={TaxYear}, Form={FormName}, SessionId={SessionId}",
                    control.TaxYear, resolvedFormName, sessionId);

                using var scope = _serviceScopeFactory.CreateScope();
                var scopedBuildState = scope.ServiceProvider.GetRequiredService<IBuildStateService>();
                scopedBuildState.FailBuild(sessionId, $"Build failed: {ex.Message}");
            }
        });

        TempData["StatusMessage"] = selectAllAssociations
            ? "Build started for all associations. Progress is shown below."
            : $"Build started for associations: {string.Join(", ", selectedAssociations)}.";

        return RedirectToAction(nameof(FormMenu));
    }

    /// <summary>
    /// API endpoint to check validation status (for AJAX polling)
    /// </summary>
    [HttpGet]
    [Route("TaxReporting/GetValidationStatus")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult GetValidationStatus()
    {
        var sessionId = HttpContext.Session.Id;
        var state = _validationState.GetState(sessionId);
        var nowUtc = DateTime.UtcNow;

        return Json(new
        {
            isRunning = state.IsRunning,
            progress = state.Progress,
            result = state.ResultMessage,
            error = state.ErrorMessage,
            lastUpdated = state.LastUpdated,
            ageSeconds = Math.Max(0, (int)(nowUtc - state.LastUpdated).TotalSeconds),
            timestamp = nowUtc
        });
    }

    [HttpGet]
    [Route("TaxReporting/GetBuildStatus")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult GetBuildStatus()
    {
        var sessionId = HttpContext.Session.Id;
        var hasState = _buildStateService.HasState(sessionId);
        var state = _buildStateService.GetState(sessionId);
        var nowUtc = DateTime.UtcNow;

        _logger.LogDebug("GetBuildStatus poll: SessionId={SessionId}, IsRunning={IsRunning}, Progress={Progress}%",
            sessionId, state.IsRunning, state.Progress);

        return Json(new
        {
            hasState,
            isRunning = state.IsRunning,
            progress = state.Progress,
            scopeDisplay = state.ScopeDisplay,
            result = state.ResultMessage,
            error = state.ErrorMessage,
            lastUpdated = state.LastUpdated,
            ageSeconds = Math.Max(0, (int)(nowUtc - state.LastUpdated).TotalSeconds),
            timestamp = nowUtc
        });
    }

    [HttpGet]
    public async Task<IActionResult> DetailReportSummary()
    {
        var control = GetSessionControl();
        var formName = HttpContext.Session.GetString(SessionKeyForm);

        if (control is null || string.IsNullOrEmpty(formName))
            return RedirectToAction(nameof(MainMenu));

        try
        {
            var summary = await _reportService.GetDetailReportSummaryAsync(
                control.TaxYear,
                formName,
                new List<string>(),
                selectAll: true);

            var viewModel = new DetailReportSummaryViewModel
            {
                TaxYear = control.TaxYear,
                FormName = formName,
                FormDescription = FormDescriptions.TryGetValue(formName, out var desc) ? desc : "Unknown",
                ScreenTitle = $"Detail Report Summary - {formName}",
                ProgramName = "TX9501",
                TotalRecordCount = summary.TotalCount,
                AssociationSummaries = summary.Associations
            };

            return View(viewModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading detail report summary");
            TempData["ErrorMessage"] = $"Error loading detail report: {ex.Message}";
            return RedirectToAction(nameof(FormMenu));
        }
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
            // Detail report always starts from all associations.
            var selectedAssociations = new List<string>();
            var selectAllAssociations = true;
            var selectedAssociationFilter = NormalizeAssociationCode(assoc);

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

            // Get ALL distinct associations from both IBM i and SQLite, not just current page
            var allAvailableAssociations = await _reportService.GetDistinctAssociationsAsync(
                control.TaxYear,
                formName,
                new List<string>(),
                selectAll: true,
                TaxDetailListMode.Detail);

            var availableAssociationFilters = BuildAvailableAssociationFilters(
                allAvailableAssociations,
                selectedAssociationFilter);

            return View(BuildPagedTaxDetailViewModel(
                control.TaxYear,
                formName,
                "Tax Reporting System - Detail Report",
                "TX9530",
                paged,
                selectedAssociationFilter,
                availableAssociationFilters));
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
        return await DownloadReportCsvAsync(
            TaxDetailListMode.Detail,
            "DetailReport",
            assoc,
            useAllAssociations: false);
    }

    [HttpGet]
    public async Task<IActionResult> DownloadDetailReportPdf(string? assoc = null)
    {
        return await DownloadReportPdfAsync(
            TaxDetailListMode.Detail,
            nameof(DetailReport),
            assoc,
            useAllAssociations: false);
    }

    [HttpGet]
    public async Task<IActionResult> GetAssociationOptions(string mode, string? currentAssoc = null)
    {
        var control = GetSessionControl();
        var formName = HttpContext.Session.GetString(SessionKeyForm);

        if (control is null || string.IsNullOrEmpty(formName))
            return Json(Array.Empty<string>());

        try
        {
            IList<string> options;

            // For Error Report, show only associations that have errors
            if (mode == "Error")
            {
                var associationsWithErrors = await _reportService.GetDistinctAssociationsWithErrorsAsync(
                    control.TaxYear, formName);
                options = associationsWithErrors.ToList();
            }
            else
            {
                var reportMode = mode switch
                {
                    "Exclusion" => TaxDetailListMode.Exclusion,
                    _ => TaxDetailListMode.Detail
                };

                var sessionAssociations = GetSelectedAssociationCodes();
                var sessionSelectAll = AreAllAssociationsSelected();

                // Detail and Exclusion filter options should always be based on all associations.
                if (string.Equals(mode, "Exclusion", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mode, "Detail", StringComparison.OrdinalIgnoreCase))
                {
                    sessionAssociations = new List<string>();
                    sessionSelectAll = true;
                }

                var allAssociations = await _reportService.GetDistinctAssociationsAsync(
                    control.TaxYear, formName, sessionAssociations, sessionSelectAll, reportMode);
                options = BuildAvailableAssociationFilters(allAssociations, NormalizeAssociationCode(currentAssoc));
            }

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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            // Get all associations that have error records with timeout protection
            var associationsWithErrors = await _reportService.GetDistinctAssociationsWithErrorsAsync(
                control.TaxYear, formName);

            sw.Stop();
            _logger.LogInformation("ErrorReport: GetDistinctAssociationsWithErrors completed in {Elapsed}ms", sw.ElapsedMilliseconds);

            var selectedAssociations = associationsWithErrors;
            var selectedAssociationFilter = NormalizeAssociationCode(assoc);

            if (!string.IsNullOrEmpty(selectedAssociationFilter))
            {
                selectedAssociations = new List<string> { selectedAssociationFilter };
            }

            // Show error records from associations that have errors
            sw.Restart();
            var paged = await _reportService.GetDetailReportPageAsync(
                control.TaxYear,
                formName,
                selectedAssociations,
                selectAll: false,
                page,
                ReportPageSize,
                TaxDetailListMode.Error);
            sw.Stop();
            _logger.LogInformation("ErrorReport: GetDetailReportPageAsync completed in {Elapsed}ms with {Count} items", sw.ElapsedMilliseconds, paged.Items.Count);

            ApplyErrorDescriptions(paged.Items, formName);

            // Build the available filter options from all associations with errors
            var availableAssociationFilters = BuildAvailableAssociationFilters(
                associationsWithErrors,
                selectedAssociationFilter);

            return View(BuildPagedTaxDetailViewModel(
                control.TaxYear,
                formName,
                "Tax Reporting System - Error Report",
                "TX9532",
                paged,
                selectedAssociationFilter,
                availableAssociationFilters));
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ErrorReport: Query timeout for form {Form}", formName);
            TempData["ErrorMessage"] = "Error report query timed out. Please try filtering by a specific association or try again later.";
            return RedirectToAction(nameof(FormMenu));
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
        var control = GetSessionControl();
        var formName = HttpContext.Session.GetString(SessionKeyForm);

        if (control is null || string.IsNullOrEmpty(formName))
            return RedirectToAction(nameof(MainMenu));

        try
        {
            // Get all associations that have error records
            var associationsWithErrors = await _reportService.GetDistinctAssociationsWithErrorsAsync(
                control.TaxYear, formName);

            var selectedAssociations = associationsWithErrors;
            var selectedAssociationFilter = NormalizeAssociationCode(assoc);

            if (!string.IsNullOrEmpty(selectedAssociationFilter))
            {
                selectedAssociations = new List<string> { selectedAssociationFilter };
            }

            var rows = await GetAllReportRowsAsync(
                control.TaxYear,
                formName,
                selectedAssociations,
                selectAllAssociations: false,
                TaxDetailListMode.Error);

            var csvBytes = BuildReportCsv(rows);
            var fileName = $"{formName.Trim()}_error_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";
            return File(csvBytes, "text/csv", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting error report for form {Form}", formName);
            TempData["ErrorMessage"] = $"Unable to export error report: {ex.Message}";
            return RedirectToAction(nameof(ErrorReport));
        }
    }

    [HttpGet]
    public async Task<IActionResult> DownloadErrorReportPdf(string? assoc = null)
    {
        var control = GetSessionControl();
        var formName = HttpContext.Session.GetString(SessionKeyForm);

        if (control is null || string.IsNullOrEmpty(formName))
            return RedirectToAction(nameof(MainMenu));

        try
        {
            var associationsWithErrors = await _reportService.GetDistinctAssociationsWithErrorsAsync(
                control.TaxYear, formName);

            var selectedAssociations = associationsWithErrors;
            var selectedAssociationFilter = NormalizeAssociationCode(assoc);

            if (!string.IsNullOrEmpty(selectedAssociationFilter))
            {
                selectedAssociations = new List<string> { selectedAssociationFilter };
            }

            var rows = await GetAllReportRowsAsync(
                control.TaxYear,
                formName,
                selectedAssociations,
                selectAllAssociations: false,
                TaxDetailListMode.Error);

            var pdfBytes = BuildReportPdf(rows, control.TaxYear, formName, TaxDetailListMode.Error, selectedAssociationFilter);
            var fileName = $"{formName.Trim()}_error_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf";
            return File(pdfBytes, "application/pdf", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting error report PDF for form {Form}", formName);
            TempData["ErrorMessage"] = $"Unable to export PDF report: {ex.Message}";
            return RedirectToAction(nameof(ErrorReport));
        }
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
            // Exclusion report always starts from all associations.
            var selectedAssociations = new List<string>();
            var selectAllAssociations = true;
            var selectedAssociationFilter = NormalizeAssociationCode(assoc);

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

            // Get ALL distinct associations from both IBM i and SQLite, not just current page
            var allAvailableAssociations = await _reportService.GetDistinctAssociationsAsync(
                control.TaxYear,
                formName,
                new List<string>(),
                selectAll: true,
                TaxDetailListMode.Exclusion);

            var availableAssociationFilters = BuildAvailableAssociationFilters(
                allAvailableAssociations,
                selectedAssociationFilter);

            return View(BuildPagedTaxDetailViewModel(
                control.TaxYear,
                formName,
                "Tax Reporting System - Exclusion Report",
                "TX9531",
                paged,
                selectedAssociationFilter,
                availableAssociationFilters));
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
        return await DownloadReportCsvAsync(
            TaxDetailListMode.Exclusion,
            "ExclusionReport",
            assoc,
            useAllAssociations: true);
    }

    [HttpGet]
    public async Task<IActionResult> DownloadExclusionReportPdf(string? assoc = null)
    {
        return await DownloadReportPdfAsync(
            TaxDetailListMode.Exclusion,
            "ExclusionReport",
            assoc,
            useAllAssociations: true);
    }

    [HttpGet]
    public async Task<IActionResult> Letters(int page = 1, string? assoc = null)
    {
        var control = GetSessionControl();
        var formName = HttpContext.Session.GetString(SessionKeyForm);

        if (control is null || string.IsNullOrEmpty(formName))
            return RedirectToAction(nameof(MainMenu));

        if (!string.Equals(formName, "1098", StringComparison.OrdinalIgnoreCase))
        {
            TempData["ErrorMessage"] = "Letter processing is available only for form 1098.";
            return RedirectToAction(nameof(FormMenu));
        }

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var selectedAssociations = GetSelectedAssociationCodes();
            var selectAllAssociations = AreAllAssociationsSelected();
            var selectedAssociationFilter = NormalizeAssociationCode(assoc);

            if (!string.IsNullOrEmpty(selectedAssociationFilter))
            {
                selectedAssociations = new List<string> { selectedAssociationFilter };
                selectAllAssociations = false;
            }

            _logger.LogInformation("Letters: Starting page={Page} assoc={Assoc}", page, assoc);

            sw.Restart();
            var paged = await _reportService.GetLetterCandidatesPageAsync(
                control.TaxYear,
                selectedAssociations,
                selectAllAssociations,
                page,
                ReportPageSize);
            _logger.LogInformation("Letters: GetLetterCandidatesPageAsync completed in {Elapsed}ms", sw.ElapsedMilliseconds);

            sw.Restart();
            var allRowsForFilters = await _reportService.GetLetterCandidatesAsync(
                control.TaxYear,
                GetSelectedAssociationCodes(),
                AreAllAssociationsSelected());
            _logger.LogInformation("Letters: GetLetterCandidatesAsync completed in {Elapsed}ms", sw.ElapsedMilliseconds);

            var availableAssociationFilters = BuildAvailableAssociationFilters(
                allRowsForFilters.Select(r => r.Asa),
                selectedAssociationFilter);

            var totalInterestAmount = allRowsForFilters.Sum(r => r.IntPd);
            var totalCustomerCount = allRowsForFilters
                .Select(r => $"{r.Asa.Trim()}|{r.MbrNo:0}|{(r.MbrSub ?? string.Empty).Trim()}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            return View(new LetterCandidateListViewModel
            {
                TaxYear = control.TaxYear,
                SelectedAssociationFilter = selectedAssociationFilter,
                AvailableAssociationFilters = availableAssociationFilters,
                Rows = paged.Items,
                PageNumber = paged.PageNumber,
                PageSize = paged.PageSize,
                TotalCount = paged.TotalCount,
                TotalInterestAmount = totalInterestAmount,
                TotalCustomerCount = totalCustomerCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building TX9591R letter page for tax year {TaxYear}", control.TaxYear);
            TempData["ErrorMessage"] = $"Unable to build letter page: {ex.Message}";
            return RedirectToAction(nameof(FormMenu));
        }
    }

    [HttpGet]
    public async Task<IActionResult> DownloadLetters(string? assoc = null)
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

            var rows = await _reportService.GetLetterCandidatesAsync(
                control.TaxYear,
                selectedAssociations,
                selectAllAssociations);

            var csvBytes = BuildLettersCsv(rows);
            var fileName = $"1098_nonreport_letters_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";
            return File(csvBytes, "text/csv", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting TX9591R letter list for tax year {TaxYear}", control.TaxYear);
            TempData["ErrorMessage"] = $"Unable to export letters: {ex.Message}";
            return RedirectToAction(nameof(Letters));
        }
    }

    [HttpGet]
    public async Task<IActionResult> DownloadLettersPdf(string? assoc = null)
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

            var rows = await _reportService.GetLetterCandidatesAsync(
                control.TaxYear,
                selectedAssociations,
                selectAllAssociations);

            var pdfBytes = BuildLettersPdf(rows, control.TaxYear, selectedAssociationFilter);
            var fileName = $"1098_nonreport_letters_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf";
            return File(pdfBytes, "application/pdf", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting TX9591R letter PDF for tax year {TaxYear}", control.TaxYear);
            TempData["ErrorMessage"] = $"Unable to export letter PDF: {ex.Message}";
            return RedirectToAction(nameof(Letters));
        }
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

    private static bool IsInactiveYear(TaxControlRecord control)
    {
        var status = control.TaxStatus?.Trim();
        return !string.IsNullOrEmpty(status)
            && status.Equals("INACTIVE", StringComparison.OrdinalIgnoreCase);
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

    private static void ApplyErrorDescriptions(
        IEnumerable<Tx9501.Models.Entities.TaxDetailRecord> rows,
        string formName)
    {
        foreach (var row in rows)
        {
            if (!string.Equals(row.Errors, "Y", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var description = BuildErrorDescription(row, formName);
            row.Errors = string.IsNullOrEmpty(description) ? "Flagged by validation" : description;
        }
    }

    private static string BuildErrorDescription(
        Tx9501.Models.Entities.TaxDetailRecord record,
        string formName)
    {
        var reasons = new List<string>();
        var normalizedForm = (formName ?? string.Empty).Trim().ToUpperInvariant();
        var ssiDc = (record.SsiDc ?? string.Empty).Trim().ToUpperInvariant();
        var reportToIrs = (record.ReportToIrs ?? string.Empty).Trim().ToUpperInvariant();
        var nonRptReason = (record.NonRptReason ?? string.Empty).Trim();
        var foreign = string.Equals((record.Foreign ?? string.Empty).Trim(), "Y", StringComparison.OrdinalIgnoreCase);

        if (reportToIrs != "Y" && reportToIrs != "N")
            reasons.Add("Report-to-IRS must be Y or N");

        if (reportToIrs == "N" && string.IsNullOrWhiteSpace(nonRptReason))
            reasons.Add("Non-report reason is required when Report-to-IRS is N");

        if (reportToIrs == "Y" && !string.IsNullOrWhiteSpace(nonRptReason))
            reasons.Add("Non-report reason must be blank when Report-to-IRS is Y");

        if (record.SsiDn <= 0)
            reasons.Add("Taxpayer ID number is missing");

        if (record.SsiDn is 111111111 or 222222222 or 333333333 or 444444444
            or 555555555 or 666666666 or 777777777 or 888888888 or 999999999)
            reasons.Add("Taxpayer ID number is invalid");

        if (ssiDc is not "S" and not "E")
            reasons.Add("Taxpayer ID type must be S or E");

        if (string.IsNullOrWhiteSpace(record.BorrName))
            reasons.Add("Borrower name is required");

        if (string.IsNullOrWhiteSpace(record.BorrAddr))
            reasons.Add("Address is required");

        if (string.IsNullOrWhiteSpace(record.BorrCity))
            reasons.Add("City is required");

        if (!foreign && string.IsNullOrWhiteSpace(record.BorrState))
            reasons.Add("State is required for non-foreign address");

        if (!foreign && record.BorrZip <= 0)
            reasons.Add("ZIP is required for non-foreign address");

        if (normalizedForm == "1098"
            && (!string.IsNullOrWhiteSpace(record.SecAddr)
                || !string.IsNullOrWhiteSpace(record.SecDesc)
                || !string.IsNullOrWhiteSpace(record.SecOther))
            && record.SecNum <= 0)
        {
            reasons.Add("Number of mortgaged properties is required when secured address is provided");
        }

        if (normalizedForm == "1099-A")
        {
            if (string.IsNullOrWhiteSpace(record.DteAqr) || !DateTime.TryParse(record.DteAqr, out _))
                reasons.Add("Date acquired is invalid for 1099-A");

            if (record.FmVal <= 0 || record.UnpPrn <= 0 || string.IsNullOrWhiteSpace(record.PrDesc))
                reasons.Add("Fair market value, unpaid principal, and property description are required for 1099-A");
        }

        return string.Join("; ", reasons.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private async Task<IActionResult> DownloadReportCsvAsync(
        TaxDetailListMode mode,
        string fallbackAction,
        string? assoc = null,
        bool useAllAssociations = false)
    {
        var control = GetSessionControl();
        var formName = HttpContext.Session.GetString(SessionKeyForm);

        if (control is null || string.IsNullOrEmpty(formName))
            return RedirectToAction(nameof(MainMenu));

        try
        {
            var selectedAssociations = useAllAssociations
                ? new List<string>()
                : GetSelectedAssociationCodes();
            var selectAllAssociations = useAllAssociations || AreAllAssociationsSelected();
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

    private async Task<IActionResult> DownloadReportPdfAsync(
        TaxDetailListMode mode,
        string fallbackAction,
        string? assoc = null,
        bool useAllAssociations = false)
    {
        var control = GetSessionControl();
        var formName = HttpContext.Session.GetString(SessionKeyForm);

        if (control is null || string.IsNullOrEmpty(formName))
            return RedirectToAction(nameof(MainMenu));

        try
        {
            var selectedAssociations = useAllAssociations
                ? new List<string>()
                : GetSelectedAssociationCodes();
            var selectAllAssociations = useAllAssociations || AreAllAssociationsSelected();
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

            var pdfBytes = BuildReportPdf(rows, control.TaxYear, formName, mode, selectedAssociationFilter);
            var modeName = mode.ToString().ToLowerInvariant();
            var fileName = $"{formName.Trim()}_{modeName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf";
            return File(pdfBytes, "application/pdf", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting {Mode} PDF report for form {Form}", mode, formName);
            TempData["ErrorMessage"] = $"Unable to export PDF report: {ex.Message}";
            return RedirectToAction(fallbackAction);
        }
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
                FormatTaxpayerIdForExport(row.SsiDc, row.SsiDn),
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

    private static byte[] BuildLettersCsv(IList<Tx9501.Models.Entities.TaxDetailRecord> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Association,MemberNumber,MemberSub,TaxpayerIdMasked,BorrowerName,Address,Address2,City,State,Zip,InterestPaid,NonReportReason");

        foreach (var row in rows)
        {
            var fields = new[]
            {
                row.Asa,
                row.MbrNo.ToString("0", CultureInfo.InvariantCulture),
                row.MbrSub,
                FormatTaxpayerIdForExport(row.SsiDc, row.SsiDn),
                row.BorrName,
                row.BorrAddr,
                row.BorrAddrX,
                row.BorrCity,
                row.BorrState,
                row.BorrZip.ToString("0", CultureInfo.InvariantCulture),
                row.IntPd.ToString("0.00", CultureInfo.InvariantCulture),
                row.NonRptReason
            };

            sb.AppendLine(string.Join(",", fields.Select(EscapeCsv)));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] BuildLettersPdf(
        IList<Tx9501.Models.Entities.TaxDetailRecord> rows,
        string taxYear,
        string selectedAssociationFilter)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var letterRows = rows
            .GroupBy(r => $"{NormalizeReportTextForLetter(r.Asa)}|{(long)r.MbrNo}|{NormalizeReportTextForLetter(r.MbrSub)}|{(long)r.SsiDn}", StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                return new
                {
                    Association = first.Asa,
                    MemberNo = first.MbrNo,
                    MemberSub = first.MbrSub,
                    Name = first.BorrName,
                    Address1 = first.BorrAddr,
                    Address2 = first.BorrAddrX,
                    City = first.BorrCity,
                    State = first.BorrState,
                    Zip = first.BorrZip,
                    TaxpayerType = first.SsiDc,
                    TaxpayerId = first.SsiDn,
                    InterestAmount = g.Sum(x => x.IntPd)
                };
            })
            .OrderBy(x => x.Association)
            .ThenBy(x => x.MemberNo)
            .ThenBy(x => x.MemberSub)
            .ToList();

        var assocLabel = string.IsNullOrWhiteSpace(selectedAssociationFilter)
            ? "All"
            : selectedAssociationFilter;

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(1.0f, Unit.Inch);
                page.DefaultTextStyle(t => t.FontFamily("Times New Roman").FontSize(12));

                page.Content().Column(column =>
                {
                    for (var i = 0; i < letterRows.Count; i++)
                    {
                        var row = letterRows[i];

                        column.Item().Text($"Tax Year {taxYear} - Non-1098 Interest Notification").Bold().FontSize(13);
                        column.Item().Text($"Association: {(row.Association ?? string.Empty).Trim()}    Member: {row.MemberNo:0}-{(row.MemberSub ?? string.Empty).Trim()}").FontSize(10);
                        column.Item().Text($"Generated: {DateTime.Now:MMMM d, yyyy}").FontSize(10);
                        column.Item().PaddingBottom(16).Text($"Filter: {assocLabel}").FontSize(10);

                        column.Item().Text((row.Name ?? string.Empty).Trim());
                        column.Item().Text((row.Address1 ?? string.Empty).Trim());
                        if (!string.IsNullOrWhiteSpace(row.Address2))
                            column.Item().Text(row.Address2.Trim());

                        var cityStateZip = $"{(row.City ?? string.Empty).Trim()}, {(row.State ?? string.Empty).Trim()} {FormatZipForAddress(row.Zip)}".Trim();
                        column.Item().PaddingBottom(18).Text(cityStateZip);

                        column.Item().Text("Dear Borrower,").Bold();
                        column.Item().PaddingTop(8).Text(
                            $"Our records indicate that you paid cash interest during tax year {taxYear}, " +
                            "but the amount did not meet the threshold for issuance of IRS Form 1098.");

                        column.Item().PaddingTop(8).Text(
                            $"Total interest paid for your account: {row.InterestAmount.ToString("C", CultureInfo.CurrentCulture)}.");

                        column.Item().PaddingTop(8).Text(
                            $"For your records, taxpayer identification on file is {FormatTaxpayerIdForExport(row.TaxpayerType, row.TaxpayerId)}.");

                        column.Item().PaddingTop(8).Text(
                            "Please keep this letter with your tax records. Contact your association if you have questions.");

                        column.Item().PaddingTop(24).Text("Sincerely,");
                        column.Item().PaddingTop(24).Text("Tax Reporting Department");

                        if (i < letterRows.Count - 1)
                            column.Item().PageBreak();
                    }
                });
            });
        }).GeneratePdf();
    }

    private static byte[] BuildReportPdf(
        IList<Tx9501.Models.Entities.TaxDetailRecord> rows,
        string taxYear,
        string formName,
        TaxDetailListMode mode,
        string selectedAssociationFilter)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var modeName = mode.ToString();
        var assocLabel = string.IsNullOrWhiteSpace(selectedAssociationFilter)
            ? "All"
            : selectedAssociationFilter;

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(1.2f, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(8).FontFamily("Courier New"));

                page.Header().Column(header =>
                {
                    header.Item().Text($"TX95xx - {modeName} Report").FontSize(12).Bold().FontFamily("Arial");
                    header.Item().Text($"Tax Year: {taxYear}   Form: {formName}   Association: {assocLabel}   Generated: {DateTime.Now:yyyy-MM-dd HH:mm}")
                        .FontSize(8)
                        .FontColor(Colors.Grey.Darken1);
                    header.Item().PaddingTop(3).LineHorizontal(1).LineColor(Colors.Grey.Darken1);
                });

                page.Content().PaddingTop(6).Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(1.0f); // Assoc
                        cols.RelativeColumn(1.2f); // Member
                        cols.RelativeColumn(0.8f); // Sub
                        cols.RelativeColumn(0.7f); // Type
                        cols.RelativeColumn(1.4f); // Taxpayer ID
                        cols.RelativeColumn(2.3f); // Name
                        cols.RelativeColumn(1.4f); // City
                        cols.RelativeColumn(0.7f); // State
                        cols.RelativeColumn(0.8f); // IRS
                        cols.RelativeColumn(2.6f); // Error
                    });

                    static IContainer HeaderCell(IContainer c)
                        => c.Background(Colors.Grey.Darken3).Padding(3).AlignCenter();

                    table.Header(h =>
                    {
                        h.Cell().Element(HeaderCell).Text("Assoc").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text("Member").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text("Sub").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text("Type").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text("Taxpayer ID").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text("Name").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text("City").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text("State").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text("Report").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text("Error").Bold().FontColor(Colors.White);
                    });

                    bool even = false;
                    foreach (var row in rows)
                    {
                        even = !even;
                        var bg = even ? Colors.White : Colors.Grey.Lighten4;

                        IContainer Cell(IContainer c) => c.Background(bg).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3);

                        table.Cell().Element(Cell).Text(row.Asa ?? string.Empty);
                        table.Cell().Element(Cell).Text(row.MbrNo.ToString("0", CultureInfo.InvariantCulture));
                        table.Cell().Element(Cell).Text(row.MbrSub ?? string.Empty);
                        table.Cell().Element(Cell).Text(row.SsiDc ?? string.Empty);
                        table.Cell().Element(Cell).Text(FormatTaxpayerIdForExport(row.SsiDc, row.SsiDn));
                        table.Cell().Element(Cell).Text(row.BorrName ?? string.Empty);
                        table.Cell().Element(Cell).Text(row.BorrCity ?? string.Empty);
                        table.Cell().Element(Cell).Text(row.BorrState ?? string.Empty);
                        table.Cell().Element(Cell).Text(row.ReportToIrs ?? string.Empty);
                        table.Cell().Element(Cell).Text(row.Errors ?? string.Empty);
                    }
                });

                page.Footer().AlignRight().Text(t =>
                {
                    t.Span("Page ").FontSize(8).FontColor(Colors.Grey.Darken1);
                    t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Darken1);
                    t.Span(" of ").FontSize(8).FontColor(Colors.Grey.Darken1);
                    t.TotalPages().FontSize(8).FontColor(Colors.Grey.Darken1);
                });
            });
        }).GeneratePdf();
    }

    private static string FormatTaxpayerIdForExport(string? taxpayerType, decimal taxpayerId)
    {
        var normalizedType = (taxpayerType ?? string.Empty).Trim().ToUpperInvariant();
        var digits = taxpayerId.ToString("000000000", CultureInfo.InvariantCulture);

        if (normalizedType is "S" or "E")
        {
            return $"XXX-XX-{digits[^4..]}";
        }

        return digits;
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

    private static string NormalizeReportTextForLetter(string? value)
        => (value ?? string.Empty).Trim();

    private static string FormatZipForAddress(decimal zip)
    {
        if (zip <= 0)
            return string.Empty;

        var asLong = (long)zip;
        if (asLong > 99999)
            return asLong.ToString("000000000", CultureInfo.InvariantCulture);

        return asLong.ToString("00000", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds a detailed exception message including inner exceptions
    /// </summary>
    private static string BuildExceptionMessage(Exception? ex)
    {
        if (ex is null)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        var current = ex;
        int level = 0;

        while (current is not null)
        {
            var indent = new string(' ', level * 2);
            sb.AppendLine($"{indent}[{current.GetType().Name}] {current.Message}");
            
            if (!string.IsNullOrEmpty(current.StackTrace))
            {
                // Only include first line of stack trace in error message
                var firstStackLine = current.StackTrace.Split('\n').FirstOrDefault()?.Trim();
                if (firstStackLine is not null)
                    sb.AppendLine($"{indent}  at: {firstStackLine}");
            }

            current = current.InnerException;
            level++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds a string representation of the full exception chain for logging
    /// </summary>
    private static string BuildExceptionChain(Exception? ex)
    {
        if (ex is null)
            return string.Empty;

        var chain = new List<string>();
        var current = ex;

        while (current is not null)
        {
            chain.Add($"{current.GetType().Name}: {current.Message}");
            current = current.InnerException;
        }

        return string.Join(" -> ", chain);
    }

    [HttpGet]
    [Route("TaxReporting/HealthCheck")]
    public async Task<IActionResult> HealthCheck()
    {
        var result = new Dictionary<string, object>();

        try
        {
            // Try to get a scoped TaxDetailSourceService to test the connection
            using var scope = _serviceScopeFactory.CreateScope();
            var sourceService = scope.ServiceProvider.GetRequiredService<TaxDetailSourceService>();
            
            // Query LNMASTR to get record count
            var records = await sourceService.QueryLoanMasterAsync(2025, "TXLIB", "001");
            result["LnmastrCount"] = records.Count;
            result["DdmastrQueried"] = true;
            
            result["Status"] = "SUCCESS";
            result["Message"] = $"LNMASTR returned {records.Count} records";
        }
        catch (Exception ex)
        {
            result["Status"] = "FAILED";
            result["Error"] = ex.Message;
            result["InnerError"] = ex.InnerException?.Message ?? "";
        }

        return Json(result);
    }
}
