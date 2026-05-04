using System.Data.Odbc;
using Tx9501.Models.Entities;

namespace Tx9501.Services;

/// <summary>
/// Queries source files from IBM i (LNMASTR, DDMASTR, SHCRCT/SHCRPR, etc.)
/// to support TX9515-equivalent transformation logic in the web app.
/// </summary>
public sealed partial class TaxDetailSourceService
{
    private readonly string _cs;
    private readonly string _lib;
    private readonly string _coreDataLib;
    private readonly ILogger<TaxDetailSourceService> _logger;

    public TaxDetailSourceService(IConfiguration cfg, ILogger<TaxDetailSourceService> logger)
    {
        _cs     = cfg.GetConnectionString("IBMi")!;
        _lib    = cfg["IBMiSettings:Library"] ?? "TXLIB";
        _coreDataLib = cfg["IBMiSettings:CoreDataLibrary"] ?? "DATCLIQ";
        _logger = logger;
    }

    /// <summary>
    /// Query FCMCCR to get the branch library for an association (ASSOCID).
    /// This lookup is needed before querying source files like LNMASTR, DDMASTR, etc.
    /// </summary>
    public async Task<string> GetAssociationLibraryAsync(string associationId)
    {
        const string sql = @"
            SELECT FMRPT1 
            FROM {0}/FCMCCR
            WHERE FMMCID = ? AND FMSTAT = 'A'";

        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            await using var cmd = new OdbcCommand(string.Format(sql, _lib), conn);
            cmd.Parameters.AddWithValue("?", associationId.PadRight(10));

            await using var rdr = await cmd.ExecuteReaderAsync();
            if (await rdr.ReadAsync())
            {
                var library = rdr.IsDBNull(0) ? "" : rdr.GetString(0).Trim();
                _logger.LogInformation("Found library {Library} for association {AssocId}", library, associationId);
                return library;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query FCMCCR for association {AssocId}", associationId);
        }

        _logger.LogWarning("No library found for association {AssocId}", associationId);
        return string.Empty;
    }

    /// <summary>
    /// Query FCMCCR to get the FMDLIB (facility library) for a group/class code (FMGLPC).
    /// This lookup is needed before querying SHCRCT/SHCRPR tables.
    /// </summary>
    public async Task<string> GetFacilityLibraryByGroupAsync(string groupCode)
    {
        const string sql = @"
            SELECT FMDLIB 
            FROM {0}/FCMCCR
            WHERE FMGLPC = ? AND FMSTAT = 'A'";

        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();
            await using var cmd = new OdbcCommand(string.Format(sql, _lib), conn);
            cmd.Parameters.AddWithValue("?", groupCode.PadRight(10));

            await using var rdr = await cmd.ExecuteReaderAsync();
            if (await rdr.ReadAsync())
            {
                var library = rdr.IsDBNull(0) ? "" : rdr.GetString(0).Trim();
                Console.WriteLine($"[DEBUG] GetFacilityLibraryByGroupAsync: Found library '{library}' for group '{groupCode}'");
                _logger.LogInformation("Found facility library {Library} for group {GroupCode}", library, groupCode);
                return library;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] GetFacilityLibraryByGroupAsync: Exception for group '{groupCode}': {ex.Message}");
            _logger.LogWarning(ex, "Failed to query FCMCCR for group {GroupCode}", groupCode);
        }

