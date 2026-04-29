using Tx9501.Models.Entities;
using Tx9501.Models.ViewModels;

namespace Tx9501.Services;

/// <summary>
/// Covers IBM i programs:
///   TX9500 – year-select/add/change (tx9500.rpgle)
///   TX9512 – audit year-select (tx9512.rpgle, read-only variant)
/// </summary>
public interface IYearSelectService
{
    /// <summary>List all TXRCTL records, newest first (matches subfile READP loop).</summary>
    Task<IList<TaxYearRow>> ListYearsAsync();

    /// <summary>Add a new TXRCTL record.</summary>
    Task AddYearAsync(int taxYear, string description);

    /// <summary>Update the description of an existing TXRCTL record.</summary>
    Task UpdateYearAsync(int taxYear, string description);
}

/// <summary>
/// Covers IBM i programs:
///   TX9505 – association select prompt (tx9505.rpgle)
///   TX9506 – pre-clear warning display (tx9506.rpgle)
/// </summary>
public interface IAssociationService
{
    /// <summary>Return all associations from FCMCCRL2 that the user is authorised to.</summary>
    Task<IList<AssociationRow>> GetAuthorisedAssociationsAsync(string userId);

    /// <summary>
    /// Return only the associations that are in the supplied association list.
    /// Used by TX9506 (pre-clear warning) to show which associations will be cleared.
    /// </summary>
    Task<IList<AssociationRow>> GetSelectedAssociationsAsync(IEnumerable<string> corpCodes);
}

/// <summary>
/// Covers IBM i programs:
///   TX9510 – clear tax data (tx9510.sqlrpgle)
/// </summary>
public interface IClearTaxDataService
{
    /// <summary>
    /// DELETE rows from TXRDTL and TXRAUD for the given year/form/associations.
    /// Mirrors the SQL DELETE statements in tx9510.
    /// </summary>
    Task ClearAsync(string taxYear, string formName, IEnumerable<string> associations, bool selectAll);
}

/// <summary>
/// Covers IBM i programs:
///   TX9515 – build/load tax records (tx9515.sqlrpgle)
///   TX9540 – build 1099-MISC/NEC from SmartStream (tx9540.sqlrpgle)
///   TX951501R / TX951502R / TX9517 – Patronage sub-processes
/// Current web implementation stages data by reading IBM i TXRDTL rows;
/// it does not issue direct ODBC program calls to TX9515/TX9540.
/// </summary>
public interface IBuildTaxDataService
{
    /// <summary>
    /// Stage build-source records for the given form into local SQLite.
    /// Source rows are read from IBM i TXRDTL (or local fallback if IBM i is unavailable).
    /// </summary>
    Task BuildAsync(string taxYear, string formName, IEnumerable<string> associations, bool selectAll);
}

/// <summary>
/// Covers IBM i programs:
///   TX9526 – validate / flag records in error (tx9526.rpgle)
/// </summary>
public interface IValidateTaxService
{
    /// <summary>
    /// Run validation against web-visible tax records and update the local ERRORS flag.
    /// Returns the count of records flagged in the web staging store.
    /// Progress is reported as percentage (0-100) via the optional progress parameter.
    /// </summary>
    Task<int> ValidateAsync(string taxYear, string formName, IEnumerable<string> associations, bool selectAll, IProgress<int>? progress = null);
}

/// <summary>
/// Covers IBM i programs:
///   TX9520 – display summary (tx9520.sqlrpgle)
/// </summary>
public interface ISummaryService
{
    /// <summary>Return per-association summary rows (count + amounts).</summary>
    Task<IList<SummaryRow>> GetSummaryAsync(string taxYear, string formName);
}

/// <summary>
/// Covers IBM i programs:
///   TX9525 – maintain tax records (tx9525.sqlrpgle)
/// </summary>
public interface IMaintainService
{
    /// <summary>Retrieve a single TXRDTL record.</summary>
    Task<TaxDetailRecord?> GetRecordAsync(string taxYear, string formName, string asa, decimal mbrNo, string? mbrSub);

    /// <summary>Add a new TXRDTL record and write audit row.</summary>
    Task AddRecordAsync(TaxDetailRecord record);

    /// <summary>Update an existing TXRDTL record and write audit row.</summary>
    Task UpdateRecordAsync(TaxDetailRecord record);

    /// <summary>Delete a staged SQLite TXRDTL record by key. Returns true when a local row is deleted.</summary>
    Task<bool> DeleteRecordAsync(TaxDetailRecord record);

    /// <summary>Return all error-flagged records for review (F10 function).</summary>
    Task<IList<TaxDetailRecord>> GetErrorRecordsAsync(string taxYear, string formName, string asa);

    /// <summary>Return a paged error list, preferring staged SQLite rows when available.</summary>
    Task<PagedResult<TaxDetailRecord>> GetErrorRecordsPageAsync(string taxYear, string formName, string asa, int pageNumber, int pageSize);
}

