using System.Data.Odbc;
using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;
using Tx9501.Data;
using Tx9501.Models.Entities;
using Tx9501.Models.ViewModels;

namespace Tx9501.Services;

/// <summary>
/// Implements IYearSelectService.
/// Replaces tx9500.rpgle and tx9512.rpgle.
/// Reads/writes IBM i TXRCTL via ODBC.
/// </summary>
public sealed class YearSelectService : IYearSelectService
{
    private readonly string _cs;
    private readonly string _lib;
    private readonly LocalDbContext _db;
    private readonly ILogger<YearSelectService> _logger;

    public YearSelectService(IConfiguration cfg, LocalDbContext db,
                              ILogger<YearSelectService> logger)
    {
        _cs     = cfg.GetConnectionString("IBMi")!;
        _lib    = cfg["IBMiSettings:Library"] ?? "TXLIB";
        _db     = db;
        _logger = logger;
    }

    public async Task<IList<TaxYearRow>> ListYearsAsync()
    {
        const string sql = "SELECT TAXYR, TAXDES, TAXSTA FROM {0}/TXRCTL ORDER BY TAXYR DESC";
        var rows = new List<TaxYearRow>();
        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            await using var cmd = new OdbcCommand(string.Format(sql, _lib), conn);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                rows.Add(new TaxYearRow
                {
                    TaxYear     = rdr.GetInt32(0),
                    Description = rdr.GetString(1).Trim(),
                    Status      = rdr.GetString(2).Trim()
                });
        }
        catch
        {
        }

        // Merge SQLite local years (skip years already returned by IBM i)
        var ibmiYears = rows.Select(r => r.TaxYear).ToHashSet();
        var localRows = await _db.TaxYears
            .OrderByDescending(x => x.TaxYear)
            .ToListAsync();
        foreach (var lr in localRows)
        {
            if (!ibmiYears.Contains(lr.TaxYear))
                rows.Add(lr.ToRow());
        }

