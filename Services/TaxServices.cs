using System.Data.Odbc;
using Microsoft.EntityFrameworkCore;
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
        // Simplified – no BKGLIB authorization check, read all associations from FCMCCRL2
        const string sql =
            "SELECT FMBNUM, FMDLIB, FMGLPC, FMDSC, FMASTY, FMPALB " +
            "FROM {0}/FCMCCRL2 " +
            "ORDER BY FMGLPC";

        return await ReadAssocRowsAsync(string.Format(sql, _lib), null);
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

    public async Task ClearAsync(string taxYear, string formName,
                                  IEnumerable<string> associations, bool selectAll)
    {
        var asns = associations.ToList();

        await ClearLocalAsync(taxYear, formName, asns, selectAll);

        await using var conn = new OdbcConnection(_cs);
        await conn.OpenAsync();

        foreach (var asa in (selectAll ? await GetAllAsnsAsync(conn) : asns))
        {
            // DELETE FROM TXRDTL (mirrors tx9510 SQL)
            await DeleteAsync(conn,
                $"DELETE FROM {_lib}/TXRDTL WHERE TAXYR=? AND FORM=? AND ASA=?",
                taxYear, formName, asa);

            // DELETE FROM TXRAUD
            await DeleteAsync(conn,
                $"DELETE FROM {_lib}/TXRAUD WHERE TAXYR=? AND FORM=? AND ASA=?",
                taxYear, formName, asa);
        }
    }

    private async Task ClearLocalAsync(string taxYear, string formName, IList<string> associations, bool selectAll)
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

        _db.TaxDetails.RemoveRange(await query.ToListAsync());
        _db.TaxAudits.RemoveRange(await auditQuery.ToListAsync());
        await _db.SaveChangesAsync();
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
/// Replaces tx9515.sqlrpgle + tx9540.sqlrpgle.
/// Delegates the actual processing to the IBM i programs via ODBC CALL.
/// </summary>
public sealed class BuildTaxDataService : IBuildTaxDataService
{
    private readonly string _cs;
    private readonly string _lib;
    private readonly LocalDbContext _db;
    private readonly ILogger<BuildTaxDataService> _logger;

    public BuildTaxDataService(IConfiguration cfg, LocalDbContext db,
                               ILogger<BuildTaxDataService> logger)
    {
        _cs     = cfg.GetConnectionString("IBMi")!;
        _lib    = cfg["IBMiSettings:Library"] ?? "TXLIB";
        _db     = db;
        _logger = logger;
    }

    public async Task BuildAsync(string taxYear, string formName,
                                  IEnumerable<string> associations, bool selectAll)
    {
        var assocList = associations
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sourceRows = new List<TaxDetailRecord>();
        try
        {
            sourceRows = await QueryBuildSourceFromIbmiAsync(taxYear, formName, assocList, selectAll);
        }
        catch (OdbcException ex)
        {
            _logger.LogWarning(ex,
                "IBM i source query failed during BUILD for year {TaxYear}, form {FormName}; falling back to local staging data.",
                taxYear, formName);
            sourceRows = await QueryBuildSourceFromLocalAsync(taxYear, formName, assocList, selectAll);
        }

        // Batch process all rows at once instead of one-by-one
        await BatchUpsertLocalTaxDetailsAsync(sourceRows);

        _logger.LogInformation("Web-side BUILD completed for year {TaxYear}, form {FormName}. Rows staged: {Count}",
            taxYear, formName, sourceRows.Count);
    }

    private async Task<List<TaxDetailRecord>> QueryBuildSourceFromIbmiAsync(
        string taxYear, string formName, IList<string> associations, bool selectAll)
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

    public ValidateTaxService(IConfiguration cfg, LocalDbContext db, ILogger<ValidateTaxService> logger)
    {
        _cs     = cfg.GetConnectionString("IBMi")!;
        _lib    = cfg["IBMiSettings:Library"] ?? "TXLIB";
        _db     = db;
        _logger = logger;
    }

    public async Task<int> ValidateAsync(string taxYear, string formName,
                                          IEnumerable<string> associations, bool selectAll)
    {
        var rows = await GetCandidateRecordsAsync(taxYear, formName, associations, selectAll);
        var flagged = 0;

        foreach (var record in rows)
        {
            record.Errors = HasValidationError(record) ? "Y" : string.Empty;
            if (record.Errors == "Y")
            {
                flagged++;
            }

            await UpsertLocalRecordAsync(record);
        }

        return flagged;
    }

