using Tx9501.Models.Entities;

namespace Tx9501.Services;

/// <summary>
/// Orchestrates the TX9515 equivalent build process in .NET:
/// 1. Queries source files (LNMASTR, DDMASTR, SHCRCT/SHCRPR)
/// 2. Transforms records to TXRDTL format
/// 3. Returns built tax detail records for staging
/// </summary>
public sealed class TX9515BuildService
{
    private readonly TaxDetailSourceService _sourceService;
    private readonly TaxDetailTransformService _transformService;
    private readonly TX9540MiscNecService _miscNecService;
    private readonly TX9517PatronageService _patronageService;
    private readonly ILogger<TX9515BuildService> _logger;

    public TX9515BuildService(
        TaxDetailSourceService sourceService,
        TaxDetailTransformService transformService,
        TX9540MiscNecService miscNecService,
        TX9517PatronageService patronageService,
        ILogger<TX9515BuildService> logger)
    {
        _sourceService = sourceService;
        _transformService = transformService;
        _miscNecService = miscNecService;
        _patronageService = patronageService;
        _logger = logger;
    }

    /// <summary>
    /// Build TXRDTL records for the specified form type.
    /// Equivalent to calling TX9515 with form-specific processing.
    /// If associationId is provided, will lookup its library from FCMCCR.
    /// </summary>
    public async Task<List<TaxDetailRecord>> BuildTaxDetailsAsync(
        int taxYear,
        string formName,
        string corpCode,
        string branchLib,
        bool isCurrentYear,
        IProgress<int>? progress = null,
        string? associationId = null,
        string parentLib = "")
    {
        var results = new List<TaxDetailRecord>();
        progress?.Report(20);

        var effectiveCorpCode = string.IsNullOrWhiteSpace(associationId)
            ? corpCode
            : associationId.Trim();

        _logger.LogInformation(
            "BuildTaxDetailsAsync started: TaxYear={TaxYear}, Form={FormName}, Corp={CorpCode}, BranchLib={BranchLib}, ParentLib={ParentLib}, AssocId={AssocId}",
            taxYear, formName, effectiveCorpCode, branchLib, parentLib, associationId ?? "none");

        // branchLib is already resolved per-association from FCMCCRL2 (FMDLIB) by the caller.
        // Do NOT call GetAssociationLibraryAsync here — it queries FMRPT1, not FMDLIB.
        var libraryToUse = string.IsNullOrWhiteSpace(branchLib) ? "DATCLIQ" : branchLib;

        try
        {
            switch (formName.Trim().ToUpper())
            {
                case "1098":
                    results = await BuildForm1098Async(taxYear, effectiveCorpCode, libraryToUse, isCurrentYear, progress, 22, 55, parentLib);
                    break;

                case "1099-A":
                case "1099A":
                    results = await BuildForm1099AAsync(taxYear, effectiveCorpCode, libraryToUse, isCurrentYear, progress, 22, 55, parentLib);
                    break;

                case "1099-INT":
                    results = await BuildForm1099IntAsync(taxYear, effectiveCorpCode, libraryToUse, isCurrentYear, progress, 22, 55, parentLib);
                    break;

                case "1099-DIV":
                    results = await BuildForm1099DivAsync(taxYear, effectiveCorpCode, libraryToUse, isCurrentYear, progress, 22, 55, parentLib);
                    break;

                case "1099-PATR":
                    results = await BuildForm1099PatrAsync(taxYear, effectiveCorpCode, libraryToUse, isCurrentYear, progress, 22, 55, parentLib);
                    break;

                case "1099-MISC":
                    progress?.Report(35);
                    results = await _miscNecService.BuildMiscAndNecAsync(taxYear, effectiveCorpCode, "1099-MISC");
                    progress?.Report(55);
                    break;

                case "1099-NEC":
                    progress?.Report(35);
                    results = await _miscNecService.BuildMiscAndNecAsync(taxYear, effectiveCorpCode, "1099-NEC");
                    progress?.Report(55);
                    break;

                default:
                    _logger.LogWarning("Unsupported form type for TX9515 build: {FormName}", formName);
                    break;
            }

            _logger.LogInformation(
                "TX9515 BUILD completed for form {Form}, corp {Corp}, year {Year}. Records: {Count}",
                formName, effectiveCorpCode, taxYear, results.Count);
            progress?.Report(55);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during TX9515 build for form {Form}, year {Year}", formName, taxYear);
        }

        return results;
    }