        return rows.OrderByDescending(r => r.TaxYear).ToList();
    }

    public async Task AddYearAsync(int taxYear, string description)
    {
        // New years are staged locally in SQLite; sync to IBM i separately.
        _db.TaxYears.Add(new LocalTaxYear
        {
            TaxYear     = taxYear,
            Description = description,
            Status      = "IN PROCESS",
            CreatedAt   = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    public async Task UpdateYearAsync(int taxYear, string description)
    {
        // If the year is a local record, update SQLite; otherwise update IBM i.
        var local = await _db.TaxYears.FirstOrDefaultAsync(y => y.TaxYear == taxYear);
        if (local is not null)
        {
            local.Description = description;
            await _db.SaveChangesAsync();
            return;
        }

        const string sql = "UPDATE {0}/TXRCTL SET TAXDES = ? WHERE TAXYR = ?";
        await ExecuteNonQueryAsync(string.Format(sql, _lib),
            new[] { description, taxYear.ToString() });
    }

    private async Task ExecuteNonQueryAsync(string sql, string[] parms)
    {
        await using var conn = new OdbcConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new OdbcCommand(sql, conn);
        foreach (var p in parms)
            cmd.Parameters.AddWithValue("?", p);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Implements IAssociationService.
/// Replaces tx9505.rpgle (select) and tx9506.rpgle (pre-clear warning).
/// Reads IBM i FCMCCRL2 via ODBC (no BKGLIB authorization check).
/// </summary>
public sealed class AssociationService : IAssociationService
{
    private readonly string _cs;
    private readonly string _lib;
    private readonly ILogger<AssociationService> _logger;

    public AssociationService(IConfiguration cfg, ILogger<AssociationService> logger)
    {
        _cs     = cfg.GetConnectionString("IBMi")!;
        _lib    = cfg["IBMiSettings:Library"] ?? "TXLIB";
        _logger = logger;
    }

    public async Task<IList<AssociationRow>> GetAuthorisedAssociationsAsync(string userId)
    {
        var sql = $"SELECT FMBNUM, FMDLIB, FMGLPC, FMDSC, FMASTY, FMPALB FROM {_lib}/FCMCCRL2 ORDER BY FMGLPC";
        return await ReadAssocRowsAsync(sql, null);
    }

    public async Task<IList<AssociationRow>> GetSelectedAssociationsAsync(IEnumerable<string> corpCodes)
    {
        var codes = corpCodes.Select(c => $"'{c.Replace("'", "''")}'").ToList();
        if (!codes.Any()) return new List<AssociationRow>();

        var inList = string.Join(",", codes);
        var sql    = $"SELECT FMBNUM, FMDLIB, FMGLPC, FMDSC, FMASTY, FMPALB " +
                     $"FROM {_lib}/FCMCCRL2 WHERE FMGLPC IN ({inList}) ORDER BY FMGLPC";

        return await ReadAssocRowsAsync(sql, null);
    }

    private async Task<IList<AssociationRow>> ReadAssocRowsAsync(string sql, string? parm)
    {
        var rows = new List<AssociationRow>();
        await using var conn = new OdbcConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new OdbcCommand(sql, conn);
        if (parm is not null)
            cmd.Parameters.AddWithValue("?", parm);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
            rows.Add(new AssociationRow
            {
                BranchNum   = rdr.GetDecimal(0),
                BranchLib   = rdr.GetString(1).Trim(),
                CorpCode    = rdr.GetString(2).Trim(),
                Description = rdr.GetString(3).Trim(),
                AssnType    = rdr.GetString(4).Trim(),
                ParentLib   = rdr.GetString(5).Trim()
            });
        return rows;
    }
}

/// <summary>
/// Implements IClearTaxDataService.
/// Replaces tx9510.sqlrpgle.
/// </summary>
public sealed class ClearTaxDataService : IClearTaxDataService
{
    private readonly IIBMiService _ibmi;
    private readonly string _cs;
    private readonly string _lib;
    private readonly LocalDbContext _db;
    private readonly ILogger<ClearTaxDataService> _logger;

    public ClearTaxDataService(IIBMiService ibmi, IConfiguration cfg,
                                LocalDbContext db, ILogger<ClearTaxDataService> logger)
    {
        _ibmi   = ibmi;
        _cs     = cfg.GetConnectionString("IBMi")!;
        _lib    = cfg["IBMiSettings:Library"] ?? "TXLIB";
        _db     = db;
        _logger = logger;
    }

    public async Task<int> ClearAsync(string taxYear, string formName,
                                      IEnumerable<string> associations, bool selectAll)
    {
        var asns = associations.ToList();

        var localDeletedRows = await ClearLocalAsync(taxYear, formName, asns, selectAll);

        // Web clear is local-first: only delete from SQLite staging tables.
        // Do not fail CLEAR on IBM i authorization constraints.
        return localDeletedRows;
    }

    private async Task<int> ClearLocalAsync(string taxYear, string formName, IList<string> associations, bool selectAll)
    {
        var normalizedTaxYear = taxYear.Trim();
        var normalizedForm = formName.Trim();
        var query = _db.TaxDetails.Where(d => d.TaxYear == normalizedTaxYear && d.Form == normalizedForm);
        var auditQuery = _db.TaxAudits.Where(d => d.TaxYear == normalizedTaxYear && d.Form == normalizedForm);

        if (!selectAll && associations.Count > 0)
        {
            query = query.Where(d => associations.Contains(d.Asa));
            auditQuery = auditQuery.Where(d => associations.Contains(d.Asa));
        }

        var localTaxDetails = await query.ToListAsync();
        var localTaxAudits = await auditQuery.ToListAsync();
        _db.TaxDetails.RemoveRange(localTaxDetails);
        _db.TaxAudits.RemoveRange(localTaxAudits);
        await _db.SaveChangesAsync();

        return localTaxDetails.Count + localTaxAudits.Count;
    }

    private static async Task DeleteAsync(OdbcConnection conn, string sql, params string[] parms)
    {
        await using var cmd = new OdbcCommand(sql, conn);
        foreach (var p in parms) cmd.Parameters.AddWithValue("?", p);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<IList<string>> GetAllAsnsAsync(OdbcConnection conn)
    {
        var list = new List<string>();
        await using var cmd = new OdbcCommand(
            $"SELECT FMGLPC FROM {_lib}/FCMCCRL2 ORDER BY FMGLPC", conn);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync()) list.Add(rdr.GetString(0).Trim());
        return list;
    }
}

/// <summary>
/// Implements IBuildTaxDataService.
/// Stages build-source records from IBM i/local tables for web processing.
/// IBM i TX9515/TX9540 remain upstream producers of TXRDTL/TXSSAP data.
/// </summary>
public sealed class BuildTaxDataService : IBuildTaxDataService
{
    private readonly string _cs;
    private readonly string _lib;
    private readonly LocalDbContext _db;
    private readonly IDbContextFactory<LocalDbContext> _dbFactory;
    private readonly IConfiguration _cfg;
    private readonly TX9515BuildService _tx9515Build;
    private readonly TX9526ValidationService _validationService;
    private readonly IAssociationService _assocService;
    private readonly ILogger<BuildTaxDataService> _logger;

    public BuildTaxDataService(IConfiguration cfg, LocalDbContext db,
                               IDbContextFactory<LocalDbContext> dbFactory,
                               TX9515BuildService tx9515Build,
                               TX9526ValidationService validationService,
                               IAssociationService assocService,
                               ILogger<BuildTaxDataService> logger)
    {
        _cs                = cfg.GetConnectionString("IBMi")!;
        _lib               = cfg["IBMiSettings:Library"] ?? "TXLIB";
        _db                = db;
        _dbFactory         = dbFactory;
        _cfg               = cfg;
        _tx9515Build       = tx9515Build;
        _validationService = validationService;
        _assocService      = assocService;
        _logger            = logger;
    }

    public async Task<int> BuildAsync(string taxYear, string formName,
                                  IEnumerable<string> associations, bool selectAll,
                                  IProgress<int>? progress = null)
    {
        progress?.Report(5);

        var assocList = associations
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!int.TryParse(taxYear, out var taxYearInt))
        {
            _logger.LogError("Invalid tax year provided: {TaxYear}", taxYear);
            return 0;
        }

        var buildAllModeByAssociation = _cfg.GetValue<bool?>("TaxSettings:BuildAllModeByAssociation") ?? true;
        if (selectAll && assocList.Count == 0 && buildAllModeByAssociation)
        {
            assocList = await GetAllAssociationCodesAsync();
            _logger.LogInformation(
                "ALL mode resolved {AssocCount} associations for per-association build. TaxYear={TaxYear}, Form={FormName}",
                assocList.Count,
                taxYear,
                formName);
        }

        progress?.Report(10);

        var sourceRows = new List<TaxDetailRecord>();
        progress?.Report(15);

        await ClearLocalBuildScopeAsync(taxYear, formName, assocList, selectAll);
        progress?.Report(20);

        // Get corp code and branch lib from configuration (fallback defaults)
        var corpCode = _cfg["TaxSettings:CorpCode"] ?? "001";
        var defaultBranchLib = _cfg["IBMiSettings:Library"] ?? "TXLIB";
        var isCurrentYear = DateTime.Now.Year == taxYearInt;

        // Resolve full AssociationRow metadata (FMDLIB, FMPALB) for per-association builds.
        // This replaces the old GetAssociationLibraryAsync (which queried FMRPT1, not FMDLIB).
        IList<AssociationRow> assocRowLookup = new List<AssociationRow>();
        if (assocList.Count > 0)
        {
            try
            {
                assocRowLookup = await _assocService.GetSelectedAssociationsAsync(assocList);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve AssociationRow metadata; build will use default library");
            }
        }

        _logger.LogInformation(
            "BuildAsync starting: TaxYear={TaxYear}, Form={FormName}, CorpCode={CorpCode}, DefaultBranchLib={BranchLib}, SelectAll={SelectAll}, AssocCount={AssocCount}, Associations={Associations}",
            taxYear, formName, corpCode, defaultBranchLib, selectAll, assocList.Count, string.Join(",", assocList));

        try
        {
            // Process one association at a time to provide frequent progress updates
            // and avoid a single long-running batch.
            // In ALL mode this behavior can be toggled via TaxSettings:BuildAllModeByAssociation.
            var processByAssociation = assocList.Count > 0 && (!selectAll || buildAllModeByAssociation);
            if (processByAssociation)
            {
                sourceRows = new List<TaxDetailRecord>();
                var assocCount = assocList.Count;

                for (var i = 0; i < assocCount; i++)
                {
                    var assoc = assocList[i];
                    var rangeStart = 20 + (int)Math.Floor(40.0 * i / assocCount);
                    var rangeEnd = 20 + (int)Math.Floor(40.0 * (i + 1) / assocCount);

                    var assocProgress = new Progress<int>(p =>
                    {
                        var normalized = Math.Clamp((p - 20) / 35.0, 0.0, 1.0);
                        var mapped = rangeStart + (int)Math.Round((rangeEnd - rangeStart) * normalized);
                        progress?.Report(Math.Clamp(mapped, rangeStart, rangeEnd));
                    });

                    // Resolve per-association FMDLIB and FMPALB from FCMCCRL2.
                    var assocRow = assocRowLookup.FirstOrDefault(r =>
                        string.Equals(r.CorpCode, assoc, StringComparison.OrdinalIgnoreCase));
                    var assocBranchLib = assocRow?.BranchLib ?? defaultBranchLib;
                    var assocParentLib = assocRow?.ParentLib ?? "";

                    _logger.LogInformation(
                        "Building association {Association} ({Index}/{Total}) TaxYear={TaxYear}, Form={FormName}, FMDLIB={BranchLib}, FMPALB={ParentLib}",
                        assoc, i + 1, assocCount, taxYear, formName, assocBranchLib, assocParentLib);

                    var assocRows = await _tx9515Build.BuildTaxDetailsAsync(
                        taxYearInt,
                        formName.Trim(),
                        corpCode,
                        assocBranchLib,
                        isCurrentYear,
                        assocProgress,
                        assoc,
                        assocParentLib);

                    sourceRows.AddRange(assocRows);
                }
            }
            else
            {
                // Use TX9515BuildService to build records from source files instead of reading pre-built TXRDTL
                sourceRows = await _tx9515Build.BuildTaxDetailsAsync(
                    taxYearInt,
                    formName.Trim(),
                    corpCode,
                    defaultBranchLib,
                    isCurrentYear,
                    progress);
            }
            progress?.Report(60);

            _logger.LogInformation(
                "TX9515 BUILD service completed for year {TaxYear}, form {FormName}. Records built: {Count}",
                taxYear, formName, sourceRows.Count);

            // Filter by associations only when caller explicitly selected a subset.
            if (!selectAll && assocList.Count > 0)
            {
                sourceRows = sourceRows
                    .Where(r => assocList.Contains(r.Asa))
                    .ToList();
            }
            progress?.Report(65);

            // Refresh staging rows with the latest TXRDTL values when available.
            // This keeps SQLite aligned with IBM i updates used for error checks and reports.
            try
            {
                var txrdtlRows = await QueryBuildSourceFromIbmiAsync(taxYear, formName, assocList, selectAll);
                if (txrdtlRows.Count > 0)
                {
                    var sourceCount = sourceRows.Count;
                    sourceRows = MergeBuildRowsPreferTxrdtl(sourceRows, txrdtlRows);
                    _logger.LogInformation(
                        "Merged latest TXRDTL values into build rows for year {TaxYear}, form {FormName}. Source={SourceCount}, TXRDTL={TxrdtlCount}, Merged={MergedCount}",
                        taxYear, formName, sourceCount, txrdtlRows.Count, sourceRows.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Unable to refresh from TXRDTL during build for year {TaxYear}, form {FormName}; using build-source rows.",
                    taxYear, formName);
            }

                    progress?.Report(75);

            // Some form/source paths may return zero rows without throwing.
            // In that case, try the legacy pre-built TXRDTL source before declaring no data.
            if (sourceRows.Count == 0)
            {
                _logger.LogWarning(
                    "TX9515 BUILD returned 0 rows for year {TaxYear}, form {FormName}; attempting fallback to pre-built TXRDTL.",
                    taxYear, formName);

                try
                {
                    sourceRows = await QueryBuildSourceFromIbmiAsync(taxYear, formName, assocList, selectAll);
                    _logger.LogInformation(
                        "Zero-row fallback to TXRDTL completed for year {TaxYear}, form {FormName}. Rows: {Count}",
                        taxYear, formName, sourceRows.Count);
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogWarning(fallbackEx,
                        "TXRDTL fallback after zero-row primary build failed; using local staging data if available");
                    sourceRows = await QueryBuildSourceFromLocalAsync(taxYear, formName, assocList, selectAll);
                }
            }

            await RemoveStaleLocalTaxDetailsAsync(taxYear, formName, assocList, selectAll, sourceRows);
            progress?.Report(80);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "TX9515 BUILD failed for year {TaxYear}, form {FormName}; attempting fallback to pre-built TXRDTL",
                taxYear, formName);

            // Fallback: Try to read pre-built TXRDTL from IBM i if TX9515 build fails
            try
            {
                sourceRows = await QueryBuildSourceFromIbmiAsync(taxYear, formName, assocList, selectAll);
                _logger.LogWarning("Fallback to pre-built TXRDTL successful for year {TaxYear}, form {FormName}", taxYear, formName);
                await RemoveStaleLocalTaxDetailsAsync(taxYear, formName, assocList, selectAll, sourceRows);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogWarning(fallbackEx,
                    "TXRDTL fallback also failed; using local staging data if available");
                sourceRows = await QueryBuildSourceFromLocalAsync(taxYear, formName, assocList, selectAll);
            }
        }

        // Run per-association upserts in parallel using one DbContext per worker.
        await BatchUpsertLocalTaxDetailsByAssociationAsync(sourceRows);
        progress?.Report(90);

        _logger.LogInformation("Web-side BUILD completed for year {TaxYear}, form {FormName}. Rows staged: {Count}",
            taxYear, formName, sourceRows.Count);

        // Run TX9526 validation (equivalent to calling TX9526 in IBM i)
        try
        {
            var validationAssocs = selectAll ? Enumerable.Empty<string>() : assocList;
            var recordsWithErrors = await _validationService.ValidateRecordsAsync(
                taxYearInt,
                formName.Trim(),
                validationAssocs);

            _logger.LogInformation(
                "TX9526 Validation completed for year {TaxYear}, form {FormName}. Records flagged with errors: {Count}",
                taxYear, formName, recordsWithErrors);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TX9526 Validation encountered an error for year {TaxYear}, form {FormName}. Records may need manual review.",
                taxYear, formName);
        }

        progress?.Report(100);

        return sourceRows.Count;
    }

    private async Task ClearLocalBuildScopeAsync(
        string taxYear,
        string formName,
        IList<string> associations,
        bool selectAll)
    {
        var normalizedTaxYear = NormalizeBuildKey(taxYear);
        var normalizedForm = NormalizeBuildKey(formName);

        var detailsQuery = _db.TaxDetails
            .Where(d => d.TaxYear == normalizedTaxYear && d.Form == normalizedForm);

        var auditsQuery = _db.TaxAudits
            .Where(a => a.TaxYear == normalizedTaxYear && a.Form == normalizedForm);

        if (!selectAll && associations.Count > 0)
        {
            detailsQuery = detailsQuery.Where(d => associations.Contains(d.Asa));
            auditsQuery = auditsQuery.Where(a => associations.Contains(a.Asa));
        }

        var detailsToDelete = await detailsQuery.ToListAsync();
        var auditsToDelete = await auditsQuery.ToListAsync();

        if (detailsToDelete.Count == 0 && auditsToDelete.Count == 0)
        {
            _logger.LogInformation(
                "Pre-build local cleanup found no rows to clear for year {TaxYear}, form {FormName}, SelectAll={SelectAll}.",
                taxYear,
                formName,
                selectAll);
            return;
        }

        if (detailsToDelete.Count > 0)
        {
            _db.TaxDetails.RemoveRange(detailsToDelete);
        }

        if (auditsToDelete.Count > 0)
        {
            _db.TaxAudits.RemoveRange(auditsToDelete);
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Pre-build local cleanup removed {DetailCount} TaxDetails and {AuditCount} TaxAudits rows for year {TaxYear}, form {FormName}, SelectAll={SelectAll}.",
            detailsToDelete.Count,
            auditsToDelete.Count,
            taxYear,
            formName,
            selectAll);
    }

    private async Task RemoveStaleLocalTaxDetailsAsync(
        string taxYear,
        string formName,
        IList<string> associations,
        bool selectAll,
        List<TaxDetailRecord> sourceRows)
    {
        var normalizedTaxYear = NormalizeBuildKey(taxYear);
        var normalizedForm = NormalizeBuildKey(formName);

        var scopedQuery = _db.TaxDetails
            .Where(d => d.TaxYear == normalizedTaxYear && d.Form == normalizedForm);

        if (!selectAll && associations.Count > 0)
        {
            scopedQuery = scopedQuery.Where(d => associations.Contains(d.Asa));
        }

        var existingScopedRows = await scopedQuery.ToListAsync();

        if (existingScopedRows.Count == 0)
        {
            return;
        }

        var sourceKeySet = sourceRows
            .Select(r =>
            {
                var normalized = NormalizeRecordForLocal(r);
                return (normalized.TaxYear, normalized.Form, normalized.Asa, normalized.MbrNo, normalized.MbrSub);
            })
            .ToHashSet();

        var staleRows = existingScopedRows
            .Where(d => !sourceKeySet.Contains((d.TaxYear, d.Form, d.Asa, d.MbrNo, d.MbrSub)))
            .ToList();

        if (staleRows.Count == 0)
        {
            return;
        }

        _db.TaxDetails.RemoveRange(staleRows);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "BUILD true-refresh removed {Count} stale local rows for year {TaxYear}, form {FormName}.",
            staleRows.Count,
            taxYear,
            formName);
    }

    public Task<List<TaxDetailRecord>> GetIbmiTxrdtlRowsAsync(string taxYear, string formName, IEnumerable<string> associations, bool selectAll, int maxRows = 0)
        => QueryBuildSourceFromIbmiAsync(taxYear, formName, associations.ToList(), selectAll, maxRows);

    private async Task<List<TaxDetailRecord>> QueryBuildSourceFromIbmiAsync(
        string taxYear, string formName, IList<string> associations, bool selectAll, int maxRows = 0)
    {
        if (!int.TryParse(taxYear, out var taxYearInt))
        {
            return new List<TaxDetailRecord>();
        }

        var sql = new System.Text.StringBuilder($@"SELECT TAXYR,FORM,ASA,MBRNO,MBRSUB,SSIDN,BNM,BAD,BADX,BCTY,BST,BZP,
                            SSIDC,INTPD,POINTS,INTERN,ERNWTH,COMPEN,RENTS,MEDPAY,LGLPAY,OTHER,
                            WTHHELD,ERRORS,RPT_TO_IRS,NONRPT_RSN,CORRIN,ORIGDATE,SECSAME,SECADDR,SECDESC,
                            SECOTHER,SECNUM,MTGACQDT,UNPPRN,FMVAL,DTEAQR,PRDESC,FOREGN,DEPT,CHANGE_DT
                     FROM {_lib}/TXRDTL
                     WHERE TAXYR=? AND FORM=?");

        if (!selectAll && associations.Count > 0)
        {
            sql.Append(" AND ASA IN (");
            sql.Append(string.Join(",", associations.Select(_ => "?")));
            sql.Append(")");
        }

        sql.Append(" ORDER BY ASA, MBRNO, MBRSUB");

        if (maxRows > 0)
            sql.Append($" FETCH FIRST {maxRows} ROWS ONLY");

        var rows = new List<TaxDetailRecord>();
        await using var conn = new OdbcConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new OdbcCommand(sql.ToString(), conn);
        cmd.Parameters.AddWithValue("?", taxYearInt);
        cmd.Parameters.AddWithValue("?", formName.Trim().PadRight(9));
        foreach (var assoc in associations)
        {
            cmd.Parameters.AddWithValue("?", assoc.PadRight(3));
        }

        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            rows.Add(MapBuildRecord(rdr));
        }

        return rows;
    }

    private async Task<List<string>> GetAllAssociationCodesAsync()
    {
        var codes = new List<string>();

        try
        {
            var sql = $"SELECT DISTINCT FMGLPC FROM {_lib}/FCMCCRL2 WHERE FMGLPC IS NOT NULL AND TRIM(FMGLPC) <> '' ORDER BY FMGLPC";
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            await using var cmd = new OdbcCommand(sql, conn);
            await using var rdr = await cmd.ExecuteReaderAsync();

            while (await rdr.ReadAsync())
            {
                var code = rdr.IsDBNull(0) ? string.Empty : rdr.GetString(0).Trim();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    codes.Add(code);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Unable to resolve association list for ALL mode; falling back to bulk build path.");
        }

        return codes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<TaxDetailRecord>> QueryBuildSourceFromLocalAsync(
        string taxYear, string formName, IList<string> associations, bool selectAll)
    {
        var normalizedTaxYear = NormalizeBuildKey(taxYear);
        var normalizedForm = NormalizeBuildKey(formName);
        var query = _db.TaxDetails.Where(d => d.TaxYear == normalizedTaxYear && d.Form == normalizedForm);

        if (!selectAll && associations.Count > 0)
        {
            query = query.Where(d => associations.Contains(d.Asa));
        }

        return await query
            .Select(d => d.ToRecord())
            .ToListAsync();
    }

    private async Task BatchUpsertLocalTaxDetailsAsync(List<TaxDetailRecord> records)
    {
        if (records.Count == 0)
            return;

        // Normalize all records upfront
        var normalizedRecords = records
            .Select(r => NormalizeRecordForLocal(r))
            .ToList();

        // Build in-memory composite keys and use EF-translatable pre-filters.
        var recordKeySet = normalizedRecords
            .Select(r => (r.TaxYear, r.Form, r.Asa, r.MbrNo, r.MbrSub))
            .ToHashSet();

        var taxYears = normalizedRecords
            .Select(r => r.TaxYear)
            .Distinct()
            .ToList();
        var forms = normalizedRecords
            .Select(r => r.Form)
            .Distinct()
            .ToList();
        var asns = normalizedRecords
            .Select(r => r.Asa)
            .Distinct()
            .ToList();

        var existingCandidates = await _db.TaxDetails
            .Where(d =>
                taxYears.Contains(d.TaxYear) &&
                forms.Contains(d.Form) &&
                asns.Contains(d.Asa))
            .ToListAsync();

        var existingRecords = existingCandidates
            .Where(d => recordKeySet.Contains((d.TaxYear, d.Form, d.Asa, d.MbrNo, d.MbrSub)))
            .ToList();

        // Create a lookup for existing records
        var existingMap = existingRecords
            .ToDictionary(r => (r.TaxYear, r.Form, r.Asa, r.MbrNo, r.MbrSub), r => r);

        // Process all records: add new ones, update existing ones
        foreach (var normalized in normalizedRecords)
        {
            var key = (normalized.TaxYear, normalized.Form, normalized.Asa, normalized.MbrNo, normalized.MbrSub);
            
            if (existingMap.TryGetValue(key, out var existing))
            {
                // Update existing record
                normalized.Id = existing.Id;
                normalized.CreatedAt = existing.CreatedAt;
                _db.Entry(existing).CurrentValues.SetValues(normalized);
            }
            else
            {
                // Add new record
                _db.TaxDetails.Add(normalized);
            }
        }

        // Save all changes at once
        await _db.SaveChangesAsync();
    }

    private async Task BatchUpsertLocalTaxDetailsByAssociationAsync(List<TaxDetailRecord> records)
    {
        if (records.Count == 0)
            return;

        var groups = records
            .GroupBy(r => NormalizeBuildKey(r.Asa), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var configuredParallelism = _cfg.GetValue<int?>("TaxSettings:BuildParallelism") ?? 4;
        var maxParallelism = Math.Max(1, configuredParallelism);
        using var semaphore = new SemaphoreSlim(maxParallelism, maxParallelism);

        var tasks = groups.Select(async group =>
        {
            await semaphore.WaitAsync();
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                await UpsertAssociationBatchAsync(db, group.ToList());
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private static async Task UpsertAssociationBatchAsync(LocalDbContext db, List<TaxDetailRecord> records)
    {
        if (records.Count == 0)
            return;

        var normalizedRecords = records
            .Select(NormalizeRecordForLocal)
            .ToList();

        var recordKeySet = normalizedRecords
            .Select(r => (r.TaxYear, r.Form, r.Asa, r.MbrNo, r.MbrSub))
            .ToHashSet();

        var taxYears = normalizedRecords
            .Select(r => r.TaxYear)
            .Distinct()
            .ToList();
        var forms = normalizedRecords
            .Select(r => r.Form)
            .Distinct()
            .ToList();
        var asns = normalizedRecords
            .Select(r => r.Asa)
            .Distinct()
            .ToList();

        var existingCandidates = await db.TaxDetails
            .Where(d =>
                taxYears.Contains(d.TaxYear) &&
                forms.Contains(d.Form) &&
                asns.Contains(d.Asa))
            .ToListAsync();

        var existingRecords = existingCandidates
            .Where(d => recordKeySet.Contains((d.TaxYear, d.Form, d.Asa, d.MbrNo, d.MbrSub)))
            .ToList();

        var existingMap = existingRecords
            .ToDictionary(r => (r.TaxYear, r.Form, r.Asa, r.MbrNo, r.MbrSub), r => r);

        foreach (var normalized in normalizedRecords)
        {
            var key = (normalized.TaxYear, normalized.Form, normalized.Asa, normalized.MbrNo, normalized.MbrSub);

            if (existingMap.TryGetValue(key, out var existing))
            {
                normalized.Id = existing.Id;
                normalized.CreatedAt = existing.CreatedAt;
                db.Entry(existing).CurrentValues.SetValues(normalized);
            }
            else
            {
                db.TaxDetails.Add(normalized);
            }
        }

        await db.SaveChangesAsync();
    }

    private async Task UpsertLocalTaxDetailAsync(TaxDetailRecord record)
    {
        var normalized = NormalizeRecordForLocal(record);
        var existing = await _db.TaxDetails.FirstOrDefaultAsync(d =>
            d.TaxYear == normalized.TaxYear &&
            d.Form == normalized.Form &&
            d.Asa == normalized.Asa &&
            d.MbrNo == normalized.MbrNo &&
            d.MbrSub == normalized.MbrSub);

        if (existing is null)
        {
            _db.TaxDetails.Add(normalized);
        }
        else
        {
            normalized.Id = existing.Id;
            normalized.CreatedAt = existing.CreatedAt;
            normalized.UpdatedAt = DateTime.UtcNow;
            _db.Entry(existing).CurrentValues.SetValues(normalized);
        }

        await _db.SaveChangesAsync();
    }

    private static TaxDetailRecord MapBuildRecord(System.Data.Common.DbDataReader r) => new()
    {
        TaxYear      = SafeBuildString(r, 0),
        Form         = SafeBuildString(r, 1),
        Asa          = SafeBuildString(r, 2),
        MbrNo        = r.IsDBNull(3) ? 0 : r.GetDecimal(3),
        MbrSub       = SafeBuildString(r, 4),
        SsiDn        = r.IsDBNull(5) ? 0 : r.GetDecimal(5),
        BorrName     = SafeBuildString(r, 6),
        BorrAddr     = SafeBuildString(r, 7),
        BorrAddrX    = SafeBuildString(r, 8),
        BorrCity     = SafeBuildString(r, 9),
        BorrState    = SafeBuildString(r, 10),
        BorrZip      = r.IsDBNull(11) ? 0 : r.GetDecimal(11),
        SsiDc        = SafeBuildString(r, 12),
        IntPd        = r.IsDBNull(13) ? 0 : r.GetDecimal(13),
        Points       = r.IsDBNull(14) ? 0 : r.GetDecimal(14),
        InterN       = r.IsDBNull(15) ? 0 : r.GetDecimal(15),
        ErnWth       = r.IsDBNull(16) ? 0 : r.GetDecimal(16),
        Compen       = r.IsDBNull(17) ? 0 : r.GetDecimal(17),
        Rents        = r.IsDBNull(18) ? 0 : r.GetDecimal(18),
        MedPay       = r.IsDBNull(19) ? 0 : r.GetDecimal(19),
        LglPay       = r.IsDBNull(20) ? 0 : r.GetDecimal(20),
        Other        = r.IsDBNull(21) ? 0 : r.GetDecimal(21),
        WthHeld      = r.IsDBNull(22) ? 0 : r.GetDecimal(22),
        Errors       = SafeBuildString(r, 23),
        ReportToIrs  = SafeBuildString(r, 24),
        NonRptReason = SafeBuildString(r, 25),
        CorrIn       = SafeBuildString(r, 26),
        OrigDate     = SafeBuildString(r, 27),
        SecSame      = SafeBuildString(r, 28),
        SecAddr      = SafeBuildString(r, 29),
        SecDesc      = SafeBuildString(r, 30),
        SecOther     = SafeBuildString(r, 31),
        SecNum       = r.IsDBNull(32) ? 0 : r.GetDecimal(32),
        MtgAcqDt     = SafeBuildString(r, 33),
        UnpPrn       = r.IsDBNull(34) ? 0 : r.GetDecimal(34),
        FmVal        = r.IsDBNull(35) ? 0 : r.GetDecimal(35),
        DteAqr       = SafeBuildString(r, 36),
        PrDesc       = SafeBuildString(r, 37),
        Foreign      = SafeBuildString(r, 38),
        Dept         = r.IsDBNull(39) ? 0 : r.GetDecimal(39),
        ChangeDate   = SafeBuildString(r, 40),
    };

    private static LocalTaxDetail NormalizeRecordForLocal(TaxDetailRecord r)
    {
        var local = LocalTaxDetail.FromRecord(r);
        local.TaxYear = NormalizeBuildKey(local.TaxYear);
        local.Form = NormalizeBuildKey(local.Form);
        local.Asa = NormalizeBuildKey(local.Asa);
        local.MbrSub = NormalizeBuildKey(local.MbrSub);
        local.SsiDc = NormalizeBuildKey(local.SsiDc);
        local.BorrName = NormalizeBuildKey(local.BorrName);
        local.BorrAddr = NormalizeBuildKey(local.BorrAddr);
        local.BorrAddrX = NormalizeBuildKey(local.BorrAddrX);
        local.BorrCity = NormalizeBuildKey(local.BorrCity);
        local.BorrState = NormalizeBuildKey(local.BorrState);
        local.Errors = NormalizeBuildKey(local.Errors);
        local.ReportToIrs = NormalizeBuildKey(local.ReportToIrs);
        local.CorrIn = NormalizeBuildKey(local.CorrIn);
        local.Foreign = NormalizeBuildKey(local.Foreign);
        local.ChangeDate = NormalizeBuildKey(local.ChangeDate);
        local.DteAqr = NormalizeBuildKey(local.DteAqr);
        local.PrDesc = NormalizeBuildKey(local.PrDesc);
        local.OrigDate = NormalizeBuildKey(local.OrigDate);
        local.SecSame = NormalizeBuildKey(local.SecSame);
        local.SecAddr = NormalizeBuildKey(local.SecAddr);
        local.SecDesc = NormalizeBuildKey(local.SecDesc);
        local.SecOther = NormalizeBuildKey(local.SecOther);
        local.MtgAcqDt = NormalizeBuildKey(local.MtgAcqDt);
        local.UpdatedAt = DateTime.UtcNow;
        return local;
    }

    private static string NormalizeBuildKey(string? value) => (value ?? string.Empty).Trim();

    private static string GetBuildRecordKey(TaxDetailRecord record)
    {
        var normalizedMbrNo = ((long)record.MbrNo).ToString();
        return $"{NormalizeBuildKey(record.TaxYear)}|{NormalizeBuildKey(record.Form)}|{NormalizeBuildKey(record.Asa)}|{normalizedMbrNo}|{NormalizeBuildKey(record.MbrSub)}";
    }

    private static List<TaxDetailRecord> MergeBuildRowsPreferTxrdtl(
        IEnumerable<TaxDetailRecord> buildRows,
        IEnumerable<TaxDetailRecord> txrdtlRows)
    {
        var merged = buildRows.ToDictionary(GetBuildRecordKey, StringComparer.OrdinalIgnoreCase);
        foreach (var txrdtl in txrdtlRows)
        {
            merged[GetBuildRecordKey(txrdtl)] = txrdtl;
        }

        return merged.Values.ToList();
    }

    private static string SafeBuildString(System.Data.Common.DbDataReader r, int col)
    {
        if (r.IsDBNull(col)) return string.Empty;
        var val = r.GetValue(col);
        if (val is DateTime dt) return dt.ToString("yyyyMMdd");
        return val.ToString()?.Trim() ?? string.Empty;
    }
}

/// <summary>
/// Implements IValidateTaxService.
/// Replaces tx9526.rpgle – flags TXRDTL records in error.
/// Performs validation locally and updates ERRORS flag via ODBC.
/// </summary>
public sealed class ValidateTaxService : IValidateTaxService
{
    private readonly string _cs;
    private readonly string _lib;
    private readonly LocalDbContext _db;
    private readonly ILogger<ValidateTaxService> _logger;
    private static readonly HashSet<decimal> GoofySsns = new()
    {
        111111111m, 222222222m, 333333333m, 444444444m, 555555555m,
        666666666m, 777777777m, 888888888m, 999999999m
    };

    private static readonly HashSet<string> FallbackStateCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA","KS","KY","LA","ME","MD",
        "MA","MI","MN","MS","MO","MT","NE","NV","NH","NJ","NM","NY","NC","ND","OH","OK","OR","PA","RI","SC",
        "SD","TN","TX","UT","VT","VA","WA","WV","WI","WY","DC","PR","VI","GU","AS","MP"
    };

    public ValidateTaxService(IConfiguration cfg, LocalDbContext db, ILogger<ValidateTaxService> logger)
    {
        _cs     = cfg.GetConnectionString("IBMi")!;
        _lib    = cfg["IBMiSettings:Library"] ?? "TXLIB";
        _db     = db;
        _logger = logger;
    }

    public async Task<int> ValidateAsync(string taxYear, string formName,
                                          IEnumerable<string> associations, bool selectAll,
                                          IProgress<int>? progress = null)
    {
        var rows = await GetCandidateRecordsAsync(taxYear, formName, associations, selectAll);
        var validStateCodes = await GetValidStateCodesAsync();
        var flagged = 0;
        var totalRecords = rows.Count;

        // Load all existing records once instead of per-record lookup
        // IMPORTANT: Normalize Asa and MbrSub when building the key to match the normalized local records
        // Convert MbrNo to integer string format to handle decimal storage in database
        var normalizedYear = NormalizeText(taxYear);
        var normalizedForm = NormalizeText(formName);
        var existingRecords = new Dictionary<string, LocalTaxDetail>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in await _db.TaxDetails
            .Where(d => d.TaxYear == normalizedYear && d.Form == normalizedForm)
            .ToListAsync())
        {
            var key = $"{NormalizeText(record.Asa)}|{(long)record.MbrNo}|{NormalizeText(record.MbrSub)}";
            existingRecords[key] = record;
        }

        // Validate and collect changes
        var recordsToAdd = new List<LocalTaxDetail>();
        var recordsToUpdate = new List<(LocalTaxDetail Existing, LocalTaxDetail Updated)>();

        int recordsProcessed = 0;
        foreach (var record in rows)
        {
            record.Errors = HasValidationError(record, validStateCodes) ? "Y" : "N";
            if (record.Errors == "Y")
            {
                flagged++;
            }

            var local = NormalizeLocalRecord(record);
            // Build key with MbrNo as integer string to match database key format
            var key = $"{local.Asa}|{(long)local.MbrNo}|{local.MbrSub}";

            if (existingRecords.TryGetValue(key, out LocalTaxDetail? existing) && existing is not null)
            {
                local.Id = existing.Id;
                local.CreatedAt = existing.CreatedAt;
                local.UpdatedAt = DateTime.UtcNow;
                recordsToUpdate.Add((existing, local));
            }
            else
            {
                recordsToAdd.Add(local);
            }

            recordsProcessed++;
            // Report progress: percentage complete (0-100)
            // Use floating point math to avoid integer division rounding down to 0
            var percentComplete = totalRecords > 0 ? (int)((recordsProcessed * 100.0) / totalRecords) : 100;
            progress?.Report(percentComplete);
        }

        // Batch insert
        if (recordsToAdd.Count > 0)
        {
            _db.TaxDetails.AddRange(recordsToAdd);
        }

        // Batch update
        foreach (var (existing, updated) in recordsToUpdate)
        {
            _db.Entry(existing).CurrentValues.SetValues(updated);
        }

        // Single save for all changes
        if (recordsToAdd.Count > 0 || recordsToUpdate.Count > 0)
        {
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Log detailed error information for debugging
                var errorMsg = BuildSaveErrorDetails(recordsToAdd, recordsToUpdate, ex);
                throw new InvalidOperationException(
                    $"Error saving validation changes for {formName} {taxYear}. Added: {recordsToAdd.Count}, Updated: {recordsToUpdate.Count}. Details: {errorMsg}", 
                    ex);
            }
        }

        // Final progress: 100%
        progress?.Report(100);

        return flagged;
    }

    private async Task<IList<TaxDetailRecord>> GetCandidateRecordsAsync(
        string taxYear, string formName, IEnumerable<string> associations, bool selectAll)
    {
        var localRows = await QueryLocalAsync(taxYear, formName, associations, selectAll);

        try
        {
            var ibmiRows = await QueryIbmiAsync(taxYear, formName, associations, selectAll);
            return MergeCandidateRows(ibmiRows, localRows);
        }
        catch (OdbcException ex)
        {
            _logger.LogWarning(ex, "Falling back to local validation data for {TaxYear} {FormName}.", taxYear, formName);
            return localRows;
        }
    }

    private async Task<List<TaxDetailRecord>> QueryIbmiAsync(
        string taxYear, string formName, IEnumerable<string> associations, bool selectAll)
    {
        if (!int.TryParse(taxYear, out var taxYearInt))
        {
            return new List<TaxDetailRecord>();
        }

        var assocList = associations.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sql = new System.Text.StringBuilder($@"SELECT TAXYR,FORM,ASA,MBRNO,MBRSUB,SSIDN,BNM,BAD,BADX,BCTY,BST,BZP,
                            SSIDC,INTPD,POINTS,INTERN,ERNWTH,COMPEN,RENTS,MEDPAY,LGLPAY,OTHER,
                            WTHHELD,ERRORS,RPT_TO_IRS,NONRPT_RSN,CORRIN,ORIGDATE,SECSAME,SECADDR,SECDESC,
                            SECOTHER,SECNUM,MTGACQDT,UNPPRN,FMVAL,DTEAQR,PRDESC,FOREGN,DEPT,CHANGE_DT
                     FROM {_lib}/TXRDTL
                     WHERE TAXYR=? AND FORM=?");

        if (!selectAll && assocList.Count > 0)
        {
            sql.Append(" AND ASA IN (");
            sql.Append(string.Join(",", assocList.Select(_ => "?")));
            sql.Append(")");
        }

        sql.Append(" ORDER BY ASA, MBRNO, MBRSUB");

        var rows = new List<TaxDetailRecord>();
        await using var conn = new OdbcConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new OdbcCommand(sql.ToString(), conn);
        cmd.Parameters.AddWithValue("?", taxYearInt);
        cmd.Parameters.AddWithValue("?", formName.Trim().PadRight(9));
        foreach (var assoc in assocList)
        {
            cmd.Parameters.AddWithValue("?", assoc.PadRight(3));
        }

        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            rows.Add(MapValidationRecord(rdr));
        }

        return rows;
    }

    private async Task<List<TaxDetailRecord>> QueryLocalAsync(
        string taxYear, string formName, IEnumerable<string> associations, bool selectAll)
    {
        var assocList = associations.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var query = _db.TaxDetails.Where(d => d.TaxYear == NormalizeText(taxYear) && d.Form == NormalizeText(formName));

        if (!selectAll && assocList.Count > 0)
        {
            query = query.Where(d => assocList.Contains(d.Asa));
        }

        var localRows = await query
            .Select(d => d.ToRecord())
            .ToListAsync();

        return localRows
            .OrderBy(d => d.Asa)
            .ThenBy(d => d.MbrNo)
            .ThenBy(d => d.MbrSub)
            .ToList();
    }

    private static bool HasValidationError(TaxDetailRecord record, HashSet<string> validStateCodes)
    {
        var form = NormalizeText(record.Form).ToUpperInvariant();
        var ssiDc = NormalizeText(record.SsiDc).ToUpperInvariant();
        var foreign = NormalizeText(record.Foreign).Equals("Y", StringComparison.OrdinalIgnoreCase);
        var reportToIrs = NormalizeText(record.ReportToIrs).ToUpperInvariant();
        var nonRptReason = NormalizeText(record.NonRptReason);
        var state = NormalizeText(record.BorrState).ToUpperInvariant();

        if (record.SsiDn <= 0) return true;
        if (GoofySsns.Contains(record.SsiDn)) return true;
        if (ssiDc is not "S" and not "E") return true;
        if (string.IsNullOrWhiteSpace(record.BorrName)) return true;
        if (string.IsNullOrWhiteSpace(record.BorrAddr)) return true;
        if (string.IsNullOrWhiteSpace(record.BorrCity)) return true;
        if (!foreign && string.IsNullOrWhiteSpace(record.BorrState)) return true;
        if (!foreign && record.BorrZip < 1000000) return true;
        if (!foreign && validStateCodes.Count > 0 && !validStateCodes.Contains(state)) return true;
        if (reportToIrs is not "Y" and not "N") return true;
        if (reportToIrs == "N" && string.IsNullOrWhiteSpace(nonRptReason)) return true;
        if (reportToIrs == "Y" && !string.IsNullOrWhiteSpace(nonRptReason)) return true;

        if (form == "1098"
            && (!string.IsNullOrWhiteSpace(record.SecAddr)
                || !string.IsNullOrWhiteSpace(record.SecDesc)
                || !string.IsNullOrWhiteSpace(record.SecOther))
            && record.SecNum <= 0)
        {
            return true;
        }

        if (form == "1099-A")
        {
            if (!IsValidDateAcquired(record.DteAqr)) return true;
            if (record.FmVal <= 0 || record.UnpPrn <= 0 || string.IsNullOrWhiteSpace(record.PrDesc)) return true;
        }

        return false;
    }

    private async Task<HashSet<string>> GetValidStateCodesAsync()
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            await using var cmd = new OdbcCommand($"SELECT * FROM {_lib}/BKSTAT", conn);
            await using var rdr = await cmd.ExecuteReaderAsync();

            while (await rdr.ReadAsync())
            {
                if (rdr.FieldCount == 0 || rdr.IsDBNull(0))
                {
                    continue;
                }

                var code = rdr.GetValue(0)?.ToString()?.Trim().ToUpperInvariant() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(code))
                {
                    codes.Add(code);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to load BKSTAT state codes; using fallback state code set.");
        }

        if (codes.Count == 0)
        {
            return new HashSet<string>(FallbackStateCodes, StringComparer.OrdinalIgnoreCase);
        }

        return codes;
    }

    private static bool IsValidDateAcquired(string? value)
    {
        var text = NormalizeText(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return DateTime.TryParseExact(text,
                   "yyyyMMdd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out _)
               || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
               || DateTime.TryParse(text, out _);
    }

    private static TaxDetailRecord MapValidationRecord(System.Data.Common.DbDataReader r) => new()
    {
        TaxYear      = SafeValidationString(r, 0),
        Form         = SafeValidationString(r, 1),
        Asa          = SafeValidationString(r, 2),
        MbrNo        = r.IsDBNull(3) ? 0 : r.GetDecimal(3),
        MbrSub       = SafeValidationString(r, 4),
        SsiDn        = r.IsDBNull(5) ? 0 : r.GetDecimal(5),
        BorrName     = SafeValidationString(r, 6),
        BorrAddr     = SafeValidationString(r, 7),
        BorrAddrX    = SafeValidationString(r, 8),
        BorrCity     = SafeValidationString(r, 9),
        BorrState    = SafeValidationString(r, 10),
        BorrZip      = r.IsDBNull(11) ? 0 : r.GetDecimal(11),
        SsiDc        = SafeValidationString(r, 12),
        IntPd        = r.IsDBNull(13) ? 0 : r.GetDecimal(13),
        Points       = r.IsDBNull(14) ? 0 : r.GetDecimal(14),
        InterN       = r.IsDBNull(15) ? 0 : r.GetDecimal(15),
        ErnWth       = r.IsDBNull(16) ? 0 : r.GetDecimal(16),
        Compen       = r.IsDBNull(17) ? 0 : r.GetDecimal(17),
        Rents        = r.IsDBNull(18) ? 0 : r.GetDecimal(18),
        MedPay       = r.IsDBNull(19) ? 0 : r.GetDecimal(19),
        LglPay       = r.IsDBNull(20) ? 0 : r.GetDecimal(20),
        Other        = r.IsDBNull(21) ? 0 : r.GetDecimal(21),
        WthHeld      = r.IsDBNull(22) ? 0 : r.GetDecimal(22),
        Errors       = SafeValidationString(r, 23),
        ReportToIrs  = SafeValidationString(r, 24),
        NonRptReason = SafeValidationString(r, 25),
        CorrIn       = SafeValidationString(r, 26),
        OrigDate     = SafeValidationString(r, 27),
        SecSame      = SafeValidationString(r, 28),
        SecAddr      = SafeValidationString(r, 29),
        SecDesc      = SafeValidationString(r, 30),
        SecOther     = SafeValidationString(r, 31),
        SecNum       = r.IsDBNull(32) ? 0 : r.GetDecimal(32),
        MtgAcqDt     = SafeValidationString(r, 33),
        UnpPrn       = r.IsDBNull(34) ? 0 : r.GetDecimal(34),
        FmVal        = r.IsDBNull(35) ? 0 : r.GetDecimal(35),
        DteAqr       = SafeValidationString(r, 36),
        PrDesc       = SafeValidationString(r, 37),
        Foreign      = SafeValidationString(r, 38),
        Dept         = r.IsDBNull(39) ? 0 : r.GetDecimal(39),
        ChangeDate   = SafeValidationString(r, 40),
    };

    private static LocalTaxDetail NormalizeLocalRecord(TaxDetailRecord record)
    {
        var local = LocalTaxDetail.FromRecord(record);
        local.TaxYear = NormalizeText(local.TaxYear);
        local.Form = NormalizeText(local.Form);
        local.Asa = NormalizeText(local.Asa);
        local.MbrSub = NormalizeText(local.MbrSub);
        local.SsiDc = NormalizeText(local.SsiDc);
        local.BorrName = NormalizeText(local.BorrName);
        local.BorrAddr = NormalizeText(local.BorrAddr);
        local.BorrAddrX = NormalizeText(local.BorrAddrX);
        local.BorrCity = NormalizeText(local.BorrCity);
        local.BorrState = NormalizeText(local.BorrState);
        local.Errors = NormalizeText(local.Errors);
        local.ReportToIrs = NormalizeText(local.ReportToIrs);
        local.NonRptReason = NormalizeText(local.NonRptReason);
        local.CorrIn = NormalizeText(local.CorrIn);
        local.Foreign = NormalizeText(local.Foreign);
        local.ChangeDate = NormalizeText(local.ChangeDate);
        local.DteAqr = NormalizeText(local.DteAqr);
        local.PrDesc = NormalizeText(local.PrDesc);
        local.OrigDate = NormalizeText(local.OrigDate);
        local.SecSame = NormalizeText(local.SecSame);
        local.SecAddr = NormalizeText(local.SecAddr);
        local.SecDesc = NormalizeText(local.SecDesc);
        local.SecOther = NormalizeText(local.SecOther);
        local.MtgAcqDt = NormalizeText(local.MtgAcqDt);
        local.UpdatedAt = DateTime.UtcNow;
        return local;
    }

    private static string GetDetailKey(TaxDetailRecord record)
    {
        var normalizedMbrNo = ((long)record.MbrNo).ToString();
        return $"{NormalizeText(record.TaxYear)}|{NormalizeText(record.Form)}|{NormalizeText(record.Asa)}|{normalizedMbrNo}|{NormalizeText(record.MbrSub)}";
    }

    public static List<TaxDetailRecord> MergeCandidateRows(
        IEnumerable<TaxDetailRecord> ibmiRows,
        IEnumerable<TaxDetailRecord> localRows)
    {
        var merged = ibmiRows.ToDictionary(GetDetailKey, StringComparer.OrdinalIgnoreCase);
        foreach (var local in localRows)
        {
            var key = GetDetailKey(local);
            if (merged.TryGetValue(key, out var ibmi)
                && string.IsNullOrWhiteSpace(local.NonRptReason)
                && !string.IsNullOrWhiteSpace(ibmi.NonRptReason))
            {
                local.NonRptReason = ibmi.NonRptReason;
            }

            merged[key] = local;
        }

        return merged.Values.ToList();
    }

    private static string NormalizeText(string? value) => (value ?? string.Empty).Trim();

    private static string SafeValidationString(System.Data.Common.DbDataReader r, int col)
    {
        if (r.IsDBNull(col)) return string.Empty;
        var val = r.GetValue(col);
        if (val is DateTime dt) return dt.ToString("yyyyMMdd");
        return val.ToString()?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Builds detailed error information for SaveChangesAsync failures
    /// </summary>
    private static string BuildSaveErrorDetails(
        List<LocalTaxDetail> recordsToAdd,
        List<(LocalTaxDetail Existing, LocalTaxDetail Updated)> recordsToUpdate,
        Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Original error: {ex.Message}");

        if (ex.InnerException is not null)
        {
            sb.AppendLine($"Inner error: {ex.InnerException.Message}");
        }

        // Log sample of records being saved
        if (recordsToAdd.Count > 0)
        {
            sb.AppendLine($"Sample record to add (first): TaxYear={recordsToAdd[0].TaxYear}, Form={recordsToAdd[0].Form}, Asa={recordsToAdd[0].Asa}, MbrNo={recordsToAdd[0].MbrNo}");
        }

        if (recordsToUpdate.Count > 0)
        {
            var (existing, updated) = recordsToUpdate[0];
            sb.AppendLine($"Sample record to update (first): TaxYear={existing.TaxYear}, Form={existing.Form}, Asa={existing.Asa}, MbrNo={existing.MbrNo}");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Implements ISummaryService.
/// Replaces tx9520.sqlrpgle – queries TXRDTL for per-association summary.
/// </summary>
public sealed class SummaryService : ISummaryService
{
    private readonly string _cs;
    private readonly string _lib;
    private readonly ILogger<SummaryService> _logger;

    public SummaryService(IConfiguration cfg, ILogger<SummaryService> logger)
    {
        _cs     = cfg.GetConnectionString("IBMi")!;
        _lib    = cfg["IBMiSettings:Library"] ?? "TXLIB";
        _logger = logger;
    }

    public async Task<IList<SummaryRow>> GetSummaryAsync(string taxYear, string formName)
    {
        // Mirrors the SQL cursor inside tx9520.$Load_SFL
        var sql = $@"
            SELECT ASA, RPT_TO_IRS,
                   COUNT(*) AS CNT,
                   SUM(INTPD)  AS INTPD,
                   SUM(POINTS) AS POINTS,
                   SUM(INTERN) AS INTERN,
                   SUM(ERNWTH) AS ERNWTH,
                   SUM(DIVRCV) AS DIVRCV,
                   SUM(DIVWTH) AS DIVWTH,
                   SUM(PATREF) AS PATREF,
                   SUM(PATWTH) AS PATWTH,
                   SUM(FMVAL)  AS FMVAL,
                   SUM(UNPPRN) AS UNPPRN,
                   SUM(COMPEN) AS COMPEN,
                   SUM(RENTS)  AS RENTS,
                   SUM(MEDPAY) AS MEDPAY,
                   SUM(LGLPAY) AS LGLPAY,
                   SUM(OTHER)  AS OTHER
            FROM {_lib}/TXRDTL
            WHERE TAXYR = ? AND FORM = ?
            GROUP BY ASA, RPT_TO_IRS
            ORDER BY ASA, RPT_TO_IRS";

        var dict = new Dictionary<string, SummaryRow>();
        await using var conn = new OdbcConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new OdbcCommand(sql, conn);
        cmd.Parameters.AddWithValue("?", taxYear);
        cmd.Parameters.AddWithValue("?", formName);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var asa    = rdr.GetString(0).Trim();
            var isYes  = rdr.GetString(1).Trim().Equals("Y", StringComparison.OrdinalIgnoreCase);
            var cnt    = rdr.GetInt32(2);

            var intPd  = rdr.IsDBNull(3)  ? 0m : rdr.GetDecimal(3);
            var points = rdr.IsDBNull(4)  ? 0m : rdr.GetDecimal(4);
            var interN = rdr.IsDBNull(5)  ? 0m : rdr.GetDecimal(5);
            var ernWth = rdr.IsDBNull(6)  ? 0m : rdr.GetDecimal(6);
            var divRcv = rdr.IsDBNull(7)  ? 0m : rdr.GetDecimal(7);
            var divWth = rdr.IsDBNull(8)  ? 0m : rdr.GetDecimal(8);
            var patRef = rdr.IsDBNull(9)  ? 0m : rdr.GetDecimal(9);
            var patWth = rdr.IsDBNull(10) ? 0m : rdr.GetDecimal(10);
            var fmVal  = rdr.IsDBNull(11) ? 0m : rdr.GetDecimal(11);
            var unpPrn = rdr.IsDBNull(12) ? 0m : rdr.GetDecimal(12);
            var compen = rdr.IsDBNull(13) ? 0m : rdr.GetDecimal(13);
            var rents  = rdr.IsDBNull(14) ? 0m : rdr.GetDecimal(14);
            var medPay = rdr.IsDBNull(15) ? 0m : rdr.GetDecimal(15);
            var lglPay = rdr.IsDBNull(16) ? 0m : rdr.GetDecimal(16);
            var other  = rdr.IsDBNull(17) ? 0m : rdr.GetDecimal(17);

            var normalizedForm = (formName ?? string.Empty).Trim().ToUpperInvariant();
            decimal a1;
            decimal a2;
            decimal a3 = 0m;

            switch (normalizedForm)
            {
                case "1098":
                    a1 = intPd;
                    a2 = points;
                    break;
                case "1099-INT":
                    a1 = interN;
                    a2 = ernWth;
                    break;
                case "1099-DIV":
                    a1 = divRcv;
                    a2 = divWth;
                    break;
                case "1099-PATR":
                    a1 = patRef;
                    a2 = patWth;
                    break;
                case "1099-A":
                    a1 = fmVal;
                    a2 = unpPrn;
                    break;
                case "1099-MISC":
                    a1 = rents;
                    a2 = medPay;
                    a3 = other;
                    break;
                case "1099-NEC":
                    a1 = compen;
                    a2 = lglPay;
                    break;
                default:
                    // Safe fallback for unexpected forms.
                    a1 = intPd;
                    a2 = points;
                    break;
            }

            if (!dict.TryGetValue(asa, out var row))
                dict[asa] = row = new SummaryRow { Assoc = asa };

            if (isYes) { row.CountYes = cnt; row.Amt1Yes = a1; row.Amt2Yes = a2; row.Amt3Yes = a3; }
            else       { row.CountNo  = cnt; row.Amt1No  = a1; row.Amt2No  = a2; row.Amt3No  = a3; }
        }
        return dict.Values.OrderBy(r => r.Assoc).ToList();
    }
}

/// <summary>
/// Implements IMaintainService.
/// Replaces tx9525.sqlrpgle – CRUD on TXRDTL + local audit write in SQLite.
/// </summary>
public sealed class MaintainService : IMaintainService
{
    private readonly string _cs;
    private readonly string _lib;
    private readonly IHttpContextAccessor _httpCtx;
    private readonly LocalDbContext _db;
    private readonly ILogger<MaintainService> _logger;
    private readonly bool _logDataQualityIssues;

    public MaintainService(IConfiguration cfg, IHttpContextAccessor httpCtx,
                            LocalDbContext db, ILogger<MaintainService> logger)
    {
        _cs      = cfg.GetConnectionString("IBMi")!;
        _lib     = cfg["IBMiSettings:Library"] ?? "TXLIB";
        _httpCtx = httpCtx;
        _db      = db;
        _logger  = logger;
        _logDataQualityIssues = cfg.GetValue("Diagnostics:LogNullOrEmptyKeyFields", true);
    }

    public async Task<TaxDetailRecord?> GetRecordAsync(
        string taxYear, string formName, string asa, decimal mbrNo, string? mbrSub)
    {
        // ── Step 1: SQLite staged record (prefer latest local edit for this account) ──
        var keyTaxYear = NormalizeKey(taxYear);
        var keyForm    = NormalizeKey(formName);
        var keyAsa     = NormalizeKey(asa);
        var keyMbrSub  = NormalizeKey(mbrSub);

        IQueryable<LocalTaxDetail> localQuery = _db.TaxDetails
            .Where(d => d.TaxYear == keyTaxYear && d.Form == keyForm &&
                        d.Asa == keyAsa && d.MbrNo == mbrNo);

        if (!string.IsNullOrEmpty(keyMbrSub))
        {
            localQuery = localQuery.Where(d => d.MbrSub == keyMbrSub);
        }

        var local = await localQuery
            .OrderByDescending(d => d.UpdatedAt)
            .ThenByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync();

        if (local is not null)
        {
            var localRecord = local.ToRecord();
            if (!string.IsNullOrWhiteSpace(localRecord.NonRptReason))
            {
                return localRecord;
            }

            var ibmiRecordForReason = await FetchRecordFromIbmiAsync(
                taxYear, formName, asa, mbrNo, keyMbrSub);

            if (ibmiRecordForReason is not null && !string.IsNullOrWhiteSpace(ibmiRecordForReason.NonRptReason))
            {
                localRecord.NonRptReason = ibmiRecordForReason.NonRptReason;
            }

            return localRecord;
        }

        // ── Step 2: IBM i TXRDTL fallback ─────────────────────────────────
        return await FetchRecordFromIbmiAsync(taxYear, formName, asa, mbrNo, keyMbrSub);
    }

    private async Task<TaxDetailRecord?> FetchRecordFromIbmiAsync(
        string taxYear, string formName, string asa, decimal mbrNo, string keyMbrSub)
    {
        int taxYearInt;
        if (!int.TryParse(taxYear, out taxYearInt))
        {
            return null;
        }

        var formPadded   = formName.Trim().PadRight(9);
        var asaPadded    = asa.Trim().PadRight(3);
        var mbrNoLong    = (long)mbrNo;
        var mbrSubPadded = keyMbrSub.PadRight(3);

        var sql = $@"SELECT TAXYR,FORM,ASA,MBRNO,MBRSUB,SSIDN,BNM,BAD,BADX,BCTY,BST,BZP,
                            SSIDC,INTPD,POINTS,INTERN,ERNWTH,COMPEN,RENTS,MEDPAY,LGLPAY,OTHER,
                    WTHHELD,ERRORS,RPT_TO_IRS,NONRPT_RSN,CORRIN,ORIGDATE,SECSAME,SECADDR,SECDESC,
                            SECOTHER,SECNUM,MTGACQDT,UNPPRN,FMVAL,DTEAQR,PRDESC,FOREGN,DEPT
                     FROM {_lib}/TXRDTL
                     WHERE TAXYR=? AND FORM=? AND ASA=? AND MBRNO=?";

        if (!string.IsNullOrEmpty(keyMbrSub))
        {
            sql += " AND MBRSUB=?";
        }

        sql += " FETCH FIRST 1 ROW ONLY";

        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();

            await using var cmd = new OdbcCommand(sql, conn);
            cmd.Parameters.AddWithValue("?", taxYearInt);  // TAXYR 4S 0
            cmd.Parameters.AddWithValue("?", formPadded);  // FORM  9A
            cmd.Parameters.AddWithValue("?", asaPadded);   // ASA   3A
            cmd.Parameters.AddWithValue("?", mbrNoLong);   // MBRNO 11S 0

            if (!string.IsNullOrEmpty(keyMbrSub))
            {
                cmd.Parameters.AddWithValue("?", mbrSubPadded); // MBRSUB 3A
            }

            await using var rdr = await cmd.ExecuteReaderAsync();
            if (await rdr.ReadAsync())
            {
                var rec = MapRecord(rdr);
                return rec;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to fetch record from IBM i for maintain lookup (TaxYear={TaxYear}, Form={Form}, Asa={Asa}, MbrNo={MbrNo}, MbrSub={MbrSub}).",
                taxYear, formName, asa, mbrNo, keyMbrSub);
        }

        return null;
    }

    public async Task AddRecordAsync(TaxDetailRecord r)
    {
        LogDataQualityIssues(r, "Add");
        r.ChangeDate = DateTime.Now.ToString("yyyyMMdd");
        await UpsertLocalTaxDetailAsync(r);
        await InsertLocalAuditAsync(r);
    }

    public async Task UpdateRecordAsync(TaxDetailRecord r)
    {
        LogDataQualityIssues(r, "Update");
        r.ChangeDate = DateTime.Now.ToString("yyyyMMdd");
        
        // Always upsert to SQLite, whether record came from IBM i or was created locally.
        // The web UI treats SQLite as the staging store for all updates.
        await UpsertLocalTaxDetailAsync(r);
        await InsertLocalAuditAsync(r);
    }

    public async Task<bool> DeleteRecordAsync(TaxDetailRecord r)
    {
        var normalizedTaxYear = NormalizeKey(r.TaxYear);
        var normalizedForm = NormalizeKey(r.Form);
        var normalizedAsa = NormalizeKey(r.Asa);
        var normalizedMbrSub = NormalizeKey(r.MbrSub);

        var existing = await _db.TaxDetails.FirstOrDefaultAsync(d =>
            d.TaxYear == normalizedTaxYear &&
            d.Form == normalizedForm &&
            d.Asa == normalizedAsa &&
            d.MbrNo == r.MbrNo &&
            d.MbrSub == normalizedMbrSub);

        if (existing is null)
        {
            return false;
        }

        _db.TaxDetails.Remove(existing);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<IList<TaxDetailRecord>> GetErrorRecordsAsync(
        string taxYear, string formName, string asa)
    {
        var rows = new List<TaxDetailRecord>();
        var normalizedTaxYear = NormalizeKey(taxYear);
        var normalizedForm = NormalizeKey(formName);
        var normalizedAsa = NormalizeKey(asa);

        // SQLite local error records
        var localErrors = await _db.TaxDetails
            .Where(d => d.TaxYear == normalizedTaxYear && d.Form == normalizedForm &&
                        d.Asa == normalizedAsa && d.Errors == "Y")
            .ToListAsync();
        rows.AddRange(localErrors
            .OrderBy(d => d.MbrNo)
            .Select(d => d.ToRecord()));

        // IBM i error records
        var sql = $@"SELECT TAXYR,FORM,ASA,MBRNO,MBRSUB,SSIDN,BNM,BAD,BADX,BCTY,BST,BZP,
                            SSIDC,INTPD,POINTS,INTERN,ERNWTH,COMPEN,RENTS,MEDPAY,LGLPAY,OTHER,
                    WTHHELD,ERRORS,RPT_TO_IRS,NONRPT_RSN,CORRIN,ORIGDATE,SECSAME,SECADDR,SECDESC,
                            SECOTHER,SECNUM,MTGACQDT,UNPPRN,FMVAL,DTEAQR,PRDESC,FOREGN,DEPT
                     FROM {_lib}/TXRDTL
                     WHERE TAXYR=? AND FORM=? AND ASA=? AND ERRORS='Y'
                     ORDER BY MBRNO";
        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            await using var cmd = new OdbcCommand(sql, conn);
            cmd.Parameters.AddWithValue("?", int.Parse(taxYear));         // TAXYR 4S 0
            cmd.Parameters.AddWithValue("?", formName.Trim().PadRight(9)); // FORM  9A
            cmd.Parameters.AddWithValue("?", asa.Trim().PadRight(3));      // ASA   3A
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) rows.Add(MapRecord(rdr));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to fetch IBM i error records (TaxYear={TaxYear}, Form={Form}, Asa={Asa}).",
                taxYear, formName, asa);
        }

        return rows;
    }

    public async Task<PagedResult<TaxDetailRecord>> GetErrorRecordsPageAsync(
        string taxYear, string formName, string asa, int pageNumber, int pageSize)
    {
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Max(1, pageSize);

        var normalizedTaxYear = NormalizeKey(taxYear);
        var normalizedForm = NormalizeKey(formName);
        var normalizedAsa = NormalizeKey(asa);

        var localBaseQuery = _db.TaxDetails.Where(d =>
            d.TaxYear == normalizedTaxYear &&
            d.Form == normalizedForm &&
            d.Asa == normalizedAsa);

        var localSourceCount = await localBaseQuery.CountAsync();
        if (localSourceCount > 0)
        {
            var filteredLocalRows = await localBaseQuery
                .Where(d => d.Errors == "Y")
                .Select(d => d.ToRecord())
                .ToListAsync();

            var orderedLocalRows = filteredLocalRows
                .OrderBy(d => d.MbrNo)
                .ThenBy(d => d.MbrSub)
                .ToList();

            return new PagedResult<TaxDetailRecord>
            {
                Items = orderedLocalRows
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .ToList(),
                PageNumber = pageNumber,
                PageSize = pageSize,
                TotalCount = orderedLocalRows.Count
            };
        }

        if (!int.TryParse(taxYear, out var taxYearInt))
        {
            return new PagedResult<TaxDetailRecord>
            {
                PageNumber = pageNumber,
                PageSize = pageSize,
                TotalCount = 0
            };
        }

        var countSql = $@"SELECT COUNT(*)
                          FROM {_lib}/TXRDTL
                          WHERE TAXYR=? AND FORM=? AND ASA=? AND ERRORS='Y'";
        var pageSql = $@"SELECT TAXYR,FORM,ASA,MBRNO,MBRSUB,SSIDN,BNM,BAD,BADX,BCTY,BST,BZP,
                                SSIDC,INTPD,POINTS,INTERN,ERNWTH,COMPEN,RENTS,MEDPAY,LGLPAY,OTHER,
                        WTHHELD,ERRORS,RPT_TO_IRS,NONRPT_RSN,CORRIN,ORIGDATE,SECSAME,SECADDR,SECDESC,
                                SECOTHER,SECNUM,MTGACQDT,UNPPRN,FMVAL,DTEAQR,PRDESC,FOREGN,DEPT
                         FROM {_lib}/TXRDTL
                         WHERE TAXYR=? AND FORM=? AND ASA=? AND ERRORS='Y'
                         ORDER BY MBRNO, MBRSUB
                         OFFSET {(pageNumber - 1) * pageSize} ROWS FETCH NEXT {pageSize} ROWS ONLY";

        var totalCount = 0;
        var rows = new List<TaxDetailRecord>();

        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();

            await using (var countCmd = new OdbcCommand(countSql, conn))
            {
                countCmd.Parameters.AddWithValue("?", taxYearInt);
                countCmd.Parameters.AddWithValue("?", formName.Trim().PadRight(9));
                countCmd.Parameters.AddWithValue("?", asa.Trim().PadRight(3));
                totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync() ?? 0);
            }

            await using var pageCmd = new OdbcCommand(pageSql, conn);
            pageCmd.Parameters.AddWithValue("?", taxYearInt);
            pageCmd.Parameters.AddWithValue("?", formName.Trim().PadRight(9));
            pageCmd.Parameters.AddWithValue("?", asa.Trim().PadRight(3));

            await using var rdr = await pageCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                rows.Add(MapRecord(rdr));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to fetch paged IBM i error records (TaxYear={TaxYear}, Form={Form}, Asa={Asa}, Page={Page}, PageSize={PageSize}).",
                taxYear, formName, asa, pageNumber, pageSize);
        }

        return new PagedResult<TaxDetailRecord>
        {
            Items = rows,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    // ── helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Safely read a column that IBM i may return as either a String or a DateTime.
    /// Date fields (ORIGDATE, MTGACQDT, DTEAQR etc.) come back as DateTime from the ODBC driver.
    /// We store them as yyyyMMdd strings to match the existing RPG convention.
    /// </summary>
    private static string SafeGetString(System.Data.Common.DbDataReader r, int col)
    {
        if (r.IsDBNull(col)) return "";
        var val = r.GetValue(col);
        if (val is DateTime dt) return dt.ToString("yyyyMMdd");
        return val.ToString()?.Trim() ?? "";
    }

    private static TaxDetailRecord MapRecord(System.Data.Common.DbDataReader r) => new()
    {
        TaxYear    = SafeGetString(r, 0),
        Form       = SafeGetString(r, 1),
        Asa        = SafeGetString(r, 2),
        MbrNo      = r.IsDBNull(3) ? 0 : r.GetDecimal(3),
        MbrSub     = SafeGetString(r, 4),
        SsiDn      = r.IsDBNull(5) ? 0 : r.GetDecimal(5),
        BorrName   = SafeGetString(r, 6),
        BorrAddr   = SafeGetString(r, 7),
        BorrAddrX  = SafeGetString(r, 8),
        BorrCity   = SafeGetString(r, 9),
        BorrState  = SafeGetString(r, 10),
        BorrZip    = r.IsDBNull(11) ? 0 : r.GetDecimal(11),
        SsiDc      = SafeGetString(r, 12),
        IntPd      = r.IsDBNull(13) ? 0 : r.GetDecimal(13),
        Points     = r.IsDBNull(14) ? 0 : r.GetDecimal(14),
        InterN     = r.IsDBNull(15) ? 0 : r.GetDecimal(15),
        ErnWth     = r.IsDBNull(16) ? 0 : r.GetDecimal(16),
        Compen     = r.IsDBNull(17) ? 0 : r.GetDecimal(17),
        Rents      = r.IsDBNull(18) ? 0 : r.GetDecimal(18),
        MedPay     = r.IsDBNull(19) ? 0 : r.GetDecimal(19),
        LglPay     = r.IsDBNull(20) ? 0 : r.GetDecimal(20),
        Other      = r.IsDBNull(21) ? 0 : r.GetDecimal(21),
        WthHeld    = r.IsDBNull(22) ? 0 : r.GetDecimal(22),
        Errors       = SafeGetString(r, 23),
        ReportToIrs  = SafeGetString(r, 24),
        NonRptReason = SafeGetString(r, 25),
        CorrIn       = SafeGetString(r, 26),
        OrigDate     = SafeGetString(r, 27),   // ORIGDATE  — may be DateTime from ODBC
        SecSame      = SafeGetString(r, 28),
        SecAddr      = SafeGetString(r, 29),
        SecDesc      = SafeGetString(r, 30),
        SecOther     = SafeGetString(r, 31),
        SecNum       = r.IsDBNull(32) ? 0 : r.GetDecimal(32),
        MtgAcqDt     = SafeGetString(r, 33),   // MTGACQDT  — may be DateTime from ODBC
        UnpPrn       = r.IsDBNull(34) ? 0 : r.GetDecimal(34),
        FmVal        = r.IsDBNull(35) ? 0 : r.GetDecimal(35),
        DteAqr       = SafeGetString(r, 36),    // DTEAQR    — may be DateTime from ODBC
        PrDesc       = SafeGetString(r, 37),
        Foreign      = SafeGetString(r, 38),
        Dept         = r.IsDBNull(39) ? 0 : r.GetDecimal(39),
    };

    private async Task UpsertLocalTaxDetailAsync(TaxDetailRecord r)
    {
        try
        {
            var normalized = NormalizeRecordForLocal(r);
            var existing = await _db.TaxDetails.FirstOrDefaultAsync(d =>
                d.TaxYear == normalized.TaxYear &&
                d.Form == normalized.Form &&
                d.Asa == normalized.Asa &&
                d.MbrNo == normalized.MbrNo &&
                d.MbrSub == normalized.MbrSub);

            if (existing is null)
            {
                _db.TaxDetails.Add(normalized);
            }
            else
            {
                normalized.Id = existing.Id;
                normalized.CreatedAt = existing.CreatedAt;
                normalized.UpdatedAt = DateTime.UtcNow;
                _db.Entry(existing).CurrentValues.SetValues(normalized);
            }

            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] UpsertLocalTaxDetailAsync FAILED: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                Console.WriteLine($"[ERROR] InnerException: {ex.InnerException.Message}");
            throw;
        }
    }

    private async Task InsertLocalAuditAsync(TaxDetailRecord r)
    {
        var audit = NormalizeLocalAuditRecord(r);
        _db.TaxAudits.Add(audit);
        await _db.SaveChangesAsync();
    }

    private void LogDataQualityIssues(TaxDetailRecord r, string operation)
    {
        if (!_logDataQualityIssues)
        {
            return;
        }

        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(r.TaxYear)) issues.Add("TaxYear is null/empty");
        if (string.IsNullOrWhiteSpace(r.Form)) issues.Add("Form is null/empty");
        if (string.IsNullOrWhiteSpace(r.Asa)) issues.Add("Asa is null/empty");
        if (string.IsNullOrWhiteSpace(r.MbrSub)) issues.Add("MbrSub is null/empty");
        if (string.IsNullOrWhiteSpace(r.BorrName)) issues.Add("BorrName is null/empty");
        if (r.MbrNo <= 0) issues.Add("MbrNo is <= 0");
        if (r.SsiDn <= 0) issues.Add("SsiDn is <= 0");

        if (issues.Count > 0)
        {
            _logger.LogWarning(
                "Data quality warning during maintain {Operation}: {Issues}. Key={TaxYear}|{Form}|{Asa}|{MbrNo}|{MbrSub}",
                operation,
                string.Join("; ", issues),
                r.TaxYear,
                r.Form,
                r.Asa,
                r.MbrNo,
                r.MbrSub);
        }
    }

    private static string NormalizeKey(string? value) => (value ?? string.Empty).Trim();

    private static LocalTaxDetail NormalizeRecordForLocal(TaxDetailRecord r)
    {
        var local = LocalTaxDetail.FromRecord(r);
        local.TaxYear = NormalizeKey(local.TaxYear);
        local.Form = NormalizeKey(local.Form);
        local.Asa = NormalizeKey(local.Asa);
        local.MbrSub = NormalizeKey(local.MbrSub);
        
        // Normalize all string fields to prevent NULL constraint violations
        local.SsiDc = NormalizeKey(local.SsiDc);
        local.BorrName = NormalizeKey(local.BorrName);
        local.BorrAddr = NormalizeKey(local.BorrAddr);
        local.BorrAddrX = NormalizeKey(local.BorrAddrX);
        local.BorrCity = NormalizeKey(local.BorrCity);
        local.BorrState = NormalizeKey(local.BorrState);
        local.Errors = NormalizeKey(local.Errors);
        local.ReportToIrs = NormalizeKey(local.ReportToIrs);
        local.NonRptReason = NormalizeKey(local.NonRptReason);
        local.CorrIn = NormalizeKey(local.CorrIn);
        local.Foreign = NormalizeKey(local.Foreign);
        local.ChangeDate = NormalizeKey(local.ChangeDate);
        local.DteAqr = NormalizeKey(local.DteAqr);
        local.PrDesc = NormalizeKey(local.PrDesc);
        local.OrigDate = NormalizeKey(local.OrigDate);
        local.SecSame = NormalizeKey(local.SecSame);
        local.SecAddr = NormalizeKey(local.SecAddr);
        local.SecDesc = NormalizeKey(local.SecDesc);
        local.SecOther = NormalizeKey(local.SecOther);
        local.MtgAcqDt = NormalizeKey(local.MtgAcqDt);
        
        local.UpdatedAt = DateTime.UtcNow;
        return local;
    }

    private static LocalTaxAudit NormalizeLocalAuditRecord(TaxDetailRecord r)
    {
        var audit = LocalTaxAudit.FromRecord(r);
        audit.TaxYear = NormalizeKey(audit.TaxYear);
        audit.Form = NormalizeKey(audit.Form);
        audit.Asa = NormalizeKey(audit.Asa);
        audit.MbrSub = NormalizeKey(audit.MbrSub);
        audit.SsiDc = NormalizeKey(audit.SsiDc);
        audit.BorrName = NormalizeKey(audit.BorrName);
        audit.BorrAddr = NormalizeKey(audit.BorrAddr);
        audit.BorrAddrX = NormalizeKey(audit.BorrAddrX);
        audit.BorrCity = NormalizeKey(audit.BorrCity);
        audit.BorrState = NormalizeKey(audit.BorrState);
        audit.Errors = NormalizeKey(audit.Errors);
        audit.ReportToIrs = NormalizeKey(audit.ReportToIrs);
        audit.CorrIn = NormalizeKey(audit.CorrIn);
        audit.Foreign = NormalizeKey(audit.Foreign);
        audit.ChangeDate = NormalizeKey(audit.ChangeDate);
        audit.DteAqr = NormalizeKey(audit.DteAqr);
        audit.PrDesc = NormalizeKey(audit.PrDesc);
        audit.OrigDate = NormalizeKey(audit.OrigDate);
        audit.SecSame = NormalizeKey(audit.SecSame);
        audit.SecAddr = NormalizeKey(audit.SecAddr);
        audit.SecDesc = NormalizeKey(audit.SecDesc);
        audit.SecOther = NormalizeKey(audit.SecOther);
        audit.MtgAcqDt = NormalizeKey(audit.MtgAcqDt);
        audit.AuditCreatedAt = DateTime.UtcNow;
        return audit;
    }

    private async Task InsertDetailAsync(OdbcConnection conn, TaxDetailRecord r)
    {
        var sql = $@"INSERT INTO {_lib}/TXRDTL
                     (TAXYR,FORM,ASA,MBRNO,MBRSUB,SSIDN,BNM,BAD,BADX,BCTY,BST,BZP,
                      SSIDC,INTPD,POINTS,INTERN,ERNWTH,COMPEN,RENTS,MEDPAY,LGLPAY,OTHER,
                      WTHHELD,ERRORS,RPT_TO_IRS,CORRIN,ORIGDATE,SECSAME,SECADDR,SECDESC,
                      SECOTHER,SECNUM,MTGACQDT,UNPPRN,FMVAL,DTEAQR,PRDESC,FOREGN,DEPT,CHANGE_DT)
                     VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)";
        await using var cmd = new OdbcCommand(sql, conn);
        BindDetailParams(cmd, r);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task UpdateDetailAsync(OdbcConnection conn, TaxDetailRecord r)
    {
        var sql = $@"UPDATE {_lib}/TXRDTL SET
                     BNM=?,BAD=?,BADX=?,BCTY=?,BST=?,BZP=?,SSIDC=?,SSIDN=?,
                     INTPD=?,POINTS=?,INTERN=?,ERNWTH=?,COMPEN=?,RENTS=?,MEDPAY=?,LGLPAY=?,OTHER=?,
                     WTHHELD=?,ERRORS=?,RPT_TO_IRS=?,CORRIN=?,ORIGDATE=?,SECSAME=?,SECADDR=?,SECDESC=?,
                     SECOTHER=?,SECNUM=?,MTGACQDT=?,UNPPRN=?,FMVAL=?,DTEAQR=?,PRDESC=?,FOREGN=?,
                     DEPT=?,CHANGE_DT=?
                     WHERE TAXYR=? AND FORM=? AND ASA=? AND MBRNO=? AND MBRSUB=?";
        await using var cmd = new OdbcCommand(sql, conn);
        cmd.Parameters.AddWithValue("?", r.BorrName);
        cmd.Parameters.AddWithValue("?", r.BorrAddr);
        cmd.Parameters.AddWithValue("?", r.BorrAddrX);
        cmd.Parameters.AddWithValue("?", r.BorrCity);
        cmd.Parameters.AddWithValue("?", r.BorrState);
        cmd.Parameters.AddWithValue("?", r.BorrZip);
        cmd.Parameters.AddWithValue("?", r.SsiDc);
        cmd.Parameters.AddWithValue("?", r.SsiDn);
        cmd.Parameters.AddWithValue("?", r.IntPd);
        cmd.Parameters.AddWithValue("?", r.Points);
        cmd.Parameters.AddWithValue("?", r.InterN);
        cmd.Parameters.AddWithValue("?", r.ErnWth);
        cmd.Parameters.AddWithValue("?", r.Compen);
        cmd.Parameters.AddWithValue("?", r.Rents);
        cmd.Parameters.AddWithValue("?", r.MedPay);
        cmd.Parameters.AddWithValue("?", r.LglPay);
        cmd.Parameters.AddWithValue("?", r.Other);
        cmd.Parameters.AddWithValue("?", r.WthHeld);
        cmd.Parameters.AddWithValue("?", r.Errors);
        cmd.Parameters.AddWithValue("?", r.ReportToIrs);
        cmd.Parameters.AddWithValue("?", r.CorrIn);
        cmd.Parameters.AddWithValue("?", r.OrigDate);
        cmd.Parameters.AddWithValue("?", r.SecSame);
        cmd.Parameters.AddWithValue("?", r.SecAddr);
        cmd.Parameters.AddWithValue("?", r.SecDesc);
        cmd.Parameters.AddWithValue("?", r.SecOther);
        cmd.Parameters.AddWithValue("?", r.SecNum);
        cmd.Parameters.AddWithValue("?", r.MtgAcqDt);
        cmd.Parameters.AddWithValue("?", r.UnpPrn);
        cmd.Parameters.AddWithValue("?", r.FmVal);
        cmd.Parameters.AddWithValue("?", r.DteAqr);
        cmd.Parameters.AddWithValue("?", r.PrDesc);
        cmd.Parameters.AddWithValue("?", r.Foreign);
        cmd.Parameters.AddWithValue("?", r.Dept);
        cmd.Parameters.AddWithValue("?", r.ChangeDate);
        // WHERE key params – same type rules as SELECT
        cmd.Parameters.AddWithValue("?", int.Parse(r.TaxYear));                         // TAXYR 4S 0
        cmd.Parameters.AddWithValue("?", r.Form.Trim().PadRight(9));                    // FORM  9A
        cmd.Parameters.AddWithValue("?", r.Asa.Trim().PadRight(3));                     // ASA   3A
        cmd.Parameters.AddWithValue("?", r.MbrNo);                                      // MBRNO 11S 0
        cmd.Parameters.AddWithValue("?", (r.MbrSub?.Trim() ?? "").PadRight(3));         // MBRSUB 3A
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertAuditAsync(OdbcConnection conn, TaxDetailRecord r)
    {
        var sql = $@"INSERT INTO {_lib}/TXRAUD
                     (TAXYR,FORM,ASA,MBRNO,MBRSUB,SSIDN,BNM,BAD,BADX,BCTY,BST,BZP,
                      SSIDC,INTPD,POINTS,INTERN,ERNWTH,COMPEN,RENTS,MEDPAY,LGLPAY,OTHER,
                      WTHHELD,ERRORS,RPT_TO_IRS,CORRIN,ORIGDATE,SECSAME,SECADDR,SECDESC,
                      SECOTHER,SECNUM,MTGACQDT,UNPPRN,FMVAL,DTEAQR,PRDESC,FOREGN,DEPT,CHANGE_DT)
                     VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)";
        await using var cmd = new OdbcCommand(sql, conn);
        BindDetailParams(cmd, r);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void BindDetailParams(OdbcCommand cmd, TaxDetailRecord r)
    {
        cmd.Parameters.AddWithValue("?", r.TaxYear);
        cmd.Parameters.AddWithValue("?", r.Form);
        cmd.Parameters.AddWithValue("?", r.Asa);
        cmd.Parameters.AddWithValue("?", r.MbrNo);
        cmd.Parameters.AddWithValue("?", r.MbrSub);
        cmd.Parameters.AddWithValue("?", r.SsiDn);
        cmd.Parameters.AddWithValue("?", r.BorrName);
        cmd.Parameters.AddWithValue("?", r.BorrAddr);
        cmd.Parameters.AddWithValue("?", r.BorrAddrX);
        cmd.Parameters.AddWithValue("?", r.BorrCity);
        cmd.Parameters.AddWithValue("?", r.BorrState);
        cmd.Parameters.AddWithValue("?", r.BorrZip);
        cmd.Parameters.AddWithValue("?", r.SsiDc);
        cmd.Parameters.AddWithValue("?", r.IntPd);
        cmd.Parameters.AddWithValue("?", r.Points);
        cmd.Parameters.AddWithValue("?", r.InterN);
        cmd.Parameters.AddWithValue("?", r.ErnWth);
        cmd.Parameters.AddWithValue("?", r.Compen);
        cmd.Parameters.AddWithValue("?", r.Rents);
        cmd.Parameters.AddWithValue("?", r.MedPay);
        cmd.Parameters.AddWithValue("?", r.LglPay);
        cmd.Parameters.AddWithValue("?", r.Other);
        cmd.Parameters.AddWithValue("?", r.WthHeld);
        cmd.Parameters.AddWithValue("?", r.Errors);
        cmd.Parameters.AddWithValue("?", r.ReportToIrs);
        cmd.Parameters.AddWithValue("?", r.CorrIn);
        cmd.Parameters.AddWithValue("?", r.OrigDate);
        cmd.Parameters.AddWithValue("?", r.SecSame);
        cmd.Parameters.AddWithValue("?", r.SecAddr);
        cmd.Parameters.AddWithValue("?", r.SecDesc);
        cmd.Parameters.AddWithValue("?", r.SecOther);
        cmd.Parameters.AddWithValue("?", r.SecNum);
        cmd.Parameters.AddWithValue("?", r.MtgAcqDt);
        cmd.Parameters.AddWithValue("?", r.UnpPrn);
        cmd.Parameters.AddWithValue("?", r.FmVal);
        cmd.Parameters.AddWithValue("?", r.DteAqr);
        cmd.Parameters.AddWithValue("?", r.PrDesc);
        cmd.Parameters.AddWithValue("?", r.Foreign);
        cmd.Parameters.AddWithValue("?", r.Dept);
        cmd.Parameters.AddWithValue("?", r.ChangeDate);
    }
}

/// <summary>
/// Implements IReportService.
/// Delegates TX9530/TX9531/TX9532/TX9534/TX9591R to IBM i via ODBC CALL.
/// </summary>
public sealed class ReportService : IReportService
{
    private readonly IIBMiService _ibmi;
    private readonly string _cs;
    private readonly string _lib;
    private readonly LocalDbContext _db;
    private readonly ILogger<ReportService> _logger;

    public ReportService(IIBMiService ibmi, IConfiguration cfg, LocalDbContext db, ILogger<ReportService> logger)
    {
        _ibmi   = ibmi;
        _cs     = cfg.GetConnectionString("IBMi")!;
        _lib    = cfg["IBMiSettings:Library"] ?? "TXLIB";
        _db     = db;
        _logger = logger;
    }

    public Task PrintDetailReportAsync(string taxYear, string formName,
                                        IEnumerable<string> associations, bool selectAll)
    {
        var pgm = formName.Trim() is "1099-MISC" or "1099-NEC" ? "TX9534" : "TX9530";
        return CallAsync(pgm, taxYear, formName, associations, selectAll);
    }

    public async Task<IList<TaxDetailRecord>> GetDetailReportAsync(
        string taxYear, string formName, IEnumerable<string> associations, bool selectAll)
    {
        var assocList = associations.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var localRows = await QueryLocalAsync(taxYear, formName, assocList, selectAll);

        if (localRows.Count > 0)
        {
            return localRows
                .OrderBy(r => r.Asa)
                .ThenBy(r => r.MbrNo)
                .ThenBy(r => r.MbrSub)
                .ToList();
        }

        try
        {
            var ibmiRows = await QueryIbmiAsync(taxYear, formName, assocList, selectAll);
            var merged = ibmiRows.ToDictionary(GetDetailKey, StringComparer.OrdinalIgnoreCase);
            foreach (var local in localRows)
            {
                var key = GetDetailKey(local);
                if (merged.TryGetValue(key, out var ibmi)
                    && string.IsNullOrWhiteSpace(local.NonRptReason)
                    && !string.IsNullOrWhiteSpace(ibmi.NonRptReason))
                {
                    local.NonRptReason = ibmi.NonRptReason;
                }

                merged[key] = local;
            }

            return merged.Values
                .OrderBy(r => r.Asa)
                .ThenBy(r => r.MbrNo)
                .ThenBy(r => r.MbrSub)
                .ToList();
        }
        catch (OdbcException ex)
        {
            _logger.LogWarning(ex, "Falling back to SQLite detail report for {TaxYear} {FormName}.", taxYear, formName);
            return localRows
                .OrderBy(r => r.Asa)
                .ThenBy(r => r.MbrNo)
                .ThenBy(r => r.MbrSub)
                .ToList();
        }
    }

    public async Task<(int TotalCount, IList<DetailReportAssociationSummary> Associations)> GetDetailReportSummaryAsync(
        string taxYear, string formName, IEnumerable<string> associations, bool selectAll)
    {
        var assocList = associations.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var localQuery = BuildLocalReportQuery(taxYear, formName, assocList, selectAll);
        var localTotalCount = await localQuery.CountAsync();

        if (localTotalCount > 0)
        {
            var localAssociations = await localQuery
                .GroupBy(d => d.Asa)
                .Select(g => new DetailReportAssociationSummary
                {
                    AssociationCode = g.Key,
                    RecordCount = g.Count()
                })
                .OrderBy(g => g.AssociationCode)
                .ToListAsync();

            return (localTotalCount, localAssociations);
        }

        return await QueryIbmiDetailReportSummaryAsync(taxYear, formName, assocList, selectAll);
    }

    public async Task<PagedResult<TaxDetailRecord>> GetDetailReportPageAsync(
        string taxYear, string formName, IEnumerable<string> associations, bool selectAll,
        int pageNumber, int pageSize, TaxDetailListMode mode)
    {
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Max(1, pageSize);

        var assocList = associations.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Error report should merge latest staged validation output from SQLite with IBM i errors.
        // Local rows override IBM i rows with the same key.
        if (mode == TaxDetailListMode.Error)
        {
            // For error reports, use ONLY local SQLite errors (fastest approach)
            var localErrorEntities = await BuildLocalReportQuery(taxYear, formName, assocList, selectAll)
                .Where(d => d.Errors == "Y")
                .ToListAsync();

            var localErrorRows = localErrorEntities
                .OrderByDescending(d => d.UpdatedAt)
                .ThenBy(d => d.Asa)
                .ThenBy(d => d.MbrNo)
                .ThenBy(d => d.MbrSub)
                .Select(d => d.ToRecord())
                .ToList();

            // Return local errors immediately without IBM i merge
            return new PagedResult<TaxDetailRecord>
            {
                Items = localErrorRows
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .ToList(),
                PageNumber = pageNumber,
                PageSize = pageSize,
                TotalCount = localErrorRows.Count
            };
        }

        var localRows = await ApplyReportMode(BuildLocalReportQuery(taxYear, formName, assocList, selectAll), mode)
            .Select(d => d.ToRecord())
            .ToListAsync();

        if (localRows.Count > 0)
        {
            var orderedLocalRows = localRows
                .OrderBy(d => d.Asa)
                .ThenBy(d => d.MbrNo)
                .ThenBy(d => d.MbrSub)
                .ToList();

            return new PagedResult<TaxDetailRecord>
            {
                Items = orderedLocalRows
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .ToList(),
                PageNumber = pageNumber,
                PageSize = pageSize,
                TotalCount = orderedLocalRows.Count
            };
        }

        var ibmiPage = await QueryIbmiReportPageAsync(taxYear, formName, assocList, selectAll, pageNumber, pageSize, mode);
        if (ibmiPage.TotalCount > 0 || ibmiPage.Items.Count > 0)
        {
            return ibmiPage;
        }

        try
        {
            var ibmiRows = await QueryIbmiAsync(taxYear, formName, assocList, selectAll);
            var filteredIbmiRows = ApplyReportMode(ibmiRows, mode);

            // Merge IBM i and local rows by detail key, preferring local rows for staged edits.
            var merged = filteredIbmiRows.ToDictionary(GetDetailKey, StringComparer.OrdinalIgnoreCase);
            foreach (var local in localRows)
            {
                var key = GetDetailKey(local);
                if (merged.TryGetValue(key, out var ibmi)
                    && string.IsNullOrWhiteSpace(local.NonRptReason)
                    && !string.IsNullOrWhiteSpace(ibmi.NonRptReason))
                {
                    local.NonRptReason = ibmi.NonRptReason;
                }

                merged[key] = local;
            }

            var orderedRows = merged.Values
                .OrderBy(d => d.Asa)
                .ThenBy(d => d.MbrNo)
                .ThenBy(d => d.MbrSub)
                .ToList();

            return new PagedResult<TaxDetailRecord>
            {
                Items = orderedRows
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .ToList(),
                PageNumber = pageNumber,
                PageSize = pageSize,
                TotalCount = orderedRows.Count
            };
        }
        catch (OdbcException ex)
        {
            _logger.LogWarning(ex, "IBM i paged detail source unavailable for {TaxYear} {FormName}; using SQLite rows.", taxYear, formName);

            var orderedLocalRows = localRows
                .OrderBy(d => d.Asa)
                .ThenBy(d => d.MbrNo)
                .ThenBy(d => d.MbrSub)
                .ToList();

            return new PagedResult<TaxDetailRecord>
            {
                Items = orderedLocalRows
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .ToList(),
                PageNumber = pageNumber,
                PageSize = pageSize,
                TotalCount = orderedLocalRows.Count
            };
        }
    }

    public async Task<IList<string>> GetDistinctAssociationsAsync(
        string taxYear, string formName, IList<string> associations, bool selectAll, TaxDetailListMode mode)
    {
        var assocList = associations.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var localAssociations = await GetDistinctAssociationsLocalAsync(taxYear, formName, assocList, selectAll, mode);

        if (localAssociations.Count > 0)
        {
            return localAssociations
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .OrderBy(a => a)
                .ToList();
        }

        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assoc in localAssociations)
            merged.Add(assoc);

        try
        {
            var ibmiAssociations = await GetDistinctAssociationsIbmiAsync(taxYear, formName, assocList, selectAll, mode);
            foreach (var assoc in ibmiAssociations)
                merged.Add(assoc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IBM i distinct associations unavailable for {TaxYear} {FormName}; using SQLite set.", taxYear, formName);
        }

        return merged
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .OrderBy(a => a)
            .ToList();
    }

    public async Task<IList<string>> GetDistinctAssociationsWithErrorsAsync(string taxYear, string formName)
    {
        // For error reports, prioritize local SQLite errors (very fast)
        // Only query IBM i as fallback if there are no local errors
        var localAssociations = await GetDistinctAssociationsWithErrorsLocalAsync(taxYear, formName);
        
        // If we have local errors, return immediately without waiting for IBM i
        if (localAssociations.Count > 0)
        {
            _logger.LogInformation("ErrorReport: Using {Count} local error associations", localAssociations.Count);
            return localAssociations;
        }

        // Fallback: try IBM i only if no local errors exist
        try
        {
            var ibmiAssociations = await GetDistinctAssociationsWithErrorsIbmiAsync(taxYear, formName);
            if (ibmiAssociations.Count > 0)
            {
                _logger.LogInformation("ErrorReport: Using {Count} IBM i error associations (no local errors found)", ibmiAssociations.Count);
                return ibmiAssociations;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IBM i associations-with-errors unavailable for {TaxYear} {FormName}; using SQLite set.", taxYear, formName);
        }

        // No errors found in either source
        return new List<string>();
    }

    private async Task<IList<string>> GetDistinctAssociationsIbmiAsync(
        string taxYear, string formName, IList<string> associations, bool selectAll, TaxDetailListMode mode)
    {
        if (!int.TryParse(taxYear, out var taxYearInt))
            return Array.Empty<string>();

        var sql = new System.Text.StringBuilder($"SELECT DISTINCT ASA FROM {_lib}/TXRDTL WHERE TAXYR=? AND FORM=?");

        if (!selectAll && associations.Count > 0)
        {
            sql.Append(" AND ASA IN (");
            sql.Append(string.Join(",", associations.Select(_ => "?")));
            sql.Append(")");
        }

        if (mode == TaxDetailListMode.Error)
            sql.Append(" AND ERRORS='Y'");
        else if (mode == TaxDetailListMode.Exclusion)
            sql.Append(" AND RPT_TO_IRS<>'Y'");

        sql.Append(" ORDER BY ASA");

        var result = new List<string>();
        await using var conn = new OdbcConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new OdbcCommand(sql.ToString(), conn);
        cmd.Parameters.AddWithValue("?", taxYearInt);
        cmd.Parameters.AddWithValue("?", formName.Trim().PadRight(9));
        foreach (var assoc in associations)
            cmd.Parameters.AddWithValue("?", assoc.PadRight(3));

        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var asa = rdr.IsDBNull(0) ? string.Empty : rdr.GetString(0).Trim();
            if (!string.IsNullOrEmpty(asa))
                result.Add(asa);
        }

        return result;
    }

    private async Task<IList<string>> GetDistinctAssociationsLocalAsync(
        string taxYear, string formName, IList<string> associations, bool selectAll, TaxDetailListMode mode)
    {
        var query = BuildLocalReportQuery(taxYear, formName, associations, selectAll);
        query = ApplyReportMode(query, mode);

        return await query
            .Select(d => d.Asa)
            .Where(asa => asa != null && asa.Trim() != "")
            .Distinct()
            .OrderBy(asa => asa)
            .ToListAsync();
    }

    private async Task<(int TotalCount, IList<DetailReportAssociationSummary> Associations)> QueryIbmiDetailReportSummaryAsync(
        string taxYear, string formName, IList<string> associations, bool selectAll)
    {
        if (!int.TryParse(taxYear, out var taxYearInt))
            return (0, Array.Empty<DetailReportAssociationSummary>());

        var assocClause = string.Empty;
        if (!selectAll && associations.Count > 0)
        {
            assocClause = $" AND ASA IN ({string.Join(",", associations.Select(_ => "?"))})";
        }

        var countSql = $@"SELECT COUNT(*)
                          FROM {_lib}/TXRDTL
                          WHERE TAXYR=? AND FORM=?{assocClause}";

        var summarySql = $@"SELECT ASA, COUNT(*)
                            FROM {_lib}/TXRDTL
                            WHERE TAXYR=? AND FORM=?{assocClause}
                            GROUP BY ASA
                            ORDER BY ASA";

        var totalCount = 0;
        var associationsSummary = new List<DetailReportAssociationSummary>();

        await using var conn = new OdbcConnection(_cs);
        await conn.OpenAsync();

        await using (var countCmd = new OdbcCommand(countSql, conn))
        {
            AddReportParameters(countCmd, taxYearInt, formName, associations);
            totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync() ?? 0);
        }

        await using (var summaryCmd = new OdbcCommand(summarySql, conn))
        {
            AddReportParameters(summaryCmd, taxYearInt, formName, associations);
            await using var rdr = await summaryCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                associationsSummary.Add(new DetailReportAssociationSummary
                {
                    AssociationCode = rdr.IsDBNull(0) ? string.Empty : rdr.GetString(0).Trim(),
                    RecordCount = rdr.IsDBNull(1) ? 0 : Convert.ToInt32(rdr.GetValue(1))
                });
            }
        }

        return (totalCount, associationsSummary);
    }

    private async Task<IList<string>> GetDistinctAssociationsWithErrorsIbmiAsync(string taxYear, string formName)
    {
        if (!int.TryParse(taxYear, out var taxYearInt))
            return Array.Empty<string>();

        var sql = $@"SELECT DISTINCT ASA FROM {_lib}/TXRDTL 
                    WHERE TAXYR=? AND FORM=? AND ERRORS='Y' 
                    ORDER BY ASA";

        var result = new List<string>();
        await using var conn = new OdbcConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new OdbcCommand(sql, conn);
        cmd.Parameters.AddWithValue("?", taxYearInt);
        cmd.Parameters.AddWithValue("?", formName.Trim().PadRight(9));
        cmd.CommandTimeout = 30; // 30-second timeout to prevent hangs

        try
        {
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var asa = rdr.IsDBNull(0) ? string.Empty : rdr.GetString(0).Trim();
                if (!string.IsNullOrEmpty(asa))
                    result.Add(asa);
            }
        }
        catch (OdbcException ex)
        {
            _logger.LogWarning(ex, "IBM i error associations query timed out or failed");
            return Array.Empty<string>();
        }

        return result;
    }

    private async Task<IList<string>> GetDistinctAssociationsWithErrorsLocalAsync(string taxYear, string formName)
    {
        var normalizedYear = (taxYear ?? string.Empty).Trim();
        var normalizedForm = (formName ?? string.Empty).Trim();

        return await _db.TaxDetails
            .Where(d => d.TaxYear == normalizedYear && d.Form == normalizedForm && d.Errors == "Y")
            .Select(d => d.Asa)
            .Where(asa => asa != null && asa.Trim() != "")
            .Distinct()
            .OrderBy(asa => asa)
            .ToListAsync();
    }

    public Task PrintExclusionReportAsync(string taxYear, string formName,
                                           IEnumerable<string> associations, bool selectAll)
        => CallAsync("TX9531", taxYear, formName, associations, selectAll);

    public Task PrintErrorReportAsync(string taxYear, string formName,
                                       IEnumerable<string> associations, bool selectAll)
        => CallAsync("TX9532", taxYear, formName, associations, selectAll);

    public async Task PrintLettersAsync(string taxYear,
                                         IEnumerable<string> associations, bool selectAll)
    {
        var ctrl    = new Models.TaxControlRecord { TaxYear = taxYear }.ToControlString();
        var assnStr = BuildAssocParam(associations.ToList(), selectAll);
        try
        {
            await _ibmi.ExecuteProgramAsync("TX9591R", null, ctrl, assnStr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TX9591R unavailable for letter processing (TaxYear={TaxYear}). Falling back to web-only completion message.",
                taxYear);
        }
    }

    public async Task<IList<TaxDetailRecord>> GetLetterCandidatesAsync(
        string taxYear, IEnumerable<string> associations, bool selectAll)
    {
        var assocList = associations
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        const string letterForm = "1098";
        var localRows = ApplyLetterRules(await QueryLocalAsync(taxYear, letterForm, assocList, selectAll));

        // For letter candidates, prioritize local SQLite data (fastest)
        // Return immediately without waiting for IBM i merge
        if (localRows.Count > 0)
        {
            _logger.LogInformation("LetterCandidates: Using {Count} local letter candidates", localRows.Count);
            return localRows
                .OrderBy(r => r.Asa)
                .ThenBy(r => r.MbrNo)
                .ThenBy(r => r.MbrSub)
                .ToList();
        }

        // Fallback: query IBM i only if no local candidates
        try
        {
            var ibmiRows = ApplyLetterRules(await QueryIbmiAsync(taxYear, letterForm, assocList, selectAll));
            if (ibmiRows.Count > 0)
            {
                _logger.LogInformation("LetterCandidates: Using {Count} IBM i letter candidates (no local found)", ibmiRows.Count);
                return ibmiRows
                    .OrderBy(r => r.Asa)
                    .ThenBy(r => r.MbrNo)
                    .ThenBy(r => r.MbrSub)
                    .ToList();
            }
        }
        catch (OdbcException ex)
        {
            _logger.LogWarning(ex,
                "IBM i letter-candidate source unavailable for {TaxYear}; using SQLite rows.",
                taxYear);
        }

        // No candidates found in either source
        return new List<TaxDetailRecord>();
    }

    public async Task<PagedResult<TaxDetailRecord>> GetLetterCandidatesPageAsync(
        string taxYear, IEnumerable<string> associations, bool selectAll, int pageNumber, int pageSize)
    {
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Max(1, pageSize);

        var rows = await GetLetterCandidatesAsync(taxYear, associations, selectAll);
        return new PagedResult<TaxDetailRecord>
        {
            Items = rows
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToList(),
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = rows.Count
        };
    }

    private async Task CallAsync(string pgm, string taxYear, string formName,
                                  IEnumerable<string> associations, bool selectAll)
    {
        var ctrl    = new Models.TaxControlRecord { TaxYear = taxYear }.ToControlString();
        var assnStr = BuildAssocParam(associations.ToList(), selectAll);
        await _ibmi.ExecuteProgramAsync(pgm, null, ctrl, formName.PadRight(9), assnStr);
    }

    private async Task<List<TaxDetailRecord>> QueryIbmiAsync(
        string taxYear, string formName, IList<string> associations, bool selectAll)
    {
        if (!int.TryParse(taxYear, out var taxYearInt))
        {
            return new List<TaxDetailRecord>();
        }

        var sql = new System.Text.StringBuilder($@"SELECT TAXYR,FORM,ASA,MBRNO,MBRSUB,SSIDN,BNM,BAD,BADX,BCTY,BST,BZP,
                            SSIDC,INTPD,POINTS,INTERN,ERNWTH,COMPEN,RENTS,MEDPAY,LGLPAY,OTHER,
                    WTHHELD,ERRORS,RPT_TO_IRS,NONRPT_RSN,CORRIN,ORIGDATE,SECSAME,SECADDR,SECDESC,
                            SECOTHER,SECNUM,MTGACQDT,UNPPRN,FMVAL,DTEAQR,PRDESC,FOREGN,DEPT
                     FROM {_lib}/TXRDTL
                     WHERE TAXYR=? AND FORM=?");

        if (!selectAll && associations.Count > 0)
        {
            sql.Append(" AND ASA IN (");
            sql.Append(string.Join(",", associations.Select(_ => "?")));
            sql.Append(")");
        }

        sql.Append(" ORDER BY ASA, MBRNO, MBRSUB");

        var rows = new List<TaxDetailRecord>();
        await using var conn = new OdbcConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new OdbcCommand(sql.ToString(), conn);
        cmd.Parameters.AddWithValue("?", taxYearInt);
        cmd.Parameters.AddWithValue("?", formName.Trim().PadRight(9));
        foreach (var assoc in associations)
        {
            cmd.Parameters.AddWithValue("?", assoc.PadRight(3));
        }

        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            rows.Add(MapReportRecord(rdr));
        }

        return rows;
    }

    private async Task<List<TaxDetailRecord>> QueryLocalAsync(
        string taxYear, string formName, IList<string> associations, bool selectAll)
    {
        return await BuildLocalReportQuery(taxYear, formName, associations, selectAll)
            .Select(d => d.ToRecord())
            .ToListAsync();
    }

    private IQueryable<LocalTaxDetail> BuildLocalReportQuery(
        string taxYear, string formName, IList<string> associations, bool selectAll)
    {
        var query = _db.TaxDetails.Where(d =>
            d.TaxYear == NormalizeReportText(taxYear) &&
            d.Form == NormalizeReportText(formName));

        if (!selectAll && associations.Count > 0)
        {
            query = query.Where(d => associations.Contains(d.Asa));
        }

        return query;
    }

    private static IQueryable<LocalTaxDetail> ApplyReportMode(IQueryable<LocalTaxDetail> query, TaxDetailListMode mode)
        => mode switch
        {
            TaxDetailListMode.Error => query.Where(d => d.Errors == "Y"),
            TaxDetailListMode.Exclusion => query.Where(d => d.ReportToIrs != "Y"),
            _ => query
        };

    private static List<TaxDetailRecord> ApplyReportMode(IEnumerable<TaxDetailRecord> rows, TaxDetailListMode mode)
        => mode switch
        {
            TaxDetailListMode.Error => rows.Where(d => d.Errors == "Y").ToList(),
            TaxDetailListMode.Exclusion => rows.Where(d => d.ReportToIrs != "Y").ToList(),
            _ => rows.ToList()
        };

    private static List<TaxDetailRecord> ApplyLetterRules(IEnumerable<TaxDetailRecord> rows)
    {
        // Mirrors the web replication of TX9591R selection intent:
        // only non-reported 1098 rows with positive interest paid.
        return rows
            .Where(d => string.Equals(d.Form?.Trim(), "1098", StringComparison.OrdinalIgnoreCase))
            .Where(d => string.Equals(d.ReportToIrs?.Trim(), "N", StringComparison.OrdinalIgnoreCase))
            .Where(d => d.IntPd > 0)
            .ToList();
    }

    private async Task<PagedResult<TaxDetailRecord>> QueryIbmiReportPageAsync(
        string taxYear, string formName, IList<string> associations, bool selectAll,
        int pageNumber, int pageSize, TaxDetailListMode mode)
    {
        if (!int.TryParse(taxYear, out var taxYearInt))
        {
            return new PagedResult<TaxDetailRecord>
            {
                PageNumber = pageNumber,
                PageSize = pageSize,
                TotalCount = 0
            };
        }

        var filterClause = mode switch
        {
            TaxDetailListMode.Error => " AND ERRORS='Y'",
            TaxDetailListMode.Exclusion => " AND (RPT_TO_IRS <> 'Y' OR RPT_TO_IRS IS NULL)",
            _ => string.Empty
        };

        var assocClause = string.Empty;
        if (!selectAll && associations.Count > 0)
        {
            assocClause = $" AND ASA IN ({string.Join(",", associations.Select(_ => "?"))})";
        }

        var countSql = $@"SELECT COUNT(*)
                          FROM {_lib}/TXRDTL
                          WHERE TAXYR=? AND FORM=?{assocClause}{filterClause}";

        var pageSql = $@"SELECT TAXYR,FORM,ASA,MBRNO,MBRSUB,SSIDN,BNM,BAD,BADX,BCTY,BST,BZP,
                                SSIDC,INTPD,POINTS,INTERN,ERNWTH,COMPEN,RENTS,MEDPAY,LGLPAY,OTHER,
                        WTHHELD,ERRORS,RPT_TO_IRS,NONRPT_RSN,CORRIN,ORIGDATE,SECSAME,SECADDR,SECDESC,
                                SECOTHER,SECNUM,MTGACQDT,UNPPRN,FMVAL,DTEAQR,PRDESC,FOREGN,DEPT
                         FROM {_lib}/TXRDTL
                         WHERE TAXYR=? AND FORM=?{assocClause}{filterClause}
                         ORDER BY ASA, MBRNO, MBRSUB
                         OFFSET {(pageNumber - 1) * pageSize} ROWS FETCH NEXT {pageSize} ROWS ONLY";

        var totalCount = 0;
        var rows = new List<TaxDetailRecord>();

        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();

            await using (var countCmd = new OdbcCommand(countSql, conn))
            {
                AddReportParameters(countCmd, taxYearInt, formName, associations);
                totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync() ?? 0);
            }

            await using var pageCmd = new OdbcCommand(pageSql, conn);
            AddReportParameters(pageCmd, taxYearInt, formName, associations);

            await using var rdr = await pageCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                rows.Add(MapReportRecord(rdr));
            }
        }
        catch (OdbcException ex)
        {
            _logger.LogWarning(ex, "IBM i paged report query failed for {TaxYear} {FormName}.", taxYear, formName);
        }

        return new PagedResult<TaxDetailRecord>
        {
            Items = rows,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    private static void AddReportParameters(OdbcCommand cmd, int taxYearInt, string formName, IList<string> associations)
    {
        cmd.Parameters.AddWithValue("?", taxYearInt);
        cmd.Parameters.AddWithValue("?", formName.Trim().PadRight(9));
        foreach (var assoc in associations)
        {
            cmd.Parameters.AddWithValue("?", assoc.PadRight(3));
        }
    }

    private static TaxDetailRecord MapReportRecord(System.Data.Common.DbDataReader r) => new()
    {
        TaxYear    = SafeReportString(r, 0),
        Form       = SafeReportString(r, 1),
        Asa        = SafeReportString(r, 2),
        MbrNo      = r.IsDBNull(3) ? 0 : r.GetDecimal(3),
        MbrSub     = SafeReportString(r, 4),
        SsiDn      = r.IsDBNull(5) ? 0 : r.GetDecimal(5),
        BorrName   = SafeReportString(r, 6),
        BorrAddr   = SafeReportString(r, 7),
        BorrAddrX  = SafeReportString(r, 8),
        BorrCity   = SafeReportString(r, 9),
        BorrState  = SafeReportString(r, 10),
        BorrZip    = r.IsDBNull(11) ? 0 : r.GetDecimal(11),
        SsiDc      = SafeReportString(r, 12),
        IntPd      = r.IsDBNull(13) ? 0 : r.GetDecimal(13),
        Points     = r.IsDBNull(14) ? 0 : r.GetDecimal(14),
        InterN     = r.IsDBNull(15) ? 0 : r.GetDecimal(15),
        ErnWth     = r.IsDBNull(16) ? 0 : r.GetDecimal(16),
        Compen     = r.IsDBNull(17) ? 0 : r.GetDecimal(17),
        Rents      = r.IsDBNull(18) ? 0 : r.GetDecimal(18),
        MedPay     = r.IsDBNull(19) ? 0 : r.GetDecimal(19),
        LglPay     = r.IsDBNull(20) ? 0 : r.GetDecimal(20),
        Other      = r.IsDBNull(21) ? 0 : r.GetDecimal(21),
        WthHeld    = r.IsDBNull(22) ? 0 : r.GetDecimal(22),
        Errors     = SafeReportString(r, 23),
        ReportToIrs = SafeReportString(r, 24),
        NonRptReason = SafeReportString(r, 25),
        CorrIn     = SafeReportString(r, 26),
        OrigDate   = SafeReportString(r, 27),
        SecSame    = SafeReportString(r, 28),
        SecAddr    = SafeReportString(r, 29),
        SecDesc    = SafeReportString(r, 30),
        SecOther   = SafeReportString(r, 31),
        SecNum     = r.IsDBNull(32) ? 0 : r.GetDecimal(32),
        MtgAcqDt   = SafeReportString(r, 33),
        UnpPrn     = r.IsDBNull(34) ? 0 : r.GetDecimal(34),
        FmVal      = r.IsDBNull(35) ? 0 : r.GetDecimal(35),
        DteAqr     = SafeReportString(r, 36),
        PrDesc     = SafeReportString(r, 37),
        Foreign    = SafeReportString(r, 38),
        Dept       = r.IsDBNull(39) ? 0 : r.GetDecimal(39),
    };

    private static string SafeReportString(System.Data.Common.DbDataReader r, int col)
    {
        if (r.IsDBNull(col)) return string.Empty;
        var val = r.GetValue(col);
        if (val is DateTime dt) return dt.ToString("yyyyMMdd");
        return val.ToString()?.Trim() ?? string.Empty;
    }

    private static string GetDetailKey(TaxDetailRecord record)
    {
        var normalizedMbrNo = ((long)record.MbrNo).ToString();
        return $"{NormalizeReportText(record.TaxYear)}|{NormalizeReportText(record.Form)}|{NormalizeReportText(record.Asa)}|{normalizedMbrNo}|{NormalizeReportText(record.MbrSub)}";
    }

    private static string NormalizeReportText(string? value) => (value ?? string.Empty).Trim();

    private static string BuildAssocParam(IList<string> asns, bool selectAll)
    {
        var ds = new System.Text.StringBuilder(500);
        ds.Append(selectAll ? "ALL" : "   ");
        foreach (var a in asns) ds.Append(a.PadRight(3)[..3]);
        return ds.ToString().PadRight(500)[..500];
    }
}

