using System.Data.Odbc;

namespace Tx9501.Services;

/// <summary>
/// Extended source query service for all TX9515 form types.
/// Queries DDMASTR (1099-INT), SHCRCT/SHCRPR (1099-DIV), LNMENDR, FCLMSTR, etc.
/// </summary>
public partial class TaxDetailSourceService
{
    /// <summary>
    /// Query DDMASTR (Deposit/Demand Deposit Master) for 1099-INT tax records.
    /// Replaces the $EARN_BUILD subroutine logic from TX9515.
    /// </summary>
    public async Task<List<DepositMasterRecord>> QueryDepositMasterAsync(
        int taxYear,
        string branchLib,
        string corpCode)
    {
        return await Task.Run(() =>
        {
            var rows = new List<DepositMasterRecord>();
            const string sql = @"SELECT ACCTNO, SNAME, STATE, YTDINT, YTDFWH, OIDCOD
                                FROM DATCLIQ/DDMASTR
                                WHERE SRPCOD <> 'Y'
                                FETCH FIRST 500 ROWS ONLY";

            try
            {
                using var conn = new OdbcConnection(_cs);
                conn.ConnectionTimeout = 30;
                conn.Open();

                using var cmd = new OdbcCommand(sql, conn) { CommandTimeout = 30 };
                using var rdr = cmd.ExecuteReader();

                while (rdr.Read())
                {
                    decimal SafeDecimal(int index, decimal def = 0)
                    {
                        try
                        {
                            if (rdr.IsDBNull(index)) return def;
                            var v = rdr.GetValue(index);
                            if (v is decimal d) return d;
                            if (v is int i) return i;
                            if (v is long l) return l;
                            if (v is double dbl) return (decimal)dbl;
                            if (decimal.TryParse(v?.ToString() ?? "", out var parsed)) return parsed;
                            return def;
                        }
                        catch { return def; }
                    }

                    string SafeString(int index) => (rdr.IsDBNull(index) ? "" : rdr.GetValue(index)?.ToString() ?? "").Trim();

                    rows.Add(new DepositMasterRecord
                    {
                        AccountNo = SafeDecimal(0),
                        CisNo     = SafeDecimal(0),
                        MbrNo     = SafeDecimal(0),
                        Dept      = 0,
                        OidCod    = SafeString(5),
                        SsiDn     = 0,
                        SsiDc     = "",
                        BorrName  = SafeString(1),
                        BorrAddr  = "",
                        BorrAddrX = "",
                        BorrCity  = "",
                        BorrState = SafeString(2),
                        BorrZip   = 0,
                        Branch    = 0,
                        YtdOid    = 0,
                        YtdInt    = SafeDecimal(3),
                        YtdFwh    = SafeDecimal(4),
                        LyrOid    = 0,
                        LyrInt    = 0,
                        LyrFwh    = 0,
                    });
                }

                Console.WriteLine($"[DEBUG] QueryDepositMasterAsync: Found {rows.Count} DDMASTR records");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] QueryDepositMasterAsync: Exception - {ex.Message}");
                _logger.LogWarning(ex,
                    "Failed to query DDMASTR for tax year {TaxYear}, branch {BranchLib}",
                    taxYear, branchLib);
            }

            return rows;
        });
    }

    /// <summary>
    /// Query SHCRCT (Capital Reduction Control) and SHCRPR (Capital Reduction Detail)
    /// for 1099-DIV and 1099-PATR tax records.
    /// Replaces the $CR_BUILD subroutine logic from TX9515.
    /// </summary>
    public async Task<List<CapitalReductionRecord>> QueryCapitalReductionsAsync(
        int taxYear,
        string branchLib,
        string corpCode,
        string formName)
    {
        var rows = new List<CapitalReductionRecord>();
        
        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();

            // branchLib is FMGLPC (group code). Look up FMDLIB from FCMCCR using FMGLPC
            var fmdlib = await GetFacilityLibraryByGroupAsync(branchLib);
            if (string.IsNullOrWhiteSpace(fmdlib))
            {
                Console.WriteLine($"[DEBUG] QueryCapitalReductionsAsync: No FMDLIB found for group '{branchLib}' - form {formName}");
                _logger.LogWarning("Cannot query SHCRCT/SHCRPR: No FMDLIB found for group {GroupCode}", branchLib);
                return rows;
            }

            // SHCRCT and SHCRPR are in the library obtained from FCMCCR (FMDLIB)
            var sql = $@"
                SELECT c.CRPYET, c.CRLACT, c.CRLCIS, c.CPSACT, c.CPSCIS,
                       c.CRTYPE, c.CPBRCH, c.FORMNAME,
                       p.CPAMDS, p.CPAMWH, p.CPPTDC, p.CPEICD, p.CPOTIN
                FROM {fmdlib}/SHCRCT c
                JOIN {fmdlib}/SHCRPR p ON c.CRCTL = p.CRCTL
                WHERE c.CRDSB7 >= ? AND c.CRDSB7 <= ?
                  AND ((c.FORMNAME = '1099-DIV' AND c.CRTYPE = 'D' AND c.CRSTAG = 99) OR
                       (c.FORMNAME = '1099-PATR' AND c.CRTYPE = 'P' AND c.CRSTAG = 99))
                  AND p.CPEICD = 'I'
                  AND p.CPAMDS > 0
                ORDER BY c.CRCTL, p.CRCTL";

            await using var cmd = new OdbcCommand(sql, conn);
            cmd.Parameters.AddWithValue("?", taxYear * 1000);         // Start of year (CYYDDD)
            cmd.Parameters.AddWithValue("?", (taxYear * 1000) + 366); // End of year

            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                rows.Add(new CapitalReductionRecord
                {
                    CrpYet   = rdr.IsDBNull(0) ? "" : rdr.GetString(0).Trim(),
                    CrLact   = rdr.IsDBNull(1) ? 0 : rdr.GetDecimal(1),
                    CrLcis   = rdr.IsDBNull(2) ? "" : rdr.GetString(2).Trim(),
                    CpsAct   = rdr.IsDBNull(3) ? 0 : rdr.GetDecimal(3),
                    CpScis   = rdr.IsDBNull(4) ? "" : rdr.GetString(4).Trim(),
                    CrType   = rdr.IsDBNull(5) ? "" : rdr.GetString(5).Trim(),
                    CpBrch   = rdr.IsDBNull(6) ? 0 : rdr.GetDecimal(6),
                    FormName = rdr.IsDBNull(7) ? "" : rdr.GetString(7).Trim(),
                    CpAmds   = rdr.IsDBNull(8) ? 0 : rdr.GetDecimal(8),
                    CpAmwh   = rdr.IsDBNull(9) ? 0 : rdr.GetDecimal(9),
                    CpPtdc   = rdr.IsDBNull(10) ? 0 : rdr.GetDecimal(10),
                    CpEicd   = rdr.IsDBNull(11) ? "" : rdr.GetString(11).Trim(),
                    CpOtin   = rdr.IsDBNull(12) ? 0 : rdr.GetDecimal(12),
                });
            }
            Console.WriteLine($"[DEBUG] QueryCapitalReductionsAsync: Found {rows.Count} capital reduction records in '{fmdlib}' for form {formName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] QueryCapitalReductionsAsync: Exception for form {formName} - {ex.Message}");
            _logger.LogWarning(ex,
                "Failed to query capital reductions for tax year {TaxYear}, corp {CorpCode}",
                taxYear, corpCode);
        }

        return rows;
    }

    /// <summary>
    /// Query LNMENDR (Loan Month-End Detail) for unpaid principal balance.
    /// Used to populate UNPPRN field in 1098 tax detail records.
    /// </summary>
    public async Task<MonthEndBalanceRecord?> QueryMonthEndBalanceAsync(
        decimal accountNo,
        int endOfYearJulianDate)
    {
        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            
            const string sql = @"
                SELECT MNPBLN
                FROM {0}/LNMENDR
                WHERE ACCTNO = ? AND MNDTE7 = ?
                LIMIT 1";
            
            await using var cmd = new OdbcCommand(string.Format(sql, _lib), conn);
            cmd.Parameters.AddWithValue("?", accountNo);
            cmd.Parameters.AddWithValue("?", endOfYearJulianDate);
            
            await using var rdr = await cmd.ExecuteReaderAsync();
            if (await rdr.ReadAsync())
            {
                return new MonthEndBalanceRecord
                {
                    UnpaidPrincipal = rdr.IsDBNull(0) ? 0 : rdr.GetDecimal(0)
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to query LNMENDR for account {AccountNo}, Julian date {JulianDate}",
                accountNo, endOfYearJulianDate);
        }

        return null;
    }

    /// <summary>
    /// Query FCLMSTR (Facility/Collateral Master) for security address and description.
    /// Used to populate security-related fields in 1098 tax detail records.
    /// </summary>
    public async Task<FacilitySecurityRecord?> QueryFacilitySecurityAsync(
        decimal facilityCode)
    {
        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            
            const string sql = @"
                SELECT FCADD, FCCTY, FCST, FCZP, FCDSC
                FROM {0}/FCLMSTR
                WHERE FCFAC = ?
                LIMIT 1";
            
            await using var cmd = new OdbcCommand(string.Format(sql, _lib), conn);
            cmd.Parameters.AddWithValue("?", facilityCode);
            
            await using var rdr = await cmd.ExecuteReaderAsync();
            if (await rdr.ReadAsync())
            {
                return new FacilitySecurityRecord
                {
                    SecurityAddress = rdr.IsDBNull(0) ? "" : rdr.GetString(0).Trim(),
                    SecurityCity = rdr.IsDBNull(1) ? "" : rdr.GetString(1).Trim(),
                    SecurityState = rdr.IsDBNull(2) ? "" : rdr.GetString(2).Trim(),
                    SecurityZip = rdr.IsDBNull(3) ? "" : rdr.GetString(3).Trim(),
                    SecurityDescription = rdr.IsDBNull(4) ? "" : rdr.GetString(4).Trim(),
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to query FCLMSTR for facility {FacilityCode}",
                facilityCode);
        }

        return null;
    }

    /// <summary>
    /// Query LNARECR (Loan A/R Records) for interest that should be included in 1098.
    /// Used to add A/R interest to INTPD calculation.
    /// </summary>
    public async Task<ARecordInterestRecord?> QueryARecordInterestAsync(
        decimal accountNo,
        int taxYear)
    {
        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            
            const string sql = @"
                SELECT COALESCE(SUM(CASE WHEN YEAR(ARYRDT7) = ? THEN ARYTDI ELSE 0 END), 0) as YTD_INT,
                       COALESCE(SUM(CASE WHEN YEAR(ARYRDT7) = ? - 1 THEN ARYTDI ELSE 0 END), 0) as LYR_INT
                FROM {0}/LNARECR
                WHERE ARACCT = ? AND ARYRDT7 > 0
                GROUP BY ARACCT";
            
            await using var cmd = new OdbcCommand(string.Format(sql, _lib), conn);
            cmd.Parameters.AddWithValue("?", taxYear);
            cmd.Parameters.AddWithValue("?", taxYear);
            cmd.Parameters.AddWithValue("?", accountNo);
            
            await using var rdr = await cmd.ExecuteReaderAsync();
            if (await rdr.ReadAsync())
            {
                return new ARecordInterestRecord
                {
                    YearToDateInterest = rdr.IsDBNull(0) ? 0 : rdr.GetDecimal(0),
                    PriorYearInterest = rdr.IsDBNull(1) ? 0 : rdr.GetDecimal(1)
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to query LNARECR for account {AccountNo}, year {TaxYear}",
                accountNo, taxYear);
        }

        return new ARecordInterestRecord();
    }

    /// <summary>
    /// Query LNCREFR and LNCOLLR (Collateral References) to count mortgaged properties.
    /// Used to populate SECNUM (number of mortgaged properties) in 1098 records.
    /// </summary>
    public async Task<int> QueryCollateralCountAsync(decimal accountNo)
    {
        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            
            const string sql = @"
                SELECT COUNT(DISTINCT c.COLL)
                FROM {0}/LNCREFR r
                JOIN {0}/LNCOLLR c ON r.COLL = c.COLL
                WHERE r.ACCTNO = ?
                  AND c.COLLTYP IN ('MT', 'MR')
                  AND c.COLLST = 'A'";
            
            await using var cmd = new OdbcCommand(string.Format(sql, _lib), conn);
            cmd.Parameters.AddWithValue("?", accountNo);
            
            var result = await cmd.ExecuteScalarAsync();
            return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to query collateral count for account {AccountNo}",
                accountNo);
            return 0;
        }
    }
}