        Console.WriteLine($"[DEBUG] GetFacilityLibraryByGroupAsync: No library found for group '{groupCode}'");
        _logger.LogWarning("No facility library found for group {GroupCode}", groupCode);
        return string.Empty;
    }

    /// <summary>
    /// Query LNMASTR (Loan Master) for 1098 tax records.
    /// Replaces the $PAID_BUILD subroutine logic from TX9515.
    /// </summary>
    /// <summary>
    /// Query LNMASTR (Loan Master) for 1098/1099-A tax records.
    /// LNMASTR is always in DATCLIQ library (not in association-specific branches).
    /// </summary>
    public async Task<List<LoanMasterRecord>> QueryLoanMasterAsync(
        int taxYear, 
        string branchLib,
        string corpCode,
        bool isCurrentYear = true)
    {
        return await Task.Run(() => 
        {
            var rows = new List<LoanMasterRecord>();

            // CR3: exclude loans paid off before the tax year.
            // Current-year build: payoff date must fall within or after the tax year (LPDT7 > taxYear*1000).
            // Prior-year build:   payoff date must fall within or after the prior year.
            var payoffThreshold = isCurrentYear ? taxYear * 1000 : (taxYear - 1) * 1000;

            // Columns ordered for positional reader access (0-based index comments below).
            // Some legacy TX9515 fields (e.g., SSIDN/BAD/PYRLCP) are not present in current LNMASTR;
            // placeholders preserve positional mapping for downstream logic.
            var sql = @"SELECT
                  ACCTNO,   -- 0
                  CISNO,    -- 1
                CAST(0 AS DECIMAL(11,0)) AS MBRNO,   -- 2  placeholder
                  DEPT,     -- 3
                CAST(0 AS DECIMAL(9,0)) AS SSIDN,    -- 4  placeholder
                CAST('' AS CHAR(1)) AS SSIDC,        -- 5  placeholder
                  SNAME,    -- 6
                CAST('' AS CHAR(40)) AS BAD,         -- 7  placeholder
                CAST('' AS CHAR(40)) AS BADX,        -- 8  placeholder
                CAST('' AS CHAR(25)) AS BCTY,        -- 9  placeholder
                STATE,    -- 10 borrower state
                CAST(0 AS DECIMAL(9,0)) AS BZP,      -- 11 placeholder
                  YTDINT,   -- 12
                CAST(0 AS DECIMAL(11,2)) AS YTDLCP,  -- 13 placeholder
                  YTDPIN,   -- 14
                  YTDPPP,   -- 15
                  YPOINT,   -- 16
                  PTDINT,   -- 17
                CAST(0 AS DECIMAL(11,2)) AS PYRLCP,  -- 18 placeholder
                  PTDPIN,   -- 19
                  PTDPPP,   -- 20
                  PPOINT,   -- 21
                  ORGBL,    -- 22
                  SD1098,   -- 23
                  STATUS,   -- 24
                  ORGD7,    -- 25
                  ENTD7,    -- 26
                  FPDT7,    -- 27
                  FPDT6,    -- 28
                  LPDT7,    -- 29
                  BRANCH    -- 30
                  FROM {0}/LNMASTR
              WHERE STATUS <> 'P'
                 OR (STATUS = 'P' AND LPDT7 > ?)";

            Console.WriteLine($"[DEBUG] QueryLoanMasterAsync: DATCLIQ/LNMASTR, taxYear={taxYear}, isCurrentYear={isCurrentYear}, payoffThreshold={payoffThreshold}");
            try
            {
                using var conn = new OdbcConnection(_cs);
                conn.ConnectionTimeout = 60;
                conn.Open();
                Console.WriteLine("[DEBUG] QueryLoanMasterAsync: Connection opened");

                using var cmd = new OdbcCommand(string.Format(sql, _coreDataLib), conn) { CommandTimeout = 120 };
                cmd.Parameters.AddWithValue("?", payoffThreshold);
                using var rdr = cmd.ExecuteReader();
                Console.WriteLine("[DEBUG] QueryLoanMasterAsync: Query executing...");

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

                    rows.Add(new LoanMasterRecord
                    {
                        AccountNo    = SafeDecimal(0),
                        CisNo        = SafeDecimal(1),
                        MbrNo        = SafeDecimal(2),
                        Dept         = SafeDecimal(3),
                        SsiDn        = SafeDecimal(4),
                        SsiDc        = SafeString(5),
                        BorrName     = SafeString(6),
                        BorrAddr     = SafeString(7),
                        BorrAddrX    = SafeString(8),
                        BorrCity     = SafeString(9),
                        BorrState    = SafeString(10),
                        BorrZip      = SafeDecimal(11),
                        YtdInt       = SafeDecimal(12),
                        YtdLcp       = SafeDecimal(13),
                        YtdPin       = SafeDecimal(14),
                        YtdPpp       = SafeDecimal(15),
                        YPoint       = SafeDecimal(16),
                        PtdInt       = SafeDecimal(17),
                        PyrlCp       = SafeDecimal(18),
                        PtdPin       = SafeDecimal(19),
                        PtdPpp       = SafeDecimal(20),
                        PPoint       = SafeDecimal(21),
                        OrgBal       = SafeDecimal(22),
                        Sd1098       = SafeString(23),
                        Status       = SafeString(24),
                        OrgDate7     = (int)SafeDecimal(25),
                        EntDate7     = (int)SafeDecimal(26),
                        FpDate7      = (int)SafeDecimal(27),
                        FpDate6      = (int)SafeDecimal(28),
                        LpDate7      = (int)SafeDecimal(29),
                        Branch       = SafeDecimal(30)
                    });
                }
                Console.WriteLine($"[DEBUG] QueryLoanMasterAsync: Found {rows.Count} records");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] QueryLoanMasterAsync: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                    Console.WriteLine($"[ERROR] QueryLoanMasterAsync: InnerException - {ex.InnerException.Message}");
                _logger.LogWarning(ex,
                    "Failed to query LNMASTR for tax year {TaxYear}, branch {BranchLib}, corp {CorpCode}",
                    taxYear, branchLib, corpCode);
            }
            return rows;
        });
    }
}

/// <summary>
/// Record structure mirroring LNMASTR fields used in tax processing.
/// </summary>
public class LoanMasterRecord
{
    public decimal AccountNo { get; set; }
    public decimal CisNo { get; set; }
    public decimal MbrNo { get; set; }
    public decimal Dept { get; set; }
    public string Sd1098 { get; set; } = string.Empty;
    public decimal SsiDn { get; set; }
    public string SsiDc { get; set; } = string.Empty;
    public string BorrName { get; set; } = string.Empty;
    public string BorrAddr { get; set; } = string.Empty;
    public string BorrAddrX { get; set; } = string.Empty;
    public string BorrCity { get; set; } = string.Empty;
    public string BorrState { get; set; } = string.Empty;
    public decimal BorrZip { get; set; }
    public int OrgDate7 { get; set; }
    public int EntDate7 { get; set; }
    public int FpDate7 { get; set; }
    public int FpDate6 { get; set; }
    public string Status { get; set; } = string.Empty;
    public int LpDate7 { get; set; }
    public decimal Branch { get; set; }
    public decimal YtdInt { get; set; }
    public decimal YtdLcp { get; set; }
    public decimal YtdPin { get; set; }
    public decimal YtdPpp { get; set; }
    public decimal YPoint { get; set; }
    public decimal PtdInt { get; set; }
    public decimal PyrlCp { get; set; }
    public decimal PtdPin { get; set; }
    public decimal PtdPpp { get; set; }
    public decimal PPoint { get; set; }
    public decimal OrgBal { get; set; }
}