/// <summary>
/// Implements IExtractService.
/// Replaces tx9560 / tx9561 / tx9562 / tx9563 / tx9565r.
/// </summary>
public sealed class ExtractService : IExtractService
{
    private readonly IIBMiService _ibmi;
    private readonly string _cs;
    private readonly string _lib;
    private readonly ILogger<ExtractService> _logger;
    private readonly bool _goAnywhereEnabled;
    private readonly string _goAnywhereExePath;
    private readonly string _goAnywhereProjectPath;
    private readonly string _goAnywhereUsername;
    private readonly string _goAnywherePassword;
    private readonly string _goAnywhereHost;
    private readonly bool _goAnywhereExecuteOnIBMi;
    
    // Transmitter information from configuration
    private readonly string _xmtrCompanyName;
    private readonly string _xmtrCompanyName2;
    private readonly string _xmtrAddress;
    private readonly string _xmtrCity;
    private readonly string _xmtrState;
    private readonly string _xmtrZip;
    private readonly string _xmtrContactName;
    private readonly string _xmtrContactPhone;
    private readonly string _xmtrContactEmail;
    private readonly string _xmtrTIN;
    private readonly string _xmtrFEI;

    private readonly LocalDbContext _db;
    private bool _tx9565rUnavailable;

    public ExtractService(IIBMiService ibmi, IConfiguration cfg,
                           LocalDbContext db, ILogger<ExtractService> logger)
    {
        _ibmi   = ibmi;
        _cs     = cfg.GetConnectionString("IBMi")!;
        _lib    = cfg["IBMiSettings:Library"] ?? "TXLIB";
        _goAnywhereEnabled = bool.TryParse(cfg["GoAnywhere:Enabled"], out var enabled) && enabled;
        _goAnywhereExePath = cfg["GoAnywhere:ExecutablePath"] ?? "TODO_ADD_GOANYWHERE_EXE_PATH";
        _goAnywhereProjectPath = cfg["GoAnywhere:ProjectPath"] ?? string.Empty;
        // Credentials come from the single IBMiCredentials section; GoAnywhere uses the same account.
        _goAnywhereUsername = cfg["IBMiCredentials:Username"] ?? cfg["GoAnywhere:Username"] ?? string.Empty;
        _goAnywherePassword = cfg["IBMiCredentials:Password"] ?? cfg["GoAnywhere:Password"] ?? string.Empty;
        _goAnywhereHost = cfg["GoAnywhere:Host"] ?? "dev";
        _goAnywhereExecuteOnIBMi = bool.TryParse(cfg["GoAnywhere:ExecuteOnIBMi"], out var execOnIbmi) && execOnIbmi;
        
        // Load transmitter configuration
        _xmtrCompanyName = cfg["Transmitter:CompanyName"] ?? string.Empty;
        _xmtrCompanyName2 = cfg["Transmitter:CompanyName2"] ?? string.Empty;
        _xmtrAddress = cfg["Transmitter:Address"] ?? string.Empty;
        _xmtrCity = cfg["Transmitter:City"] ?? string.Empty;
        _xmtrState = cfg["Transmitter:State"] ?? string.Empty;
        _xmtrZip = cfg["Transmitter:Zip"] ?? string.Empty;
        _xmtrContactName = cfg["Transmitter:ContactName"] ?? string.Empty;
        _xmtrContactPhone = cfg["Transmitter:ContactPhone"] ?? string.Empty;
        _xmtrContactEmail = cfg["Transmitter:ContactEmail"] ?? string.Empty;
        _xmtrTIN = cfg["Transmitter:TransmitterTIN"] ?? string.Empty;
        _xmtrFEI = cfg["Transmitter:TransmitterFEI"] ?? string.Empty;
        
        _db     = db;
        _logger = logger;
    }