/// <summary>
/// Covers IBM i programs:
///   TX9530 – IRS detail report (tx9530.rpgle)
///   TX9531 – IRS exclusion report (tx9531.rpgle)
///   TX9532 – IRS error report (tx9532.rpgle)
///   TX9534 – 1099-MISC/NEC detail report (tx9534.rpgle)
///   TX9591R – Non-1098 interest letters (tx9591r.rpgle)
/// </summary>
public interface IReportService
{
    /// <summary>Submit the detail report job on IBM i (calls TX9530 / TX9534).</summary>
    Task PrintDetailReportAsync(string taxYear, string formName, IEnumerable<string> associations, bool selectAll);

    /// <summary>Return detail-report rows for the web UI, preferring IBM i data and overlaying local edits.</summary>
    Task<IList<TaxDetailRecord>> GetDetailReportAsync(string taxYear, string formName, IEnumerable<string> associations, bool selectAll);

    /// <summary>Return a paged report list, using staged SQLite rows first and IBM i only when staged rows do not exist.</summary>
    Task<PagedResult<TaxDetailRecord>> GetDetailReportPageAsync(string taxYear, string formName, IEnumerable<string> associations, bool selectAll, int pageNumber, int pageSize, TaxDetailListMode mode);

    /// <summary>Return the distinct association codes present in the full (unpaged) result set for the given mode.</summary>
    Task<IList<string>> GetDistinctAssociationsAsync(string taxYear, string formName, IList<string> associations, bool selectAll, TaxDetailListMode mode);

    /// <summary>Return distinct association codes that have error-flagged records.</summary>
    Task<IList<string>> GetDistinctAssociationsWithErrorsAsync(string taxYear, string formName);

    /// <summary>Submit the exclusion report job (calls TX9531).</summary>
    Task PrintExclusionReportAsync(string taxYear, string formName, IEnumerable<string> associations, bool selectAll);

    /// <summary>Submit the error report job (calls TX9532).</summary>
    Task PrintErrorReportAsync(string taxYear, string formName, IEnumerable<string> associations, bool selectAll);

    /// <summary>Print non-1098 interest letters (calls TX9591R).</summary>
    Task PrintLettersAsync(string taxYear, IEnumerable<string> associations, bool selectAll);
}

/// <summary>
/// Covers IBM i programs:
///   TX9560 – work with extracts (tx9560.sqlrpgle)
///   TX9561 – clear extract (tx9561.sqlrpgle)
///   TX9562 – extract setup / form selection (tx9562.rpgle)
///   TX9563 – build IRS file (tx9563.rpgle)
///   TX9565R – confirm transmit (tx9565r.rpgle)
/// </summary>
public interface IExtractService
{
    /// <summary>List all extract control records for the year (TXIRST).</summary>
    Task<IList<ExtractControlRecord>> ListExtractsAsync(string taxYear);

    /// <summary>Create a new extract definition row in TXIRST.</summary>
    Task<long> CreateExtractAsync(string taxYear, string description, string selectDate,
                                    string xmtrName, string xmtrName2);

    /// <summary>Clear an extract (TX9561 – deletes TXIRSA/B/C/F/X records + clears TXIRSB).</summary>
    Task ClearExtractAsync(string taxYear, long extSeq);

    /// <summary>Force-delete an extract from IBM i TXIRST/TXIRSB tables directly via SQL.</summary>
    Task ForceDeleteExtractFromIBMiAsync(string taxYear, long extSeq);

    /// <summary>Clear all local extract records from SQLite database.</summary>
    Task ClearAllLocalExtractsAsync();

    /// <summary>Delete a specific local extract record from SQLite database only.</summary>
    Task DeleteLocalExtractAsync(string taxYear, long extSeq);

    /// <summary>Build the IRS file (TX9562 / TX9563) for the given extract sequence.</summary>
    Task BuildIrsFileAsync(string taxYear, long extSeq, IEnumerable<string> forms,
                           IEnumerable<string> associations);

    /// <summary>Transmit the extract file to the print vendor (TX9565R / BB1220). Returns true if file exists and transmit was attempted, false if file not found.</summary>
    Task<bool> TransmitExtractAsync(string taxYear, long extSeq);

    /// <summary>Return the record count for a completed extract.</summary>
    Task<long> GetExtractRecordCountAsync(string taxYear, long extSeq);

    /// <summary>Return generated IRS extract file content for download when available.</summary>
    Task<(string FileName, byte[] Content)?> DownloadExtractAsync(string taxYear, long extSeq);

    /// <summary>Get available associations for the tax year that have tax detail records.</summary>
    Task<IList<AssociationRow>> GetAvailableAssociationsAsync(string taxYear);
}
