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
    /// DDMASTR is always in DATCLIQ library (not in association-specific branches).
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

            // Columns ordered for positional reader access (0-based index comments below).
            // Some legacy TX9515 fields (MBRNO/SSIDN/address) are not present in current DDMASTR;
            // placeholders preserve positional mapping for downstream transform logic.
            var sql = @"SELECT
                  ACCTNO,   -- 0
                  CISNO,    -- 1
                CAST(0 AS DECIMAL(11,0)) AS MBRNO,   -- 2 placeholder
                CAST(0 AS DECIMAL(3,0)) AS DEPT,     -- 3 placeholder
                CAST(0 AS DECIMAL(9,0)) AS SSIDN,    -- 4 placeholder
                CAST('' AS CHAR(1)) AS SSIDC,        -- 5 placeholder
                  SNAME,    -- 6
                CAST('' AS CHAR(40)) AS BAD,         -- 7 placeholder
                CAST('' AS CHAR(40)) AS BADX,        -- 8 placeholder
                CAST('' AS CHAR(25)) AS BCTY,        -- 9 placeholder
                STATE,    -- 10 borrower state
                CAST(0 AS DECIMAL(9,0)) AS BZP,      -- 11 placeholder
                  YTDINT,   -- 12
                  YTDFWH,   -- 13
                  YTDOID,   -- 14
                  LYRFWH,   -- 15
                  LYRINT,   -- 16
                  LYROID,   -- 17
                  OIDCOD,   -- 18
                  BRANCH,   -- 19
                  ORGD7     -- 20  origination date, used for MV4 FH account dedup
              FROM {0}/DDMASTR
              WHERE SRPCOD <> 'Y'";

            Console.WriteLine($"[DEBUG] QueryDepositMasterAsync: DATCLIQ/DDMASTR, taxYear={taxYear}");
            try
            {
                using var conn = new OdbcConnection(_cs);
                conn.ConnectionTimeout = 60;
                conn.Open();

                using var cmd = new OdbcCommand(string.Format(sql, _coreDataLib), conn) { CommandTimeout = 120 };
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
                        CisNo     = SafeDecimal(1),
                        MbrNo     = SafeDecimal(2),
                        Dept      = SafeDecimal(3),
                        SsiDn     = SafeDecimal(4),
                        SsiDc     = SafeString(5),
                        BorrName  = SafeString(6),
                        BorrAddr  = SafeString(7),
                        BorrAddrX = SafeString(8),
                        BorrCity  = SafeString(9),
                        BorrState = SafeString(10),
                        BorrZip   = SafeDecimal(11),
                        YtdInt    = SafeDecimal(12),
                        YtdFwh    = SafeDecimal(13),
                        YtdOid    = SafeDecimal(14),
                        LyrFwh    = SafeDecimal(15),
                        LyrInt    = SafeDecimal(16),
                        LyrOid    = SafeDecimal(17),
                        OidCod    = SafeString(18),
                        Branch    = SafeDecimal(19),
                        OrgDate7  = (int)SafeDecimal(20),
                    });
                }

                // MV4: For customers with multiple FundsHeld accounts sharing the same non-zero SSIDN,
                // keep only the most recently originated account (highest ORGD7).
                // This ensures one 1099-INT row per TIN, matching the legacy SSIDN_Ary dedup logic.
                var deduped = rows
                    .GroupBy(r => r.SsiDn)
                    .SelectMany(g =>
                    {
                        if (g.Key == 0 || g.Count() <= 1) return g.AsEnumerable();
                        // Keep the account with the highest OrgDate7 (most recent origination).
                        var best = g.OrderByDescending(r => r.OrgDate7).First();
                        // For the keeper, accumulate interest/withholding from duplicate accounts.
                        foreach (var dup in g.Where(r => r.AccountNo != best.AccountNo))
                        {
                            best.YtdInt  += dup.YtdInt;
                            best.YtdFwh  += dup.YtdFwh;
                            best.YtdOid  += dup.YtdOid;
                            best.LyrInt  += dup.LyrInt;
                            best.LyrFwh  += dup.LyrFwh;
                            best.LyrOid  += dup.LyrOid;
                        }
                        return new[] { best }.AsEnumerable();
                    })
                    .ToList();

                Console.WriteLine($"[DEBUG] QueryDepositMasterAsync: Found {rows.Count} DDMASTR records, {deduped.Count} after MV4 SSIDN dedup");
                return deduped;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] QueryDepositMasterAsync: {ex.GetType().Name}: {ex.Message}");
                _logger.LogWarning(ex,
                    "Failed to query DDMASTR for tax year {TaxYear}, branch {BranchLib}",
                    taxYear, branchLib);
            }

            return rows;
        });
    }

    /// <summary>
    /// Query CSMASTPR (Customer Master) for authoritative name, address, and TIN.
    /// Used by $GET_CUST + $SET_CUST equivalent enrichment in all form transforms.
    /// Key: (CSPLIB = parentLib, CSCIS = cisNo)
    /// </summary>
    public async Task<CustomerMasterRecord?> QueryCustomerMasterAsync(string cisNo, string parentLib)
    {
        if (string.IsNullOrWhiteSpace(cisNo) || cisNo == "0") return null;
        if (string.IsNullOrWhiteSpace(parentLib)) return null;

        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();

            // CSMASTPR keyed by (CSPLIB, CSCIS). CSPLIB matches the parent/owner library (FMPALB).
            var sql = string.Format(@"
                SELECT CSTIN, CSTNCD, CSLEGN, CSNA2, CSNA3, CSNA4, CSCITL, CSSTAT, CSZIP, CSCOMB
                FROM {0}/CSMASTPR
                WHERE CSPLIB = ? AND CSCIS = ?
                FETCH FIRST ROW ONLY", parentLib);

            await using var cmd = new OdbcCommand(sql, conn);
            cmd.Parameters.AddWithValue("?", parentLib.PadRight(10));
            cmd.Parameters.AddWithValue("?", cisNo.PadRight(8));

            await using var rdr = await cmd.ExecuteReaderAsync();
            if (await rdr.ReadAsync())
            {
                string S(int i) => (rdr.IsDBNull(i) ? "" : rdr.GetValue(i)?.ToString() ?? "").Trim();
                int    N(int i) => rdr.IsDBNull(i) ? 0 : Convert.ToInt32(rdr.GetValue(i));
                decimal D(int i) => rdr.IsDBNull(i) ? 0 : Convert.ToDecimal(rdr.GetValue(i));

                return new CustomerMasterRecord
                {
                    CsTin  = D(0),
                    CsTncd = S(1),
                    CsLegn = S(2),
                    CsNa2  = S(3),
                    CsNa3  = S(4),
                    CsNa4  = S(5),
                    CsCitl = N(6),
                    CsStat = S(7),
                    CsZip  = D(8),
                    CsMbs  = S(9),
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to query CSMASTPR for cisNo={CisNo}, parentLib={ParentLib}",
                cisNo, parentLib);
        }

        return null;
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

            var expectedType = formName switch
            {
                "1099-DIV" => "D",
                "1099-PATR" => "P",
                _ => ""
            };

            if (string.IsNullOrEmpty(expectedType))
            {
                _logger.LogWarning("Unsupported form name {FormName} for capital reduction query", formName);
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
                  AND c.FORMNAME = ?
                  AND c.CRTYPE = ?
                  AND c.CRSTAG = 99
                  AND p.CPEICD = 'I'
                  AND p.CPAMDS > 0
                ORDER BY c.CRCTL, p.CRCTL";

            await using var cmd = new OdbcCommand(sql, conn);
            cmd.Parameters.AddWithValue("?", taxYear * 1000);         // Start of year (CYYDDD)
            cmd.Parameters.AddWithValue("?", (taxYear * 1000) + 366); // End of year
            cmd.Parameters.AddWithValue("?", formName.PadRight(9));
            cmd.Parameters.AddWithValue("?", expectedType);

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
                SELECT LMEBL
                FROM {0}/LNMENDR
                WHERE ACCTNO = ? AND LMDT7 = ?
                FETCH FIRST 1 ROW ONLY";
            
            await using var cmd = new OdbcCommand(string.Format(sql, _coreDataLib), conn);
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
                SELECT CAST('' AS CHAR(40)) AS FCADD,
                       CAST('' AS CHAR(30)) AS FCCTY,
                       CAST('' AS CHAR(2))  AS FCST,
                       CAST(0 AS DECIMAL(9,0)) AS FCZP,
                       CAST('' AS CHAR(40)) AS FCDSC
                FROM {0}/FCLMSTR
                WHERE ACCTNO = ?
                FETCH FIRST 1 ROW ONLY";
            
            await using var cmd = new OdbcCommand(string.Format(sql, _coreDataLib), conn);
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
                  SELECT COALESCE(SUM(ARYTDI), 0) as YTD_INT,
                      COALESCE(SUM(ARPYRI), 0) as LYR_INT
                FROM {0}/LNARECR
                  WHERE ACCTNO = ?
                  GROUP BY ACCTNO";
            
            await using var cmd = new OdbcCommand(string.Format(sql, _coreDataLib), conn);
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
                                SELECT COUNT(DISTINCT r.CFSEQM)
                FROM {0}/LNCREFR r
                                JOIN {0}/LNCOLLR c ON r.CFCIS = c.CTCIS
                                                                        AND r.CFSEQM = c.CTSEQM
                                WHERE r.CFACCT = ?
                                    AND c.CTSTAT = 'A'";
            
            await using var cmd = new OdbcCommand(string.Format(sql, _coreDataLib), conn);
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
    public int OrgDate7 { get; set; }  // ORGD7 - origination date (CYYDDD), used for MV4 FH account dedup
}

/// <summary>
/// Maps to IBM i CSMASTPR (Customer Master) record used in $GET_CUST / $SET_CUST.
/// Provides authoritative SSN/EIN, legal name, and mailing address for all tax forms.
/// </summary>
public class CustomerMasterRecord
{
    public decimal CsTin  { get; set; }              // CSTIN  - TIN (SSN or EIN)
    public string  CsTncd { get; set; } = string.Empty; // CSTNCD - TIN code: 'I'=Individual, 'T'=Trust/EIN
    public string  CsLegn { get; set; } = string.Empty; // CSLEGN - Legal name
    public string  CsNa2  { get; set; } = string.Empty; // CSNA2  - Address line 2 (BADX)
    public string  CsNa3  { get; set; } = string.Empty; // CSNA3  - Address line 1 (BAD)
    public string  CsNa4  { get; set; } = string.Empty; // CSNA4  - City/state/zip combined
    public int     CsCitl { get; set; }              // CSCITL - City length within CSNA4
    public string  CsStat { get; set; } = string.Empty; // CSSTAT - State code
    public decimal CsZip  { get; set; }              // CSZIP  - Zip code
    public string  CsMbs  { get; set; } = string.Empty; // CSMBS  - Member sub-account
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