    /// <summary>
    /// IBM i DATE/TIMESTAMP columns come back as System.DateTime via the ODBC driver.
    /// This helper converts either a DateTime or a string value to yyyyMMdd safely.
    /// </summary>
    private static string ReadDateColumn(System.Data.Common.DbDataReader rdr, int ordinal)
    {
        var raw = rdr.GetValue(ordinal);
        return raw switch
        {
            DateTime dt => dt.ToString("yyyyMMdd"),
            string s    => s.Trim(),
            _           => raw?.ToString()?.Trim() ?? ""
        };
    }

    public async Task<IList<ExtractControlRecord>> ListExtractsAsync(string taxYear)
    {
        var rows = new List<ExtractControlRecord>();
        var sql = $"SELECT TAXYR,EXT_SEQ,EXT_DESC,EXT_DATE,EXT_SELDAT,XMTR_NAME,XMTR_NAME2 " +
                  $"FROM {_lib}/TXIRST WHERE TAXYR=? ORDER BY EXT_SEQ";
        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            await using var cmd = new OdbcCommand(sql, conn);
            cmd.Parameters.AddWithValue("?", taxYear);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                rows.Add(new ExtractControlRecord
                {
                    TaxYear    = rdr.GetString(0).Trim(),
                    ExtSeq     = rdr.GetInt64(1),
                    ExtDesc    = rdr.GetString(2).Trim(),
                    ExtDate    = rdr.IsDBNull(3) ? "" : ReadDateColumn(rdr, 3),
                    ExtSelDat  = rdr.IsDBNull(4) ? "" : ReadDateColumn(rdr, 4),
                    XmtrName   = rdr.GetString(5).Trim(),
                    XmtrName2  = rdr.GetString(6).Trim()
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list extracts from IBM i for TaxYear={TaxYear}.", taxYear);
        }

        // Merge SQLite local extracts (dedup by ExtSeq)
        var ibmiSeqs = rows.Select(r => r.ExtSeq).ToHashSet();
        var localExtracts = await _db.Extracts
            .Where(e => e.TaxYear == taxYear)
            .ToListAsync();
        foreach (var le in localExtracts.OrderBy(e => e.ExtSeq))
        {
            if (!ibmiSeqs.Contains(le.ExtSeq))
                rows.Add(le.ToRecord());
        }

        return rows.OrderBy(r => r.ExtSeq).ToList();
    }

    public async Task<long> CreateExtractAsync(string taxYear, string description,
                                                    string selectDate, string xmtrName,
                                                    string xmtrName2)
    {
        // Compute next sequence number from both IBM i and SQLite.
        long ibmiMax = 0L;
        try
        {
            var seqSql = $"SELECT COALESCE(MAX(EXT_SEQ),0) FROM {_lib}/TXIRST WHERE TAXYR=?";
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            await using var seqCmd = new OdbcCommand(seqSql, conn);
            seqCmd.Parameters.AddWithValue("?", taxYear);
            ibmiMax = Convert.ToInt64(await seqCmd.ExecuteScalarAsync() ?? 0L);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read max extract sequence from IBM i for TaxYear={TaxYear}.", taxYear);
        }

        long localMax = (await _db.Extracts
            .Where(e => e.TaxYear == taxYear)
            .ToListAsync())
            .Max(e => (long?)e.ExtSeq) ?? 0L;

        long nextSeq = Math.Max(ibmiMax, localMax) + 1L;

        // Save to local SQLite staging store.
        _db.Extracts.Add(new LocalExtract
        {
            TaxYear   = taxYear,
            ExtSeq    = nextSeq,
            ExtDesc   = description,
            ExtDate   = DateTime.UtcNow.ToString("yyyyMMdd"),
            ExtSelDat = selectDate,
            XmtrName  = xmtrName,
            XmtrName2 = xmtrName2,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return nextSeq;
    }

    public async Task ClearExtractAsync(string taxYear, long extSeq)
    {
        // Mirrors TX9561: DELETE from TXIRSB/B3/C/A/F then clear TXIRSX member
        var ctrl = new Models.TaxControlRecord { TaxYear = taxYear }.ToControlString();
        try
        {
            await _ibmi.ExecuteProgramAsync("TX9561", ctrl,
                extSeq.ToString().PadLeft(5));
            _logger.LogInformation(
                "TX9561 executed successfully for TaxYear={TaxYear}, ExtSeq={ExtSeq}",
                taxYear, extSeq);
            return;
        }
#pragma warning disable CS0168
        catch (Exception _)
        {
            // TX9561 unavailable, attempt web fallback
        }
#pragma warning restore CS0168

        // Web fallback: delete extract records directly via SQL
        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();

            // Delete detail records from TXIRSB
            await using var delCmd = new OdbcCommand(
                $"DELETE FROM {_lib}/TXIRSB WHERE TAXYR=? AND EXT_SEQ=?", conn);
            delCmd.Parameters.AddWithValue("?", taxYear.PadRight(4));
            delCmd.Parameters.AddWithValue("?", extSeq.ToString("0"));
            await delCmd.ExecuteNonQueryAsync();

            // Delete header record from TXIRST
            await using var delHdrCmd = new OdbcCommand(
                $"DELETE FROM {_lib}/TXIRST WHERE TAXYR=? AND EXT_SEQ=?", conn);
            delHdrCmd.Parameters.AddWithValue("?", taxYear.PadRight(4));
            delHdrCmd.Parameters.AddWithValue("?", extSeq.ToString("0"));
            var rowsDeleted = await delHdrCmd.ExecuteNonQueryAsync();

            _logger.LogInformation(
                "Extract cleared for TaxYear={TaxYear}, ExtSeq={ExtSeq}",
                taxYear, extSeq);
        }
#pragma warning disable CS0168
        catch (Exception _)
        {
            // Web SQL fallback also failed, will clear local files only
        }
#pragma warning restore CS0168

        // Always clear local file and local database
        var filePath = GetExtractFilePath(taxYear, extSeq);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            _logger.LogInformation("Deleted local extract file: {FilePath}", filePath);
        }

        var localExtract = await _db.Extracts
            .FirstOrDefaultAsync(e => e.TaxYear == taxYear && e.ExtSeq == extSeq);
        if (localExtract is not null)
        {
            localExtract.BRecsT = 0;
            localExtract.ExtDate = DateTime.UtcNow.ToString("yyyyMMdd");
            await _db.SaveChangesAsync();
            _logger.LogInformation("Updated local extract record for TaxYear={TaxYear}, ExtSeq={ExtSeq}", taxYear, extSeq);
        }
    }

    public async Task ClearAllLocalExtractsAsync()
    {
        try
        {
            var allExtracts = await _db.Extracts.ToListAsync();
            _logger.LogInformation("Clearing {Count} local extract records from SQLite", allExtracts.Count);

            _db.Extracts.RemoveRange(allExtracts);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Successfully cleared all {Count} local extract records from SQLite", allExtracts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing all local extracts from SQLite");
            throw;
        }
    }