public class DepositMasterRecord
{
    public decimal AccountNo { get; set; }
    public decimal CisNo { get; set; }
    public decimal MbrNo { get; set; }
    public decimal Dept { get; set; }
    public string OidCod { get; set; } = string.Empty;
    public decimal SsiDn { get; set; }
    public string SsiDc { get; set; } = string.Empty;
    public string BorrName { get; set; } = string.Empty;
    public string BorrAddr { get; set; } = string.Empty;
    public string BorrAddrX { get; set; } = string.Empty;
    public string BorrCity { get; set; } = string.Empty;
    public string BorrState { get; set; } = string.Empty;
    public decimal BorrZip { get; set; }
    public decimal Branch { get; set; }
    public decimal YtdOid { get; set; }
    public decimal YtdInt { get; set; }
    public decimal YtdFwh { get; set; }
    public decimal LyrOid { get; set; }
    public decimal LyrInt { get; set; }
    public decimal LyrFwh { get; set; }
}

public class CapitalReductionRecord
{
    public string CrpYet { get; set; } = string.Empty;
    public decimal CrLact { get; set; }
    public string CrLcis { get; set; } = string.Empty;
    public decimal CpsAct { get; set; }
    public string CpScis { get; set; } = string.Empty;
    public string CrType { get; set; } = string.Empty;
    public decimal CpBrch { get; set; }
    public string FormName { get; set; } = string.Empty;
    public decimal CpAmds { get; set; }
    public decimal CpAmwh { get; set; }
    public decimal CpPtdc { get; set; }
    public string CpEicd { get; set; } = string.Empty;
    public decimal CpOtin { get; set; }
}

public class MonthEndBalanceRecord
{
    public decimal UnpaidPrincipal { get; set; }
}

public class FacilitySecurityRecord
{
    public string SecurityAddress { get; set; } = string.Empty;
    public string SecurityCity { get; set; } = string.Empty;
    public string SecurityState { get; set; } = string.Empty;
    public string SecurityZip { get; set; } = string.Empty;
    public string SecurityDescription { get; set; } = string.Empty;
}

public class ARecordInterestRecord
{
    public decimal YearToDateInterest { get; set; }
    public decimal PriorYearInterest { get; set; }
}
