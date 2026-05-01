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
    private readonly ILogger<TaxDetailSourceService> _logger;

    public TaxDetailSourceService(IConfiguration cfg, ILogger<TaxDetailSourceService> logger)
    {
        _cs     = cfg.GetConnectionString("IBMi")!;
        _lib    = cfg["IBMiSettings:Library"] ?? "TXLIB";
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
    public async Task<List<LoanMasterRecord>> QueryLoanMasterAsync(
        int taxYear, 
        string branchLib, 
        string corpCode)
    {
        return await Task.Run(() => 
        {
            var rows = new List<LoanMasterRecord>();
            const string sql = @"SELECT ACCTNO, SNAME, STATE, YTDINT, ORGBL, SD1098, STATUS, ORGD7
                                FROM DATCLIQ/LNMASTR
                                FETCH FIRST 500 ROWS ONLY";

            Console.WriteLine("[DEBUG] QueryLoanMasterAsync: Starting query");
            try
            {
                using var conn = new OdbcConnection(_cs);
                conn.ConnectionTimeout = 30;
                conn.Open();
                Console.WriteLine("[DEBUG] Connection opened");

                using var cmd = new OdbcCommand(sql, conn) { CommandTimeout = 30 };
                using var rdr = cmd.ExecuteReader();
                Console.WriteLine("[DEBUG] Query executed, reading rows");

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
                        CisNo        = SafeDecimal(0),
                        MbrNo        = SafeDecimal(0),
                        Dept         = 0,
                        Sd1098       = SafeString(5),
                        SsiDn        = 0,
                        SsiDc        = "",
                        BorrName     = SafeString(1),
                        BorrAddr     = "",
                        BorrAddrX    = "",
                        BorrCity     = "",
                        BorrState    = SafeString(2),
                        BorrZip      = 0,
                        OrgDate7     = (int)SafeDecimal(7),
                        EntDate7     = 0,
                        FpDate7      = 0,
                        FpDate6      = 0,
                        Status       = SafeString(6),
                        LpDate7      = 0,
                        Branch       = 0,
                        YtdInt       = SafeDecimal(3),
                        YtdLcp       = 0,
                        YtdPin       = 0,
                        YtdPpp       = 0,
                        YPoint       = 0,
                        PtdInt       = 0,
                        PyrlCp       = 0,
                        PtdPin       = 0,
                        PtdPpp       = 0,
                        PPoint       = 0,
                        OrgBal       = SafeDecimal(4),
                        AccId        = SafeDecimal(0),
                        FacilityCode = 0
                    });
                }
                Console.WriteLine($"[DEBUG] QueryLoanMasterAsync: Found {rows.Count} records");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] QueryLoanMasterAsync: Exception - {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"[ERROR] QueryLoanMasterAsync: InnerException - {ex.InnerException.Message}");
                }
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
    public decimal AccId { get; set; }
    public decimal FacilityCode { get; set; }
}