    /// <summary>Build 1098 (Interest Paid) records from LNMASTR.</summary>
    private async Task<List<TaxDetailRecord>> BuildForm1098Async(
        int taxYear,
        string corpCode,
        string branchLib,
        bool isCurrentYear,
        IProgress<int>? progress,
        int progressStart,
        int progressEnd,
        string parentLib = "")
    {
        var results = new List<TaxDetailRecord>();
        var loanRecordsTask = _sourceService.QueryLoanMasterAsync(taxYear, branchLib, corpCode, isCurrentYear);
        var loanRecords = await AwaitWithProgressPulse(loanRecordsTask, progress, progressStart, Math.Min(progressStart + 8, progressEnd));
        ReportProgress(progress, Math.Min(progressStart + 8, progressEnd), progressEnd, 0, loanRecords.Count);

        for (var i = 0; i < loanRecords.Count; i++)
        {
            var loan = loanRecords[i];
            var record = await _transformService.TransformLoanMasterTo1098(taxYear, corpCode, loan, isCurrentYear, parentLib);

            if (record.IntPd + record.Points > 0)  // Only include if there's taxable interest
            {
                results.Add(record);
            }

            ReportProgress(progress, Math.Min(progressStart + 8, progressEnd), progressEnd, i + 1, loanRecords.Count);
        }

        return results;
    }

    /// <summary>Build 1099-INT (Interest Earned) records from DDMASTR.</summary>
    private async Task<List<TaxDetailRecord>> BuildForm1099IntAsync(
        int taxYear,
        string corpCode,
        string branchLib,
        bool isCurrentYear,
        IProgress<int>? progress,
        int progressStart,
        int progressEnd,
        string parentLib = "")
    {
        var results = new List<TaxDetailRecord>();
        var depositRecordsTask = _sourceService.QueryDepositMasterAsync(taxYear, branchLib, corpCode);
        var depositRecords = await AwaitWithProgressPulse(depositRecordsTask, progress, progressStart, Math.Min(progressStart + 8, progressEnd));
        ReportProgress(progress, Math.Min(progressStart + 8, progressEnd), progressEnd, 0, depositRecords.Count);

        for (var i = 0; i < depositRecords.Count; i++)
        {
            var deposit = depositRecords[i];
            var record = await _transformService.TransformDepositMasterTo1099Int(taxYear, corpCode, deposit, isCurrentYear, parentLib);

            // Only include if there's interest earned or withholding
            if (record.InterN > 0 || record.ErnWth > 0)
            {
                results.Add(record);
            }

            ReportProgress(progress, Math.Min(progressStart + 8, progressEnd), progressEnd, i + 1, depositRecords.Count);
        }

        return results;
    }

    /// <summary>Build 1099-A (Acquisition/Abandonment) records from LNMASTR.</summary>
    private async Task<List<TaxDetailRecord>> BuildForm1099AAsync(
        int taxYear,
        string corpCode,
        string branchLib,
        bool isCurrentYear,
        IProgress<int>? progress,
        int progressStart,
        int progressEnd,
        string parentLib = "")
    {
        var results = new List<TaxDetailRecord>();
        var loanRecordsTask = _sourceService.QueryLoanMasterAsync(taxYear, branchLib, corpCode, isCurrentYear);
        var loanRecords = await AwaitWithProgressPulse(loanRecordsTask, progress, progressStart, Math.Min(progressStart + 8, progressEnd));
        ReportProgress(progress, Math.Min(progressStart + 8, progressEnd), progressEnd, 0, loanRecords.Count);

        for (var i = 0; i < loanRecords.Count; i++)
        {
            var loan = loanRecords[i];
            var record = await _transformService.TransformLoanMasterTo1099A(taxYear, corpCode, loan, isCurrentYear, parentLib);

            // Keep staging permissive for 1099-A; downstream validation can flag incomplete records.
            if (record.UnpPrn > 0 || record.FmVal > 0 || !string.IsNullOrWhiteSpace(record.PrDesc))
            {
                results.Add(record);
            }

            ReportProgress(progress, Math.Min(progressStart + 8, progressEnd), progressEnd, i + 1, loanRecords.Count);
        }

        return results;
    }