    private async Task<IList<TaxDetailRecord>> GetCandidateRecordsAsync(
        string taxYear, string formName, IEnumerable<string> associations, bool selectAll)
    {
        var localRows = await QueryLocalAsync(taxYear, formName, associations, selectAll);

        try
        {
            var ibmiRows = await QueryIbmiAsync(taxYear, formName, associations, selectAll);
            var merged = ibmiRows.ToDictionary(GetDetailKey, StringComparer.OrdinalIgnoreCase);
            foreach (var local in localRows)
            {
                merged[GetDetailKey(local)] = local;
            }

            return merged.Values.ToList();
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

    private async Task UpsertLocalRecordAsync(TaxDetailRecord record)
    {
        var local = NormalizeLocalRecord(record);
        var existing = await _db.TaxDetails.FirstOrDefaultAsync(d =>
            d.TaxYear == local.TaxYear &&
            d.Form == local.Form &&
            d.Asa == local.Asa &&
            d.MbrNo == local.MbrNo &&
            d.MbrSub == local.MbrSub);

        if (existing is null)
        {
            _db.TaxDetails.Add(local);
        }
        else
        {
            local.Id = existing.Id;
            local.CreatedAt = existing.CreatedAt;
            local.UpdatedAt = DateTime.UtcNow;
            _db.Entry(existing).CurrentValues.SetValues(local);
        }

        await _db.SaveChangesAsync();
    }

    private static bool HasValidationError(TaxDetailRecord record)
    {
        var ssiDc = NormalizeText(record.SsiDc).ToUpperInvariant();
        var foreign = NormalizeText(record.Foreign).Equals("Y", StringComparison.OrdinalIgnoreCase);
        var reportToIrs = NormalizeText(record.ReportToIrs).ToUpperInvariant();
        var nonRptReason = NormalizeText(record.NonRptReason);

        if (record.SsiDn <= 0) return true;
        if (ssiDc is not "S" and not "E") return true;
        if (string.IsNullOrWhiteSpace(record.BorrName)) return true;
        if (string.IsNullOrWhiteSpace(record.BorrAddr)) return true;
        if (string.IsNullOrWhiteSpace(record.BorrCity)) return true;
        if (!foreign && string.IsNullOrWhiteSpace(record.BorrState)) return true;
        if (!foreign && record.BorrZip <= 0) return true;
        if (reportToIrs is not "Y" and not "N") return true;
        if (reportToIrs == "N" && string.IsNullOrWhiteSpace(nonRptReason)) return true;
        if (reportToIrs == "Y" && !string.IsNullOrWhiteSpace(nonRptReason)) return true;

        return false;
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
        => $"{NormalizeText(record.TaxYear)}|{NormalizeText(record.Form)}|{NormalizeText(record.Asa)}|{record.MbrNo}|{NormalizeText(record.MbrSub)}";

    private static string NormalizeText(string? value) => (value ?? string.Empty).Trim();

    private static string SafeValidationString(System.Data.Common.DbDataReader r, int col)
    {
        if (r.IsDBNull(col)) return string.Empty;
        var val = r.GetValue(col);
        if (val is DateTime dt) return dt.ToString("yyyyMMdd");
        return val.ToString()?.Trim() ?? string.Empty;
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
                   SUM(INTPD)  AS A1, SUM(POINTS) AS A2, SUM(INTERN) AS A3
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
            var a1     = rdr.IsDBNull(3) ? 0m : rdr.GetDecimal(3);
            var a2     = rdr.IsDBNull(4) ? 0m : rdr.GetDecimal(4);
            var a3     = rdr.IsDBNull(5) ? 0m : rdr.GetDecimal(5);

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
            return local.ToRecord();
        }

        // ── Step 2: IBM i TXRDTL fallback ─────────────────────────────────
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
                            WTHHELD,ERRORS,RPT_TO_IRS,CORRIN,ORIGDATE,SECSAME,SECADDR,SECDESC,
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
                taxYear, formName, asa, mbrNo, mbrSub);
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

        // SQLite local error records
        var localErrors = await _db.TaxDetails
            .Where(d => d.TaxYear == taxYear && d.Form == formName &&
                        d.Asa == asa && d.Errors == "Y")
            .ToListAsync();
        rows.AddRange(localErrors
            .OrderBy(d => d.MbrNo)
            .Select(d => d.ToRecord()));

        // IBM i error records
        var sql = $@"SELECT TAXYR,FORM,ASA,MBRNO,MBRSUB,SSIDN,BNM,BAD,BADX,BCTY,BST,BZP,
                            SSIDC,INTPD,POINTS,INTERN,ERNWTH,COMPEN,RENTS,MEDPAY,LGLPAY,OTHER,
                            WTHHELD,ERRORS,RPT_TO_IRS,CORRIN,ORIGDATE,SECSAME,SECADDR,SECDESC,
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
                                WTHHELD,ERRORS,RPT_TO_IRS,CORRIN,ORIGDATE,SECSAME,SECADDR,SECDESC,
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
        Errors     = SafeGetString(r, 23),
        ReportToIrs = SafeGetString(r, 24),
        CorrIn     = SafeGetString(r, 25),
        OrigDate   = SafeGetString(r, 26),   // ORIGDATE  — may be DateTime from ODBC
        SecSame    = SafeGetString(r, 27),
        SecAddr    = SafeGetString(r, 28),
        SecDesc    = SafeGetString(r, 29),
        SecOther   = SafeGetString(r, 30),
        SecNum     = r.IsDBNull(31) ? 0 : r.GetDecimal(31),
        MtgAcqDt  = SafeGetString(r, 32),   // MTGACQDT  — may be DateTime from ODBC
        UnpPrn    = r.IsDBNull(33) ? 0 : r.GetDecimal(33),
        FmVal     = r.IsDBNull(34) ? 0 : r.GetDecimal(34),
        DteAqr    = SafeGetString(r, 35),    // DTEAQR    — may be DateTime from ODBC
        PrDesc    = SafeGetString(r, 36),
        Foreign   = SafeGetString(r, 37),
        Dept      = r.IsDBNull(38) ? 0 : r.GetDecimal(38),
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

        try
        {
            var ibmiRows = await QueryIbmiAsync(taxYear, formName, assocList, selectAll);
            var merged = ibmiRows.ToDictionary(GetDetailKey, StringComparer.OrdinalIgnoreCase);
            foreach (var local in localRows)
            {
                merged[GetDetailKey(local)] = local;
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

    public async Task<PagedResult<TaxDetailRecord>> GetDetailReportPageAsync(
        string taxYear, string formName, IEnumerable<string> associations, bool selectAll,
        int pageNumber, int pageSize, TaxDetailListMode mode)
    {
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Max(1, pageSize);

        var assocList = associations.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var localBaseQuery = BuildLocalReportQuery(taxYear, formName, assocList, selectAll);
        var localSourceCount = await localBaseQuery.CountAsync();

        if (localSourceCount > 0)
        {
            var filteredLocalRows = await ApplyReportMode(localBaseQuery, mode)
                .Select(d => d.ToRecord())
                .ToListAsync();

            var orderedLocalRows = filteredLocalRows
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

        return await QueryIbmiReportPageAsync(taxYear, formName, assocList, selectAll, pageNumber, pageSize, mode);
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
            await _ibmi.ExecuteProgramAsync("TX9591R", ctrl, assnStr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TX9591R unavailable for letter processing (TaxYear={TaxYear}). Falling back to web-only completion message.",
                taxYear);
        }
    }

    private async Task CallAsync(string pgm, string taxYear, string formName,
                                  IEnumerable<string> associations, bool selectAll)
    {
        var ctrl    = new Models.TaxControlRecord { TaxYear = taxYear }.ToControlString();
        var assnStr = BuildAssocParam(associations.ToList(), selectAll);
        await _ibmi.ExecuteProgramAsync(pgm, ctrl, formName.PadRight(9), assnStr);
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
                            WTHHELD,ERRORS,RPT_TO_IRS,CORRIN,ORIGDATE,SECSAME,SECADDR,SECDESC,
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
                                WTHHELD,ERRORS,RPT_TO_IRS,CORRIN,ORIGDATE,SECSAME,SECADDR,SECDESC,
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
        CorrIn     = SafeReportString(r, 25),
        OrigDate   = SafeReportString(r, 26),
        SecSame    = SafeReportString(r, 27),
        SecAddr    = SafeReportString(r, 28),
        SecDesc    = SafeReportString(r, 29),
        SecOther   = SafeReportString(r, 30),
        SecNum     = r.IsDBNull(31) ? 0 : r.GetDecimal(31),
        MtgAcqDt   = SafeReportString(r, 32),
        UnpPrn     = r.IsDBNull(33) ? 0 : r.GetDecimal(33),
        FmVal      = r.IsDBNull(34) ? 0 : r.GetDecimal(34),
        DteAqr     = SafeReportString(r, 35),
        PrDesc     = SafeReportString(r, 36),
        Foreign    = SafeReportString(r, 37),
        Dept       = r.IsDBNull(38) ? 0 : r.GetDecimal(38),
    };

    private static string SafeReportString(System.Data.Common.DbDataReader r, int col)
    {
        if (r.IsDBNull(col)) return string.Empty;
        var val = r.GetValue(col);
        if (val is DateTime dt) return dt.ToString("yyyyMMdd");
        return val.ToString()?.Trim() ?? string.Empty;
    }

    private static string GetDetailKey(TaxDetailRecord record)
        => $"{NormalizeReportText(record.TaxYear)}|{NormalizeReportText(record.Form)}|{NormalizeReportText(record.Asa)}|{record.MbrNo}|{NormalizeReportText(record.MbrSub)}";

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

    private readonly LocalDbContext _db;
    private bool _tx9565rUnavailable;

    public ExtractService(IIBMiService ibmi, IConfiguration cfg,
                           LocalDbContext db, ILogger<ExtractService> logger)
    {
        _ibmi   = ibmi;
        _cs     = cfg.GetConnectionString("IBMi")!;
        _lib    = cfg["IBMiSettings:Library"] ?? "TXLIB";
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
                    ExtSeq     = rdr.GetDecimal(1),
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

    public async Task<decimal> CreateExtractAsync(string taxYear, string description,
                                                    string selectDate, string xmtrName,
                                                    string xmtrName2)
    {
        // Compute next sequence number from both IBM i and SQLite.
        decimal ibmiMax = 0m;
        try
        {
            var seqSql = $"SELECT COALESCE(MAX(EXT_SEQ),0) FROM {_lib}/TXIRST WHERE TAXYR=?";
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            await using var seqCmd = new OdbcCommand(seqSql, conn);
            seqCmd.Parameters.AddWithValue("?", taxYear);
            ibmiMax = Convert.ToDecimal(await seqCmd.ExecuteScalarAsync() ?? 0m);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read max extract sequence from IBM i for TaxYear={TaxYear}.", taxYear);
        }

        decimal localMax = (await _db.Extracts
            .Where(e => e.TaxYear == taxYear)
            .ToListAsync())
            .Max(e => (decimal?)e.ExtSeq) ?? 0m;

        decimal nextSeq = Math.Max(ibmiMax, localMax) + 1m;

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

    public async Task ClearExtractAsync(string taxYear, decimal extSeq)
    {
        // Mirrors TX9561: DELETE from TXIRSB/B3/C/A/F then clear TXIRSX member
        var ctrl = new Models.TaxControlRecord { TaxYear = taxYear }.ToControlString();
        try
        {
            await _ibmi.ExecuteProgramAsync("TX9561", ctrl,
                extSeq.ToString().PadLeft(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TX9561 unavailable for clear (TaxYear={TaxYear}, ExtSeq={ExtSeq}). Applying local web fallback clear.",
                taxYear, extSeq);

            // Local fallback: remove generated extract file and reset local staged extract count.
            var filePath = GetExtractFilePath(taxYear, extSeq);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            var localExtract = await _db.Extracts
                .FirstOrDefaultAsync(e => e.TaxYear == taxYear && e.ExtSeq == extSeq);
            if (localExtract is not null)
            {
                localExtract.BRecsT = 0;
                localExtract.ExtDate = DateTime.UtcNow.ToString("yyyyMMdd");
                await _db.SaveChangesAsync();
            }
        }
    }

    public async Task BuildIrsFileAsync(string taxYear, decimal extSeq,
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
        var lines = BuildExtractLines(taxYear, extSeq, formSet, assnSet, records);
        await PersistGeneratedExtractFileAsync(taxYear, extSeq, lines);

        var count = records.LongCount();
        await UpsertLocalExtractHeaderAsync(taxYear, extSeq, count);
    }

    public async Task<(string FileName, byte[] Content)?> DownloadExtractAsync(string taxYear, decimal extSeq)
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

    private static List<string> BuildExtractLines(
        string taxYear,
        decimal extSeq,
        IList<string> forms,
        IList<string> associations,
        IList<TaxDetailRecord> records)
    {
        var lines = new List<string>();
        var buildTs = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        lines.Add($"H|{taxYear}|{extSeq:00000}|{buildTs}|FORMS={string.Join(',', forms)}|ASSNS={string.Join(',', associations)}");

        // TX9563-equivalent behavior: only reportable rows are included in IRS extract output.
        var reportable = records
            .Where(r => string.Equals(Clean(r.ReportToIrs), "Y", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var recNo = 0L;
        var grpNo = 0;
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

            // A record: group header for form+association (similar to RPG's per-form/corp setup).
            lines.Add(string.Join('|', new[]
            {
                "A",
                grpNo.ToString(),
                grp.Key.Form,
                grp.Key.Asa,
                grpCount.ToString(),
                FmtMoney(grpPrimaryAmt),
                FmtMoney(grpWithheld),
                grpCorrections.ToString()
            }));

            foreach (var r in grp)
            {
                recNo++;
                // B record: detail row payload.
                lines.Add(string.Join('|', new[]
                {
                    "B",
                    recNo.ToString(),
                    grpNo.ToString(),
                    Clean(r.Form),
                    Clean(r.Asa),
                    r.MbrNo.ToString("0"),
                    Clean(r.MbrSub),
                    r.SsiDn.ToString("0"),
                    Clean(r.SsiDc),
                    Clean(r.BorrName),
                    Clean(r.BorrAddr),
                    Clean(r.BorrAddrX),
                    Clean(r.BorrCity),
                    Clean(r.BorrState),
                    r.BorrZip.ToString("0"),
                    FmtMoney(r.IntPd),
                    FmtMoney(r.Points),
                    FmtMoney(r.InterN),
                    FmtMoney(r.Compen),
                    FmtMoney(r.Rents),
                    FmtMoney(r.MedPay),
                    FmtMoney(r.LglPay),
                    FmtMoney(r.Other),
                    FmtMoney(r.WthHeld),
                    FmtMoney(r.UnpPrn),
                    FmtMoney(r.FmVal),
                    Clean(r.CorrIn),
                    Clean(r.Foreign)
                }));
            }

            // C record: group trailer/control totals.
            lines.Add(string.Join('|', new[]
            {
                "C",
                grpNo.ToString(),
                grp.Key.Form,
                grp.Key.Asa,
                grpCount.ToString(),
                FmtMoney(grpPrimaryAmt),
                FmtMoney(grpWithheld)
            }));
        }

        // F record: file trailer.
        lines.Add(string.Join('|', new[]
        {
            "F",
            recNo.ToString(),
            grpNo.ToString(),
            buildTs
        }));
        return lines;
    }

    private async Task PersistGeneratedExtractFileAsync(string taxYear, decimal extSeq, IList<string> lines)
    {
        var filePath = GetExtractFilePath(taxYear, extSeq);
        var dirPath = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dirPath);
        await File.WriteAllLinesAsync(filePath, lines);
    }

    private string GetExtractFilePath(string taxYear, decimal extSeq)
    {
        var safeYear = (taxYear ?? string.Empty).Trim();
        var safeSeq = extSeq.ToString("00000");
        var baseDir = Path.Combine(AppContext.BaseDirectory, "extract-output");
        return Path.Combine(baseDir, $"IRS_{safeYear}_{safeSeq}.txt");
    }

    private async Task UpsertLocalExtractHeaderAsync(string taxYear, decimal extSeq, long count)
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
        => $"{record.TaxYear.Trim()}|{record.Form.Trim()}|{record.Asa.Trim()}|{record.MbrNo}|{record.MbrSub.Trim()}";

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

    public async Task TransmitExtractAsync(string taxYear, decimal extSeq)
    {
        if (_tx9565rUnavailable)
        {
            _logger.LogWarning(
                "TX9565R previously detected as unavailable. Skipping IBM i transmit call for TaxYear={TaxYear}, ExtSeq={ExtSeq} and using web-only completion.",
                taxYear, extSeq);
            return;
        }

        var ctrl = new Models.TaxControlRecord { TaxYear = taxYear }.ToControlString();
        try
        {
            await _ibmi.ExecuteProgramAsync("TX9565R", ctrl, extSeq.ToString().PadLeft(5));
        }
        catch (OdbcException ex) when (ex.Message.Contains("SQL0204", StringComparison.OrdinalIgnoreCase)
                                      && ex.Message.Contains("TX9565R", StringComparison.OrdinalIgnoreCase))
        {
            _tx9565rUnavailable = true;
            _logger.LogWarning(
                "TX9565R not found on IBM i (SQL0204) for TaxYear={TaxYear}, ExtSeq={ExtSeq}. Using web-only completion until program is restored.",
                taxYear, extSeq);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TX9565R unavailable for transmit (TaxYear={TaxYear}, ExtSeq={ExtSeq}). Falling back to web-only completion message.",
                taxYear, extSeq);
        }
    }

    public async Task<long> GetExtractRecordCountAsync(string taxYear, decimal extSeq)
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
}
