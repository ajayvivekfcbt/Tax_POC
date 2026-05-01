using System.Data.Odbc;
using Tx9501.Data;
using Tx9501.Models.Entities;

namespace Tx9501.Services;

/// <summary>
/// TX9540 Equivalent: Process 1099-MISC and 1099-NEC tax reporting data from SmartStream A/P.
/// Queries TXSSAP (SmartStream A/P Tax table) for miscellaneous income and NEC reporting.
/// </summary>
public sealed class TX9540MiscNecService
{
    private readonly string _cs;
    private readonly string _lib;
    private readonly LocalDbContext _db;
    private readonly ILogger<TX9540MiscNecService> _logger;

    public TX9540MiscNecService(
        IConfiguration cfg,
        LocalDbContext db,
        ILogger<TX9540MiscNecService> logger)
    {
        _cs = cfg.GetConnectionString("IBMi")!;
        _lib = cfg["IBMiSettings:Library"] ?? "TXLIB";
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Build 1099-MISC and 1099-NEC records from SmartStream A/P tax data.
    /// Queries TXSSAP for vendor miscellaneous income and NEC reporting.
    /// </summary>
    public async Task<List<TaxDetailRecord>> BuildMiscAndNecAsync(
        int taxYear,
        string corpCode,
        string formName)  // "1099-MISC" or "1099-NEC"
    {
        var results = new List<TaxDetailRecord>();

        try
        {
            // Query TXSSAP for 1099-MISC or 1099-NEC data aggregated by vendor
            var miscData = await QueryTxssapMiscNecAsync(taxYear, corpCode, formName);

            foreach (var item in miscData)
            {
                var record = new TaxDetailRecord
                {
                    TaxYear = taxYear.ToString(),
                    Form = formName,
                    Asa = corpCode.PadRight(3)[..3],
                    MbrNo = item.VendorNumber,
                    MbrSub = item.VendorSub,
                    Dept = item.Dept,
                    BorrName = item.Name,
                    BorrAddr = item.Address1,
                    BorrAddrX = item.Address2,
                    BorrCity = item.City,
                    BorrState = item.State,
                    BorrZip = item.Zip,
                };

                // Check for goofy SSN - zero it out if detected
                if (IsGoofySSN(item.TaxId))
                {
                    record.SsiDn = 0;
                    record.SsiDc = "";
                }
                else
                {
                    record.SsiDn = item.TaxId;
                    record.SsiDc = "E";  // Vendors are typically EIN
                }

                // Route amounts based on form type
                if (formName == "1099-MISC")
                {
                    record.MedPay = item.MedicalAmount;
                    record.Rents = item.RentsAmount;
                    record.Other = item.OtherAmount;
                    // Note: Compen and LglPay are NOT included in 1099-MISC
                }
                else if (formName == "1099-NEC")
                {
                    record.Compen = item.NonCompAmount;
                    record.LglPay = item.LegalAmount;
                    // Note: MedPay, Rents, Other are NOT included in 1099-NEC
                }

                record.WthHeld = item.WithholdingAmount;

                // Determine report-to-IRS status
                DetermineMiscNecReportStatus(record, formName);

                if (record.ReportToIrs == "Y")
                {
                    results.Add(record);
                }
            }

            _logger.LogInformation(
                "TX9540 {Form} processing completed for year {TaxYear}, corp {CorpCode}. Records: {Count}",
                formName, taxYear, corpCode, results.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error processing TX9540 {Form} data for year {TaxYear}",
                formName, taxYear);
        }

        return results;
    }

    /// <summary>
    /// Query TXSSAP (SmartStream A/P Tax table) for miscellaneous income and NEC data.
    /// Aggregates by vendor and tax category code.
    /// </summary>
    private async Task<List<MiscNecRecord>> QueryTxssapMiscNecAsync(
        int taxYear,
        string corpCode,
        string formName)
    {
        var rows = new List<MiscNecRecord>();

        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();

            // SQL adapted from TX9540: Query TXSSAP grouped by vendor with category-based sums
            const string sql = @"
                SELECT DISTINCT
                    ASSNCORP,
                    CASE WHEN ASSNCORP = '110' THEN 0 ELSE 100 END as DEPT,
                    SUBSTRING(VENDOR_ID, 4, 7) as VENDOR_NO,
                    VENDOR_LC,
                    SUM(CASE WHEN D_CAT_CODE = 'MEDIC' THEN D_GRSS_AMT ELSE 0 END) as MED_AMT,
                    SUM(CASE WHEN D_CAT_CODE = 'NCOMP' THEN D_GRSS_AMT ELSE 0 END) as COMP_AMT,
                    SUM(CASE WHEN D_CAT_CODE = 'RENTS' THEN D_GRSS_AMT ELSE 0 END) as RENT_AMT,
                    SUM(CASE WHEN D_CAT_CODE = 'LEGAL' THEN D_GRSS_AMT ELSE 0 END) as LEGAL_AMT,
                    SUM(CASE WHEN D_CAT_CODE = 'OTHER' THEN D_GRSS_AMT ELSE 0 END) as OTH_AMT,
                    SUM(D_WH_AMT) as WTH_AMT,
                    V_TAX_ID, ORG_NAME, V_ADD1, V_ADD2,
                    V_CITY, V_STATE, V_ZIP
                FROM {0}/TXSSAP
                WHERE ASSNCORP = ? AND TAX_YEAR = ?
                GROUP BY ASSNCORP, SUBSTRING(VENDOR_ID, 4, 7), VENDOR_LC,
                    V_TAX_ID, ORG_NAME, V_ADD1, V_ADD2, V_CITY, V_STATE, V_ZIP
                ORDER BY ASSNCORP, SUBSTRING(VENDOR_ID, 4, 7), V_TAX_ID";

            await using var cmd = new OdbcCommand(string.Format(sql, _lib), conn);
            cmd.Parameters.AddWithValue("?", corpCode.PadRight(3));
            cmd.Parameters.AddWithValue("?", taxYear);

            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var vendorNo = rdr.IsDBNull(2) ? "" : rdr.GetString(2).Trim();
                
                // Convert vendor number - substitute '1' for non-numeric characters
                vendorNo = ConvertVendorNumber(vendorNo);
                
                // Extract numeric portion
                var vendorNumeric = string.IsNullOrEmpty(vendorNo) 
                    ? 0 
                    : decimal.TryParse(vendorNo, out var vn) ? vn : 1;

                var taxIdStr = rdr.IsDBNull(10) ? "" : rdr.GetString(10).Trim();
                var taxIdNumeric = ExtractTaxId(taxIdStr);

                rows.Add(new MiscNecRecord
                {
                    CorpCode = rdr.IsDBNull(0) ? "" : rdr.GetString(0).Trim(),
                    Dept = rdr.IsDBNull(1) ? 0 : (int)rdr.GetDecimal(1),
                    VendorNumber = vendorNumeric,
                    VendorSub = rdr.IsDBNull(3) ? "" : rdr.GetString(3).Trim()[..3],
                    MedicalAmount = rdr.IsDBNull(4) ? 0 : rdr.GetDecimal(4),
                    NonCompAmount = rdr.IsDBNull(5) ? 0 : rdr.GetDecimal(5),
                    RentsAmount = rdr.IsDBNull(6) ? 0 : rdr.GetDecimal(6),
                    LegalAmount = rdr.IsDBNull(7) ? 0 : rdr.GetDecimal(7),
                    OtherAmount = rdr.IsDBNull(8) ? 0 : rdr.GetDecimal(8),
                    WithholdingAmount = rdr.IsDBNull(9) ? 0 : rdr.GetDecimal(9),
                    TaxId = taxIdNumeric,
                    Name = rdr.IsDBNull(11) ? "" : rdr.GetString(11).Trim(),
                    Address1 = rdr.IsDBNull(12) ? "" : rdr.GetString(12).Trim(),
                    Address2 = rdr.IsDBNull(13) ? "" : rdr.GetString(13).Trim(),
                    City = rdr.IsDBNull(14) ? "" : rdr.GetString(14).Trim(),
                    State = rdr.IsDBNull(15) ? "" : rdr.GetString(15).Trim(),
                    Zip = ExtractZipCode(rdr.IsDBNull(16) ? "" : rdr.GetString(16).Trim()),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to query TXSSAP for year {TaxYear}, corp {CorpCode}",
                taxYear, corpCode);
        }

        return rows;
    }

    /// <summary>
    /// Determine if 1099-MISC or 1099-NEC record should be reported to IRS.
    /// Rules: Any of the amounts (except withholding alone) must be >= $600.
    /// </summary>
    private void DetermineMiscNecReportStatus(TaxDetailRecord record, string formName)
    {
        decimal reportAmount = 0;

        if (formName == "1099-MISC")
        {
            reportAmount = record.MedPay + record.Rents + record.Other;
        }
        else if (formName == "1099-NEC")
        {
            reportAmount = record.Compen + record.LglPay;
        }

        if (reportAmount >= 600)
        {
            record.ReportToIrs = "Y";
            record.NonRptReason = "";
        }
        else
        {
            record.ReportToIrs = "N";
            record.NonRptReason = $"{formName} amount is less than $600.00";
        }
    }

    /// <summary>
    /// Convert vendor number - substitute '1' for any non-numeric characters.
    /// </summary>
    private string ConvertVendorNumber(string vendorNo)
    {
        if (string.IsNullOrEmpty(vendorNo))
            return "0000000";

        var trimmed = vendorNo.Trim();
        var result = new System.Text.StringBuilder();

        foreach (var c in trimmed)
        {
            if (char.IsDigit(c))
            {
                result.Append(c);
            }
            else
            {
                result.Append('1');
            }
        }

        return result.ToString().Length == 0 ? "0000000" : result.ToString();
    }

    /// <summary>
    /// Extract numeric tax ID from string (handle mixed alphanumeric).
    /// Takes first 9 digits from the string.
    /// </summary>
    private decimal ExtractTaxId(string taxIdStr)
    {
        if (string.IsNullOrEmpty(taxIdStr))
            return 0;

        var numericOnly = new System.Text.StringBuilder();
        var maxDigits = 9;

        foreach (var c in taxIdStr)
        {
            if (char.IsDigit(c) && numericOnly.Length < maxDigits)
            {
                numericOnly.Append(c);
            }
        }

        return numericOnly.Length == 0 
            ? 0 
            : decimal.TryParse(numericOnly.ToString(), out var taxId) ? taxId : 0;
    }

    /// <summary>
    /// Extract numeric zip code from string (handle mixed alphanumeric).
    /// </summary>
    private decimal ExtractZipCode(string zipStr)
    {
        if (string.IsNullOrEmpty(zipStr))
            return 0;

        var numericOnly = new System.Text.StringBuilder();

        foreach (var c in zipStr)
        {
            if (char.IsDigit(c))
            {
                numericOnly.Append(c);
            }
        }

        return numericOnly.Length == 0
            ? 0
            : decimal.TryParse(numericOnly.ToString(), out var zip) ? zip : 0;
    }

    /// <summary>
    /// Detect "goofy" SSNs (all same digit: 111111111, 222222222, etc.).
    /// </summary>
    private bool IsGoofySSN(decimal ssn)
    {
        if (ssn <= 0) return false;

        var ssnStr = ssn.ToString("000000000");
        if (ssnStr.Length < 9) return false;

        var first = ssnStr[0];
        return ssnStr.All(c => c == first);
    }

    /// <summary>Record structure for TXSSAP query results.</summary>
    private class MiscNecRecord
    {
        public string CorpCode { get; set; } = string.Empty;
        public int Dept { get; set; }
        public decimal VendorNumber { get; set; }
        public string VendorSub { get; set; } = string.Empty;
        public decimal MedicalAmount { get; set; }
        public decimal NonCompAmount { get; set; }
        public decimal RentsAmount { get; set; }
        public decimal LegalAmount { get; set; }
        public decimal OtherAmount { get; set; }
        public decimal WithholdingAmount { get; set; }
        public decimal TaxId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Address1 { get; set; } = string.Empty;
        public string Address2 { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public decimal Zip { get; set; }
    }
}