    /// <summary>Build 1099-DIV (Dividends) records from SHCRCT/SHCRPR.</summary>
    private async Task<List<TaxDetailRecord>> BuildForm1099DivAsync(
        int taxYear,
        string corpCode,
        string branchLib,
        bool isCurrentYear,
        IProgress<int>? progress,
        int progressStart,
        int progressEnd,
        string parentLib = "")
    {
        var results = new List<TaxDetailRecord>();
        var crRecordsTask = _sourceService.QueryCapitalReductionsAsync(taxYear, branchLib, corpCode, "1099-DIV");
        var crRecords = await AwaitWithProgressPulse(crRecordsTask, progress, progressStart, Math.Min(progressStart + 8, progressEnd));
        ReportProgress(progress, Math.Min(progressStart + 8, progressEnd), progressEnd, 0, crRecords.Count);

        for (var i = 0; i < crRecords.Count; i++)
        {
            var cr = crRecords[i];
            var record = await _transformService.TransformCapitalReductionToForm(taxYear, corpCode, cr, "1099-DIV", parentLib);

            if (record.DivRcv > 0 || record.DivWth > 0)
            {
                results.Add(record);
            }

            ReportProgress(progress, Math.Min(progressStart + 8, progressEnd), progressEnd, i + 1, crRecords.Count);
        }

        return results;
    }

    /// <summary>Build 1099-PATR (Patronages) records from SHCRCT/SHCRPR or PAYAAS.</summary>
    private async Task<List<TaxDetailRecord>> BuildForm1099PatrAsync(
        int taxYear,
        string corpCode,
        string branchLib,
        bool isCurrentYear,
        IProgress<int>? progress,
        int progressStart,
        int progressEnd,
        string parentLib = "")
    {
        // Try capital reduction path first (SHCRCT/SHCRPR)
        var results = new List<TaxDetailRecord>();
        var crRecordsTask = _sourceService.QueryCapitalReductionsAsync(taxYear, branchLib, corpCode, "1099-PATR");
        var crRecords = await AwaitWithProgressPulse(crRecordsTask, progress, progressStart, Math.Min(progressStart + 8, progressEnd));
        ReportProgress(progress, Math.Min(progressStart + 8, progressEnd), progressEnd, 0, crRecords.Count);

        for (var i = 0; i < crRecords.Count; i++)
        {
            var cr = crRecords[i];
            var record = await _transformService.TransformCapitalReductionToForm(taxYear, corpCode, cr, "1099-PATR", parentLib);

            if (record.PatRef > 0 || record.PatWth > 0)
            {
                results.Add(record);
            }

            ReportProgress(progress, Math.Min(progressStart + 8, progressEnd), progressEnd, i + 1, crRecords.Count);
        }

        // Also add patronage from PAYAAS (patronage yearly accumulator)
        var patronageRecords = await _patronageService.BuildPatronageAsync(taxYear, corpCode);
        results.AddRange(patronageRecords);

        progress?.Report(progressEnd);

        return results;
    }

    private static void ReportProgress(IProgress<int>? progress, int start, int end, int processed, int total)
    {
        if (progress is null)
            return;

        if (total <= 0)
        {
            progress.Report(start);
            return;
        }

        var ratio = (double)processed / total;
        var value = start + (int)Math.Round((end - start) * ratio);
        progress.Report(Math.Clamp(value, start, end));
    }

    private static async Task<T> AwaitWithProgressPulse<T>(Task<T> pendingTask, IProgress<int>? progress, int start, int pulseEnd)
    {
        if (progress is null)
            return await pendingTask;

        var current = start;
        progress.Report(current);

        while (!pendingTask.IsCompleted)
        {
            await Task.WhenAny(pendingTask, Task.Delay(1000));
            if (!pendingTask.IsCompleted)
            {
                current = Math.Min(pulseEnd, current + 1);
                progress.Report(current);
            }
        }

        return await pendingTask;
    }
}