    public async Task DeleteLocalExtractAsync(string taxYear, long extSeq)
    {
        try
        {
            var localExtract = await _db.Extracts
                .FirstOrDefaultAsync(e => e.TaxYear == taxYear && e.ExtSeq == extSeq);
            
            if (localExtract is null)
            {
                _logger.LogWarning(
                    "Local extract not found in SQLite for deletion. TaxYear={TaxYear}, ExtSeq={ExtSeq}",
                    taxYear, extSeq);
                return;
            }

            _db.Extracts.Remove(localExtract);
            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "Successfully deleted local extract from SQLite. TaxYear={TaxYear}, ExtSeq={ExtSeq}",
                taxYear, extSeq);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Error deleting local extract from SQLite. TaxYear={TaxYear}, ExtSeq={ExtSeq}",
                taxYear, extSeq);
            throw;
        }
    }

    public async Task ForceDeleteExtractFromIBMiAsync(string taxYear, long extSeq)
    {
        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();

            // Delete detail records first
            await using var delDetailCmd = new OdbcCommand(
                $"DELETE FROM {_lib}/TXIRSB WHERE TAXYR=? AND EXT_SEQ=?", conn);
            delDetailCmd.Parameters.AddWithValue("?", taxYear.PadRight(4));
            delDetailCmd.Parameters.AddWithValue("?", extSeq.ToString("0"));
            var detailRowsDeleted = await delDetailCmd.ExecuteNonQueryAsync();

            // Delete header record
            await using var delHdrCmd = new OdbcCommand(
                $"DELETE FROM {_lib}/TXIRST WHERE TAXYR=? AND EXT_SEQ=?", conn);
            delHdrCmd.Parameters.AddWithValue("?", taxYear.PadRight(4));
            delHdrCmd.Parameters.AddWithValue("?", extSeq.ToString("0"));
            var hdrRowsDeleted = await delHdrCmd.ExecuteNonQueryAsync();

            _logger.LogInformation(
                "Force-deleted from IBM i for TaxYear={TaxYear}, ExtSeq={ExtSeq}. Detail rows deleted: {DetailRows}, Header rows deleted: {HeaderRows}",
                taxYear, extSeq, detailRowsDeleted, hdrRowsDeleted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error force-deleting from IBM i for TaxYear={TaxYear}, ExtSeq={ExtSeq}",
                taxYear, extSeq);
            throw;
        }
    }

    public async Task BuildIrsFileAsync(string taxYear, long extSeq,
                                         IEnumerable<string> forms,
                                         IEnumerable<string> associations)
    {
        // Web-side TX9563 conversion: select records, generate extract lines, and persist output locally.
        var formSet = forms
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var assnSet = associations
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var records = await QueryExtractSourceRecordsAsync(taxYear, formSet, assnSet);
        
        // Fetch association names from FCMCCRL2 for all associations in records
        var assocNames = await GetAssociationNamesAsync(records.Select(r => r.Asa).Distinct());
        
        var lines = BuildExtractLines(taxYear, extSeq, formSet, assnSet, records, assocNames, GetTransmitterInfo());
        await PersistGeneratedExtractFileAsync(taxYear, extSeq, lines);

        var count = records.LongCount();
        await UpsertLocalExtractHeaderAsync(taxYear, extSeq, count);
    }

    public async Task<(string FileName, byte[] Content)?> DownloadExtractAsync(string taxYear, long extSeq)
    {
        var filePath = GetExtractFilePath(taxYear, extSeq);
        if (!File.Exists(filePath))
            return null;

        var fileName = Path.GetFileName(filePath);
        var bytes = await File.ReadAllBytesAsync(filePath);
        return (fileName, bytes);
    }

    private async Task<List<TaxDetailRecord>> QueryExtractSourceRecordsAsync(
        string taxYear, IList<string> formSet, IList<string> assnSet)
    {
        var localRows = await QueryExtractSourceFromLocalAsync(taxYear, formSet, assnSet);

        try
        {
            var ibmiRows = await QueryExtractSourceFromIbmiAsync(taxYear, formSet, assnSet);
            var merged = ibmiRows.ToDictionary(GetExtractKey, StringComparer.OrdinalIgnoreCase);
            foreach (var local in localRows)
            {
                merged[GetExtractKey(local)] = local;
            }

            return merged.Values
                .OrderBy(r => r.Form)
                .ThenBy(r => r.Asa)
                .ThenBy(r => r.MbrNo)
                .ThenBy(r => r.MbrSub)
                .ToList();
        }
        catch (OdbcException ex)
        {
            _logger.LogWarning(ex,
                "IBM i query unavailable while building web extract for year {TaxYear}; using local staged records only.",
                taxYear);
            return localRows;
        }
    }

    private async Task<List<TaxDetailRecord>> QueryExtractSourceFromIbmiAsync(
        string taxYear, IList<string> formSet, IList<string> assnSet)
    {
        if (!int.TryParse(taxYear, out var taxYearInt))
            return new List<TaxDetailRecord>();

        var sql = new System.Text.StringBuilder($@"SELECT TAXYR,FORM,ASA,MBRNO,MBRSUB,SSIDN,BNM,BAD,BADX,BCTY,BST,BZP,
                            SSIDC,INTPD,POINTS,INTERN,ERNWTH,COMPEN,RENTS,MEDPAY,LGLPAY,OTHER,
                            WTHHELD,ERRORS,RPT_TO_IRS,CORRIN,ORIGDATE,SECSAME,SECADDR,SECDESC,
                            SECOTHER,SECNUM,MTGACQDT,UNPPRN,FMVAL,DTEAQR,PRDESC,FOREGN,DEPT
                     FROM {_lib}/TXRDTL
                     WHERE TAXYR=?");

        if (formSet.Count > 0)
        {
            sql.Append(" AND FORM IN (");
            sql.Append(string.Join(",", formSet.Select(_ => "?")));
            sql.Append(")");
        }

        if (assnSet.Count > 0)
        {
            sql.Append(" AND ASA IN (");
            sql.Append(string.Join(",", assnSet.Select(_ => "?")));
            sql.Append(")");
        }

        sql.Append(" ORDER BY FORM, ASA, MBRNO, MBRSUB");

        var rows = new List<TaxDetailRecord>();
        await using var conn = new OdbcConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new OdbcCommand(sql.ToString(), conn);
        cmd.Parameters.AddWithValue("?", taxYearInt);

        foreach (var form in formSet)
            cmd.Parameters.AddWithValue("?", form.PadRight(9));
        foreach (var asa in assnSet)
            cmd.Parameters.AddWithValue("?", asa.PadRight(3));

        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            rows.Add(MapExtractRecord(rdr));
        }

        return rows;
    }

    private async Task<List<TaxDetailRecord>> QueryExtractSourceFromLocalAsync(
        string taxYear, IList<string> formSet, IList<string> assnSet)
    {
        var query = _db.TaxDetails.Where(d => d.TaxYear == taxYear.Trim());
        if (formSet.Count > 0)
            query = query.Where(d => formSet.Contains(d.Form));
        if (assnSet.Count > 0)
            query = query.Where(d => assnSet.Contains(d.Asa));

        var localRows = await query.ToListAsync();

        return localRows
            .Select(d => d.ToRecord())
            .OrderBy(r => r.Form)
            .ThenBy(r => r.Asa)
            .ThenBy(r => r.MbrNo)
            .ThenBy(r => r.MbrSub)
            .ToList();
    }

    private static void CopyToRecord(char[] dest, int destIndex, string source, int length, bool padLeft = false)
    {
        if (string.IsNullOrEmpty(source))
        {
            // Fill with spaces (blank padding)
            for (int i = 0; i < length; i++)
                dest[destIndex + i] = ' ';
            return;
        }

        var src = source.ToCharArray();
        if (src.Length >= length)
        {
            // Source is long enough, copy what we need
            Array.Copy(src, 0, dest, destIndex, length);
        }
        else
        {
            // Source is shorter, copy what we have and pad
            Array.Copy(src, 0, dest, destIndex, src.Length);
            // Pad the rest with spaces
            for (int i = src.Length; i < length; i++)
            {
                if (padLeft)
                {
                    // Shift everything right and fill left with spaces
                    for (int j = length - 1; j > length - src.Length - 1; j--)
                        dest[destIndex + j] = dest[destIndex + j - 1];
                    dest[destIndex + (length - src.Length - 1)] = ' ';
                }
                else
                {
                    dest[destIndex + i] = ' ';
                }
            }
        }
    }

    private static List<string> BuildExtractLines(
        string taxYear,
        decimal extSeq,
        IList<string> forms,
        IList<string> associations,
        IList<TaxDetailRecord> records,
        IDictionary<string, string> associationNames,
        dynamic transmitterInfo)
    {
        var lines = new List<string>();
        var recSeq = 1L;
        
        // TX9563-equivalent behavior: only reportable rows are included in IRS extract output.
        var reportable = records
            .Where(r => string.Equals(Clean(r.ReportToIrs), "Y", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Count total B records for T record
        long totalBRecords = 0;
        var grpNo = 0;
        var groupsList = new List<(int GrpNo, string Form, string Asa, string AssnName, long Count, decimal PrimaryAmt, decimal Withheld, int Corrections)>();
        
        foreach (var grp in reportable
            .GroupBy(r => new { Form = Clean(r.Form), Asa = Clean(r.Asa) })
            .OrderBy(g => g.Key.Form)
            .ThenBy(g => g.Key.Asa))
        {
            grpNo++;
            var grpCount = grp.LongCount();
            var grpPrimaryAmt = grp.Sum(r => GetPrimaryAmount(r));
            var grpWithheld = grp.Sum(r => r.WthHeld);
            var grpCorrections = grp.Count(r => string.Equals(Clean(r.CorrIn), "Y", StringComparison.OrdinalIgnoreCase));
            var assnName = associationNames.TryGetValue(grp.Key.Asa, out var name) ? name : grp.Key.Asa;
            
            groupsList.Add((grpNo, grp.Key.Form, grp.Key.Asa, assnName, grpCount, grpPrimaryAmt, grpWithheld, grpCorrections));
            totalBRecords += grpCount;
        }

        // T Record: Transmitter/Header (750 chars fixed-width)
        recSeq++;
        var tRecord = new char[750];
        Array.Fill(tRecord, ' ');
        var pos = 0;
        
        // 1-1: Record type
        tRecord[pos++] = 'T';
        
        // 2-5: Tax year
        int.TryParse(taxYear, out var year);
        var yearStr = year.ToString("0000");
        CopyToRecord(tRecord, pos, yearStr, 4);
        pos += 4;
        
        // 6-6: Prior year data indicator (blank)
        pos += 1;
        
        // 7-15: Transmitter TIN
        var tinStr = Clean(transmitterInfo.TIN).PadLeft(9, '0');
        CopyToRecord(tRecord, pos, tinStr, 9);
        pos += 9;
        
        // 16-20: Transmitter control code (blank)
        pos += 5;
        
        // 21-22: Replacement alpha code (blank)
        pos += 2;
        
        // 23-27: Filler (blank)
        pos += 5;
        
        // 28-28: Test file indicator (blank)
        pos += 1;
        
        // 29-29: Transmitter FEI (foreign entity indicator)
        var feiStr = Clean(transmitterInfo.FEI);
        if (!string.IsNullOrEmpty(feiStr))
            tRecord[pos] = feiStr[0];
        pos += 1;
        
        // 30-69: Transmitter name
        var xmtrName = Clean(transmitterInfo.CompanyName).PadRight(40);
        CopyToRecord(tRecord, pos, xmtrName, 40);
        pos += 40;
        
        // 70-109: Transmitter name 2
        var xmtrName2 = Clean(transmitterInfo.CompanyName2).PadRight(40);
        CopyToRecord(tRecord, pos, xmtrName2, 40);
        pos += 40;
        
        // 110-149: Company name
        var compName = Clean(transmitterInfo.CompanyName).PadRight(40);
        CopyToRecord(tRecord, pos, compName, 40);
        pos += 40;
        
        // 150-189: Company name 2
        var compName2 = Clean(transmitterInfo.CompanyName2).PadRight(40);
        CopyToRecord(tRecord, pos, compName2, 40);
        pos += 40;
        
        // 190-229: Company mailing address
        var compAddr = Clean(transmitterInfo.Address).PadRight(40);
        CopyToRecord(tRecord, pos, compAddr, 40);
        pos += 40;
        
        // 230-269: Company city
        var compCity = Clean(transmitterInfo.City).PadRight(40);
        CopyToRecord(tRecord, pos, compCity, 40);
        pos += 40;
        
        // 270-271: Company state
        var compState = Clean(transmitterInfo.State).PadRight(2);
        CopyToRecord(tRecord, pos, compState, 2);
        pos += 2;
        
        // 272-280: Company zip
        var compZip = Clean(transmitterInfo.Zip).PadRight(9);
        CopyToRecord(tRecord, pos, compZip, 9);
        pos += 9;
        
        // 281-295: Filler (blank)
        pos += 15;
        
        // 296-303: Total number of B records
        var totalBStr = totalBRecords.ToString("00000000");
        CopyToRecord(tRecord, pos, totalBStr, 8);
        pos += 8;
        
        // 304-343: Contact name
        var contName = Clean(transmitterInfo.ContactName).PadRight(40);
        CopyToRecord(tRecord, pos, contName, 40);
        pos += 40;
        
        // 344-358: Contact phone
        var contPhone = Clean(transmitterInfo.ContactPhone).PadRight(15);
        CopyToRecord(tRecord, pos, contPhone, 15);
        pos += 15;
        
        // 359-393: Contact email
        var contEmail = Clean(transmitterInfo.ContactEmail).PadRight(35);
        CopyToRecord(tRecord, pos, contEmail, 35);
        pos += 35;
        
        // 394-395: Magnetic tape indicator (blank)
        pos += 2;
        
        // 396-410: Replacement file name (blank)
        pos += 15;
        
        // 411-416: Transmitter media number (blank)
        pos += 6;
        
        // 417-499: Filler (blank)
        pos += 83;
        
        // 500-507: Record sequence number
        var seqStr = recSeq.ToString("00000000");
        CopyToRecord(tRecord, pos, seqStr, 8);
        pos += 8;
        
        // 508-517: Filler (blank)
        pos += 10;
        
        // 518-750: Vendor and other sections (blank for our purposes)
        lines.Add(new string(tRecord));

        // Now process groups and detail records
        var bRecNo = 0L;
        var outputtedBRecordKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var (gNo, form, asa, assnName, count, primaryAmt, withheld, corrections) in groupsList)
        {
            var recs = reportable
                .Where(r => Clean(r.Form) == form && Clean(r.Asa) == asa)
                .ToList();

            // A Record: Payer header (750 chars fixed-width)
            recSeq++;
            var aRecord = new char[750];
            Array.Fill(aRecord, ' ');
            pos = 0;
            
            // 1-1: Record type
            aRecord[pos++] = 'A';
            
            // 2-5: Tax year
            CopyToRecord(aRecord, pos, yearStr, 4);
            pos += 4;
            
            // 6-6: Payer combined fed/state (blank)
            pos += 1;
            
            // 7-15: Payer TIN
            var payerTin = Clean(transmitterInfo.TIN).PadLeft(9, '0');
            CopyToRecord(aRecord, pos, payerTin, 9);
            pos += 9;
            
            // 16-19: Payer name control (blank)
            pos += 4;
            
            // 20-20: Payer last filing (blank)
            pos += 1;
            
            // 21-22: Return type (blank)
            pos += 2;
            
            // 23-40: Amount codes (blank)
            pos += 18;
            
            // 41-42: Filler (blank)
            pos += 2;
            
            // 43-43: Original file indicator
            aRecord[pos++] = 'O';
            
            // 44-44: Replacement indicator (blank)
            pos += 1;
            
            // 45-45: Correction indicator (blank)
            pos += 1;
            
            // 46-46: Filler (blank)
            pos += 1;
            
            // 47-47: Payer foreign entity (blank)
            pos += 1;
            
            // 48-87: Payer name
            CopyToRecord(aRecord, pos, form, 40);
            pos += 40;
            
            // 88-127: Payer name 2
            CopyToRecord(aRecord, pos, asa, 40);
            pos += 40;
            
            // 128-128: Transfer agent indicator (blank)
            pos += 1;
            
            // 129-168: Payer mailing address
            CopyToRecord(aRecord, pos, assnName, 40);
            pos += 40;
            
            // 169-208: Payer city
            var payerCity = Clean(transmitterInfo.City);
            CopyToRecord(aRecord, pos, payerCity, 40);
            pos += 40;
            
            // 209-210: Payer state
            CopyToRecord(aRecord, pos, compState, 2);
            pos += 2;
            
            // 211-219: Payer zip
            CopyToRecord(aRecord, pos, compZip, 9);
            pos += 9;
            
            // 220-234: Payer phone (blank)
            pos += 15;
            
            // 235-494: Filler (blank)
            pos += 260;
            
            // 495-502: Record sequence number (A record)
            seqStr = (recSeq + 1000000).ToString().Substring(1);
            CopyToRecord(aRecord, pos, seqStr, 8);
            pos += 8;
            
            // 503-749: Filler
            lines.Add(new string(aRecord));

            // B Records: Detail/Payee records (output each unique record only once)
            foreach (var r in recs)
            {
                // Create a unique key for this payee record using trimmed values
                var bRecordKey = $"{Clean(r.SsiDn.ToString())}|{Clean(r.MbrNo.ToString())}";
                
                // Skip if already output in a previous group
                if (outputtedBRecordKeys.Contains(bRecordKey))
                {
                    // DEBUG: This record was skipped due to deduplication
                    continue;
                }
                    
                outputtedBRecordKeys.Add(bRecordKey);
                bRecNo++;
                recSeq++;
                
                var bRecord = new char[750];
                Array.Fill(bRecord, ' ');
                pos = 0;
                
                // 1-1: Record type
                bRecord[pos++] = 'B';
                
                // 2-5: Tax year
                CopyToRecord(bRecord, pos, yearStr, 4);
                pos += 4;
                
                // 6-6: Corrected indicator
                var corrInd = Clean(r.CorrIn) == "Y" ? "1" : " ";
                bRecord[pos++] = corrInd[0];
                
                // 7-10: Name control (blank)
                pos += 4;
                
                // 11-11: TIN type
                var tinType = "1"; // SSN
                bRecord[pos++] = tinType[0];
                
                // 12-20: Payee TIN
                var payeeTin = r.SsiDn.ToString("000000000");
                CopyToRecord(bRecord, pos, payeeTin, 9);
                pos += 9;
                
                // 21-40: Payer account number
                var acctNum = r.MbrNo.ToString("00000000000");
                CopyToRecord(bRecord, pos, acctNum, 20);
                pos += 20;
                
                // 41-44: Payer office code (blank)
                pos += 4;
                
                // 45-54: Filler (blank)
                pos += 10;
                
                // 55-66: Amount 1 (interest paid)
                var amt1Str = r.IntPd.ToString("00000000000000");
                CopyToRecord(bRecord, pos, amt1Str, 12);
                pos += 12;
                
                // 67-78: Amount 2 (blank for 1098)
                pos += 12;
                
                // 79-90: Amount 3 (blank)
                pos += 12;
                
                // 91-102: Amount 4 (blank)
                pos += 12;
                
                // 103-114: Amount 5 (blank)
                pos += 12;
                
                // 115-126: Amount 6 (blank)
                pos += 12;
                
                // 127-138: Amount 7 (blank)
                pos += 12;
                
                // 139-150: Amount 8 (blank)
                pos += 12;
                
                // 151-162: Amount 9 (blank)
                pos += 12;
                
                // 163-174: Amount A (blank)
                pos += 12;
                
                // 175-186: Amount B (blank)
                pos += 12;
                
                // 187-198: Amount C (blank)
                pos += 12;
                
                // 199-210: Amount D (blank)
                pos += 12;
                
                // 211-222: Amount E (blank)
                pos += 12;
                
                // 223-246: Filler (blank)
                pos += 24;
                
                // 247-286: Filler (blank)
                pos += 40;
                
                // 287-287: Foreign country indicator
                var fcInd = Clean(r.Foreign) == "Y" ? "2" : " ";
                bRecord[pos++] = fcInd[0];
                
                // 288-327: Payee name
                var payeeName = Clean(r.BorrName);
                CopyToRecord(bRecord, pos, payeeName, 40);
                pos += 40;
                
                // 328-367: Payee name 2
                pos += 40;
                
                // 368-407: Payee mailing address
                var payeeAddr = Clean(r.BorrAddr);
                CopyToRecord(bRecord, pos, payeeAddr, 40);
                pos += 40;
                
                // 408-447: Filler (blank)
                pos += 40;
                
                // 448-487: Payee city
                var payeeCity = Clean(r.BorrCity);
                CopyToRecord(bRecord, pos, payeeCity, 40);
                pos += 40;
                
                // 488-489: Payee state
                var payeeState = Clean(r.BorrState);
                CopyToRecord(bRecord, pos, payeeState, 2);
                pos += 2;
                
                // 490-498: Payee zip
                var payeeZip = r.BorrZip.ToString("000000000");
                CopyToRecord(bRecord, pos, payeeZip, 9);
                pos += 9;
                
                // 499-499: Filler (blank)
                pos += 1;
                
                // 500-507: Record sequence number
                seqStr = (recSeq + 1000000).ToString().Substring(1);
                CopyToRecord(bRecord, pos, seqStr, 8);
                pos += 8;
                
                // 508-750: Filler
                lines.Add(new string(bRecord));
            }

            // C Record: Summary/Control (750 chars fixed-width)
            recSeq++;
            var cRecord = new char[750];
            Array.Fill(cRecord, ' ');
            pos = 0;
            
            // 1-1: Record type
            cRecord[pos++] = 'C';
            
            // 2-9: Total B records for this A record
            var countStr = count.ToString("00000000");
            CopyToRecord(cRecord, pos, countStr, 8);
            pos += 8;
            
            // 10-15: Filler (blank)
            pos += 6;
            
            // 16-33: Amount 1 total
            var amt1Total = primaryAmt.ToString("000000000000000000");
            CopyToRecord(cRecord, pos, amt1Total, 18);
            pos += 18;
            
            // 34-265: All other amount totals (blank)
            pos += 232;
            
            // 266-273: Record sequence number
            seqStr = (recSeq + 1000000).ToString().Substring(1);
            CopyToRecord(cRecord, pos, seqStr, 8);
            pos += 8;
            
            // 274-750: Filler
            lines.Add(new string(cRecord));
        }

        // F Record: End of file (750 chars fixed-width)
        recSeq++;
        var fRecord = new char[750];
        Array.Fill(fRecord, ' ');
        pos = 0;
        
        // 1-1: Record type
        fRecord[pos++] = 'F';
        
        // 2-9: Total A records
        var totalAStr = groupsList.Count.ToString("00000000");
        CopyToRecord(fRecord, pos, totalAStr, 8);
        pos += 8;
        
        // 10-30: All zeros (21 chars)
        var zerosStr = new string('0', 21);
        CopyToRecord(fRecord, pos, zerosStr, 21);
        pos += 21;
        
        // 31-49: Filler (blank)
        pos += 19;
        
        // 50-57: Total B records
        var totalBRecStr = totalBRecords.ToString("00000000");
        CopyToRecord(fRecord, pos, totalBRecStr, 8);
        pos += 8;
        
        // 58-499: Filler (blank)
        pos += 442;
        
        // 500-507: Record sequence number
        var fSeqStr = (recSeq + 1000000).ToString().Substring(1);
        CopyToRecord(fRecord, pos, fSeqStr, 8);
        pos += 8;
        
        // 508-750: Filler
        lines.Add(new string(fRecord));
        
        return lines;
    }

    private async Task PersistGeneratedExtractFileAsync(string taxYear, long extSeq, IList<string> lines)
    {
        var filePath = GetExtractFilePath(taxYear, extSeq);
        var dirPath = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dirPath);
        // Write without trailing newline after last 'F' record
        var content = string.Join(Environment.NewLine, lines);
        await File.WriteAllTextAsync(filePath, content);
    }

    private string GetExtractFilePath(string taxYear, long extSeq)
    {
        var safeYear = (taxYear ?? string.Empty).Trim();
        var safeSeq = extSeq.ToString("00000");
        // Output to network path: \\nterprise.net\apps\Distributions\Test
        var baseDir = @"\\nterprise.net\apps\Distributions\Test";
        return Path.Combine(baseDir, $"IRS_{safeYear}_{safeSeq}.txt");
    }

    private async Task UpsertLocalExtractHeaderAsync(string taxYear, long extSeq, long count)
    {
        var localExtract = await _db.Extracts
            .FirstOrDefaultAsync(e => e.TaxYear == taxYear && e.ExtSeq == extSeq);

        if (localExtract is null)
        {
            _db.Extracts.Add(new LocalExtract
            {
                TaxYear = taxYear,
                ExtSeq = extSeq,
                ExtDesc = "Generated Extract",
                ExtSelDat = DateTime.UtcNow.ToString("yyyyMMdd"),
                XmtrName = string.Empty,
                XmtrName2 = string.Empty,
                CreatedAt = DateTime.UtcNow,
                ExtDate = DateTime.UtcNow.ToString("yyyyMMdd"),
                BRecsT = count
            });
        }
        else
        {
            localExtract.BRecsT = count;
            localExtract.ExtDate = DateTime.UtcNow.ToString("yyyyMMdd");
        }

        await _db.SaveChangesAsync();
    }

    private static TaxDetailRecord MapExtractRecord(System.Data.Common.DbDataReader r) => new()
    {
        TaxYear    = SafeExtractString(r, 0),
        Form       = SafeExtractString(r, 1),
        Asa        = SafeExtractString(r, 2),
        MbrNo      = r.IsDBNull(3) ? 0 : r.GetDecimal(3),
        MbrSub     = SafeExtractString(r, 4),
        SsiDn      = r.IsDBNull(5) ? 0 : r.GetDecimal(5),
        BorrName   = SafeExtractString(r, 6),
        BorrAddr   = SafeExtractString(r, 7),
        BorrAddrX  = SafeExtractString(r, 8),
        BorrCity   = SafeExtractString(r, 9),
        BorrState  = SafeExtractString(r, 10),
        BorrZip    = r.IsDBNull(11) ? 0 : r.GetDecimal(11),
        SsiDc      = SafeExtractString(r, 12),
        IntPd      = r.IsDBNull(13) ? 0 : r.GetDecimal(13),
        Points     = r.IsDBNull(14) ? 0 : r.GetDecimal(14),
        InterN     = r.IsDBNull(15) ? 0 : r.GetDecimal(15),
        ErnWth     = r.IsDBNull(16) ? 0 : r.GetDecimal(16),
        Compen     = r.IsDBNull(17) ? 0 : r.GetDecimal(17),
        Rents      = r.IsDBNull(18) ? 0 : r.GetDecimal(18),
        MedPay     = r.IsDBNull(19) ? 0 : r.GetDecimal(19),
        LglPay     = r.IsDBNull(20) ? 0 : r.GetDecimal(20),
        Other      = r.IsDBNull(21) ? 0 : r.GetDecimal(21),
        WthHeld    = r.IsDBNull(22) ? 0 : r.GetDecimal(22),
        Errors     = SafeExtractString(r, 23),
        ReportToIrs = SafeExtractString(r, 24),
        CorrIn     = SafeExtractString(r, 25),
        OrigDate   = SafeExtractString(r, 26),
        SecSame    = SafeExtractString(r, 27),
        SecAddr    = SafeExtractString(r, 28),
        SecDesc    = SafeExtractString(r, 29),
        SecOther   = SafeExtractString(r, 30),
        SecNum     = r.IsDBNull(31) ? 0 : r.GetDecimal(31),
        MtgAcqDt   = SafeExtractString(r, 32),
        UnpPrn     = r.IsDBNull(33) ? 0 : r.GetDecimal(33),
        FmVal      = r.IsDBNull(34) ? 0 : r.GetDecimal(34),
        DteAqr     = SafeExtractString(r, 35),
        PrDesc     = SafeExtractString(r, 36),
        Foreign    = SafeExtractString(r, 37),
        Dept       = r.IsDBNull(38) ? 0 : r.GetDecimal(38),
    };

    private static string SafeExtractString(System.Data.Common.DbDataReader r, int col)
    {
        if (r.IsDBNull(col)) return string.Empty;
        var val = r.GetValue(col);
        if (val is DateTime dt) return dt.ToString("yyyyMMdd");
        return val.ToString()?.Trim() ?? string.Empty;
    }

    private static string GetExtractKey(TaxDetailRecord record)
    {
        var normalizedMbrNo = ((long)record.MbrNo).ToString();
        return $"{record.TaxYear.Trim()}|{record.Form.Trim()}|{record.Asa.Trim()}|{normalizedMbrNo}|{record.MbrSub.Trim()}";
    }

    private static string Clean(string? value)
        => (value ?? string.Empty).Replace("|", " ").Replace("\r", " ").Replace("\n", " ").Trim();

    private static string FmtMoney(decimal amount)
        => amount.ToString("0.00");

    private static decimal GetPrimaryAmount(TaxDetailRecord record)
    {
        var form = Clean(record.Form).ToUpperInvariant();
        return form switch
        {
            "1098" => record.IntPd + record.Points,
            "1099-INT" => record.InterN,
            "1099-A" => record.FmVal,
            "1099-PATR" => record.PatRef,
            "1099-MISC" => record.Compen + record.Rents + record.MedPay + record.LglPay + record.Other,
            "1099-NEC" => record.Compen + record.LglPay,
            _ => record.IntPd + record.InterN + record.Compen + record.Rents + record.Other
        };
    }

    private sealed class GoAnywhereTransmitOutcome
    {
        public bool DirectProgramCallFailed { get; set; }
        public bool SshFallbackAttempted { get; set; }
    }

    public async Task<TransmitExtractResult> TransmitExtractAsync(string taxYear, long extSeq)
    {
        var filePath = GetExtractFilePath(taxYear, extSeq);
        if (!File.Exists(filePath))
        {
            _logger.LogError(
                "Transmit requested but extract file not found. TaxYear={TaxYear}, ExtSeq={ExtSeq}, FilePath={FilePath}",
                taxYear, extSeq, filePath);
            return new TransmitExtractResult
            {
                Success = false,
                FileFound = false
            };
        }

        if (_tx9565rUnavailable)
        {
            var fallbackOutcome = await TryRunGoAnywhereWithLocalExtractAsync(taxYear, extSeq);
            return new TransmitExtractResult
            {
                Success = true,
                FileFound = true,
                DirectProgramCallFailed = fallbackOutcome.DirectProgramCallFailed,
                SshFallbackAttempted = fallbackOutcome.SshFallbackAttempted
            };
        }

        var ctrl = new Models.TaxControlRecord { TaxYear = taxYear }.ToControlString();
        try
        {
            await _ibmi.ExecuteProgramAsync("TX9565R", null, ctrl, extSeq.ToString().PadLeft(5));
        }
        catch (OdbcException ex) when (ex.Message.Contains("SQL0204", StringComparison.OrdinalIgnoreCase)
                                      && ex.Message.Contains("TX9565R", StringComparison.OrdinalIgnoreCase))
        {
            _tx9565rUnavailable = true;
        }
#pragma warning disable CS0168
        catch (Exception _)
        {
            // Program not available, fall through to GoAnywhere
        }
#pragma warning restore CS0168

        var goAnywhereOutcome = await TryRunGoAnywhereWithLocalExtractAsync(taxYear, extSeq);
        return new TransmitExtractResult
        {
            Success = true,
            FileFound = true,
            DirectProgramCallFailed = goAnywhereOutcome.DirectProgramCallFailed,
            SshFallbackAttempted = goAnywhereOutcome.SshFallbackAttempted
        };
    }

    private async Task<GoAnywhereTransmitOutcome> TryRunGoAnywhereWithLocalExtractAsync(string taxYear, long extSeq)
    {
        var outcome = new GoAnywhereTransmitOutcome();

        if (!_goAnywhereEnabled)
            return outcome;

        var localFilePath = GetExtractFilePath(taxYear, extSeq);
        if (!File.Exists(localFilePath))
        {
            _logger.LogError(
                "GoAnywhere transmit failed—local extract file not found. TaxYear={TaxYear}, ExtSeq={ExtSeq}, FilePath={FilePath}",
                taxYear, extSeq, localFilePath);
            return outcome;
        }

        if (string.IsNullOrWhiteSpace(_goAnywhereExePath)
            || _goAnywhereExePath.Contains("TODO_ADD_GOANYWHERE_EXE_PATH", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "GoAnywhere transmit failed—ExecutablePath not configured. TaxYear={TaxYear}, ExtSeq={ExtSeq}",
                taxYear, extSeq);
            return outcome;
        }

        // If ExecuteOnIBMi is true, call GOANYWHERE/RUNPROJECT directly on IBM i.
        // Keep SSH execution as fallback in case direct program call is unavailable.
        if (_goAnywhereExecuteOnIBMi)
        {
            return await TryRunGoAnywhereViaProgramCallAsync(localFilePath, taxYear, extSeq);
        }

        // Otherwise, try HTTP execution
        if (Uri.TryCreate(_goAnywhereExePath, UriKind.Absolute, out var executeUri)
            && (executeUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || executeUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            await TryRunGoAnywhereHttpAsync(executeUri, localFilePath, taxYear, extSeq);
        }
        else
        {
            _logger.LogError(
                "GoAnywhere transmit failed—ExecutablePath is not a valid HTTP/HTTPS URL. TaxYear={TaxYear}, ExtSeq={ExtSeq}, Path={Path}",
                taxYear, extSeq, _goAnywhereExePath);
        }

        return outcome;
    }

    private async Task TryRunGoAnywhereHttpAsync(Uri executeUri, string localFilePath, string taxYear, decimal extSeq)
    {
        if (string.IsNullOrWhiteSpace(_goAnywhereProjectPath))
        {
            _logger.LogError(
                "GoAnywhere HTTP execute failed—ProjectPath is not configured. TaxYear={TaxYear}, ExtSeq={ExtSeq}",
                taxYear, extSeq);
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            
            // Add Basic Auth if credentials are configured
            if (!string.IsNullOrWhiteSpace(_goAnywhereUsername) && !string.IsNullOrWhiteSpace(_goAnywherePassword))
            {
                var credentials = Convert.ToBase64String(
                    System.Text.Encoding.ASCII.GetBytes($"{_goAnywhereUsername}:{_goAnywherePassword}"));
                http.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
            }
            
            using var payload = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["projectFile"] = _goAnywhereProjectPath,
                ["DestFile"] = localFilePath,
                ["localFilePath"] = localFilePath,
                ["taxYear"] = taxYear,
                ["extSeq"] = extSeq.ToString("0")
            });

            using var response = await http.PostAsync(executeUri, payload);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "GoAnywhere HTTP execute failed. TaxYear={TaxYear}, ExtSeq={ExtSeq}, Status={Status}, Response={Response}",
                    taxYear, extSeq, (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "GoAnywhere HTTP execute failed with exception. TaxYear={TaxYear}, ExtSeq={ExtSeq}",
                taxYear, extSeq);
        }
    }

    private async Task<GoAnywhereTransmitOutcome> TryRunGoAnywhereViaProgramCallAsync(string localFilePath, string taxYear, decimal extSeq)
    {
        var outcome = new GoAnywhereTransmitOutcome();
        var clCommand = string.Empty;
        var qcmdexcSql = string.Empty;

        try
        {
            if (string.IsNullOrWhiteSpace(_goAnywhereProjectPath))
            {
                _logger.LogError(
                    "GoAnywhere IBM i execute failed-ProjectPath is not configured. TaxYear={TaxYear}, ExtSeq={ExtSeq}",
                    taxYear, extSeq);
                return outcome;
            }

            // Build CL command and execute via QSYS/QCMDEXC with explicit command length.
            var fileName = Path.GetFileName(localFilePath);
            var projectPath = _goAnywhereProjectPath.Contains("/Development")
                ? _goAnywhereProjectPath.Substring(_goAnywhereProjectPath.IndexOf("/Development"))
                : _goAnywhereProjectPath;

            clCommand = $"GOANYWHERE/RUNPROJECT PROJECT('{projectPath}') VARIABLE((Destfile '{fileName}'))";
            var escapedCommand = clCommand.Replace("'", "''");
            qcmdexcSql = $"CALL QSYS2.QCMDEXC('{escapedCommand}')";

            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            await using var cmd = new OdbcCommand(qcmdexcSql, conn);
            await cmd.ExecuteNonQueryAsync();

            _logger.LogInformation(
                "GoAnywhere transmit completed. TaxYear={TaxYear}, ExtSeq={ExtSeq}",
                taxYear, extSeq);
            return outcome;
        }
#pragma warning disable CS0168
        catch (Exception ex)
        {
            outcome.DirectProgramCallFailed = true;
            _logger.LogError(ex,
                "GoAnywhere IBM i direct program call failed for TaxYear={TaxYear}, ExtSeq={ExtSeq}. Falling back to SSH execution.",
                taxYear, extSeq);
            if (!string.IsNullOrWhiteSpace(clCommand))
            {
                _logger.LogError("Failed CL command string: {Command}", clCommand);
            }
            if (!string.IsNullOrWhiteSpace(qcmdexcSql))
            {
                _logger.LogError("Failed QCMDEXC SQL: {Sql}", qcmdexcSql);
            }

            // SSH fallback commented out - using direct QSYS2.QCMDEXC call only
            // outcome.SshFallbackAttempted = await TryRunGoAnywhereViaSshFallbackAsync(localFilePath, taxYear, extSeq);
            return outcome;
        }
#pragma warning restore CS0168
    }

    // Legacy SSH execution retained as fallback while validating direct RUNPROJECT call.
    private async Task<bool> TryRunGoAnywhereViaSshFallbackAsync(string localFilePath, string taxYear, decimal extSeq)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_goAnywhereHost)
                || string.IsNullOrWhiteSpace(_goAnywhereUsername)
                || string.IsNullOrWhiteSpace(_goAnywherePassword))
            {
                _logger.LogWarning(
                    "GoAnywhere SSH fallback skipped due to missing host/credentials. TaxYear={TaxYear}, ExtSeq={ExtSeq}",
                    taxYear, extSeq);
                return false;
            }

            var fileName = Path.GetFileName(localFilePath);
            var projectPath = _goAnywhereProjectPath.Contains("/Development")
                ? _goAnywhereProjectPath.Substring(_goAnywhereProjectPath.IndexOf("/Development"))
                : _goAnywhereProjectPath;
            var command = $"system \"goanyWHERE/RUNPROJECT project('{projectPath}') variable((Destfile '{fileName}'))\"";

            var sshTask = Task.Run(async () =>
            {
                try
                {
                    using var sshClient = new SshClient(_goAnywhereHost, 22, _goAnywhereUsername, _goAnywherePassword);
                    sshClient.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
                    await Task.Run(() => sshClient.Connect());

                    using var sshCommand = sshClient.CreateCommand(command);
                    sshCommand.CommandTimeout = TimeSpan.FromSeconds(300);
                    await Task.Run(() => sshCommand.Execute());

                    sshClient.Disconnect();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "GoAnywhere SSH fallback failed for TaxYear={TaxYear}, ExtSeq={ExtSeq}",
                        taxYear, extSeq);
                }
            });

            await Task.WhenAny(sshTask, Task.Delay(5000));
            _logger.LogInformation(
                "GoAnywhere SSH fallback submitted for TaxYear={TaxYear}, ExtSeq={ExtSeq}",
                taxYear, extSeq);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "GoAnywhere SSH fallback threw an exception for TaxYear={TaxYear}, ExtSeq={ExtSeq}",
                taxYear, extSeq);
            return false;
        }
    }

    /// <summary>
    /// Removes potentially sensitive information (passwords, credentials) from error messages before logging.
    /// </summary>
    private string SanitizeCredentialsFromError(string errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
            return errorMessage;

        var sanitized = errorMessage;
        
        // Remove password if it appears in the error
        if (!string.IsNullOrWhiteSpace(_goAnywherePassword))
        {
            sanitized = sanitized.Replace(_goAnywherePassword, "***PASSWORD***");
        }
        
        // Remove username if it appears in the error
        if (!string.IsNullOrWhiteSpace(_goAnywhereUsername))
        {
            sanitized = sanitized.Replace(_goAnywhereUsername, "***USERNAME***");
        }
        
        return sanitized;
    }

    public async Task<long> GetExtractRecordCountAsync(string taxYear, long extSeq)
    {
        var sql = $"SELECT COALESCE(SUM(#BRECS_T),0) FROM {_lib}/TXIRST " +
                  $"WHERE TAXYR=? AND EXT_SEQ=?";
        await using var conn = new OdbcConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new OdbcCommand(sql, conn);
        cmd.Parameters.AddWithValue("?", taxYear);
        cmd.Parameters.AddWithValue("?", extSeq);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<IDictionary<string, string>> GetAssociationNamesAsync(IEnumerable<string> assnCodes)
    {
        var assocDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assnList = assnCodes.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (assnList.Count == 0)
            return assocDict;

        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            
            // Query FCMCCRL2 for association descriptions by FMGLPC (corp code / asa)
            var sql = $"SELECT FMGLPC, FMDSC FROM FCMCCRL2 WHERE FMGLPC IN ({string.Join(",", assnList.Select(_ => "?"))})";
            await using var cmd = new OdbcCommand(sql, conn);
            
            foreach (var asn in assnList)
                cmd.Parameters.AddWithValue("?", asn.PadRight(3));

            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var code = SafeExtractString(rdr, 0).Trim();
                var desc = SafeExtractString(rdr, 1).Trim();
                if (!string.IsNullOrEmpty(code) && !assocDict.ContainsKey(code))
                {
                    assocDict[code] = desc;
                }
            }
        }
        catch (OdbcException ex)
        {
            _logger.LogWarning(ex,
                "Failed to load association names from FCMCCRL2; will use codes only. Associations: {Associations}",
                string.Join(", ", assnList));
        }

        // Ensure all associations have an entry (use code as fallback if not found in DB)
        foreach (var asn in assnList)
        {
            if (!assocDict.ContainsKey(asn))
                assocDict[asn] = asn;
        }

        return assocDict;
    }

    private object GetTransmitterInfo()
    {
        return new
        {
            CompanyName = _xmtrCompanyName,
            CompanyName2 = _xmtrCompanyName2,
            Address = _xmtrAddress,
            City = _xmtrCity,
            State = _xmtrState,
            Zip = _xmtrZip,
            ContactName = _xmtrContactName,
            ContactPhone = _xmtrContactPhone,
            ContactEmail = _xmtrContactEmail,
            TIN = _xmtrTIN,
            FEI = _xmtrFEI
        };
    }

    public async Task<IList<AssociationRow>> GetAvailableAssociationsAsync(string taxYear)
    {
        // Get distinct associations from TXRDTL for the tax year
        var query = _db.TaxDetails
            .Where(d => d.TaxYear == taxYear.Trim())
            .Select(d => d.Asa)
            .Distinct()
            .OrderBy(a => a);

        var localAssas = await query.ToListAsync();

        // Try to get from IBM i as well and merge
        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            
            var sql = $"SELECT DISTINCT ASA FROM {_lib}/TXRDTL WHERE TAXYR=? ORDER BY ASA";
            await using var cmd = new OdbcCommand(sql, conn);
            cmd.Parameters.AddWithValue("?", int.TryParse(taxYear, out var yr) ? yr : 0);
            
            var ibmiAssas = new List<string>();
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var asa = SafeExtractString(rdr, 0);
                if (!string.IsNullOrEmpty(asa) && !ibmiAssas.Contains(asa))
                {
                    ibmiAssas.Add(asa);
                }
            }

            // Merge: prefer IBM i data, overlay with local
            var merged = ibmiAssas.ToDictionary(a => a, StringComparer.OrdinalIgnoreCase);
            foreach (var local in localAssas)
            {
                merged[local] = local;
            }

            return merged.Values
                .OrderBy(a => a)
                .Select(a => new AssociationRow { CorpCode = a })
                .ToList();
        }
        catch (OdbcException ex)
        {
            _logger.LogWarning(ex,
                "IBM i query unavailable while loading associations for year {TaxYear}; using local records only.",
                taxYear);
            
            return localAssas
                .Select(a => new AssociationRow { CorpCode = a })
                .ToList();
        }
    }
}
