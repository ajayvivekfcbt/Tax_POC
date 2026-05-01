using System.Data.Odbc;
using Tx9501.Models.Entities;

namespace Tx9501.Services;

/// <summary>
/// TX9517 Equivalent: Load tax data from Patronage files (PAYAAS, PAMAST).
/// Processes patronage yearly accumulator records and creates 1099-PATR tax detail records.
/// </summary>
public sealed class TX9517PatronageService
{
    private readonly string _cs;
    private readonly string _lib;
    private readonly ILogger<TX9517PatronageService> _logger;

    public TX9517PatronageService(
        IConfiguration cfg,
        ILogger<TX9517PatronageService> logger)
    {
        _cs = cfg.GetConnectionString("IBMi")!;
        _lib = cfg["IBMiSettings:Library"] ?? "TXLIB";
        _logger = logger;
    }

    /// <summary>
    /// Build 1099-PATR (Patronage) records from PAYAAS (Patronage Yearly Accumulator).
    /// Processes qualified and non-qualified patronage amounts with withholding.
    /// </summary>
    public async Task<List<TaxDetailRecord>> BuildPatronageAsync(
        int taxYear,
        string corpCode)
    {
        var results = new List<TaxDetailRecord>();

        try
        {
            // Query PAYAAS for patronage accumulator records
            var patronageRecords = await QueryPatronageYearlyAsync(taxYear, corpCode);

            foreach (var patronage in patronageRecords)
            {
                // Create tax detail record from patronage accumulator
                var record = new TaxDetailRecord
                {
                    TaxYear = taxYear.ToString(),
                    Form = "1099-PATR",
                    Asa = corpCode.PadRight(3)[..3],
                    MbrNo = patronage.AccountNumber,
                    SsiDn = patronage.Cis,
                    SsiDc = "S",  // Assume SSN for patronage (members are individuals)
                    PatRef = patronage.QualifiedAmount + patronage.NonQualifiedAmount,
                    PatWth = patronage.FederalWithholding + patronage.StateWithholding,
                };

                // Check suppression flag from PAMAST
                if (patronage.Suppress1099 == "Y")
                {
                    record.ReportToIrs = "N";
                    record.NonRptReason = "PAMAST is flagged to suppress 1099-PATR";
                    record.Errors = "Y";
                }
                else
                {
                    // Determine report-to-IRS status
                    DeterminePatronageReportStatus(record, patronage);
                }

                if (record.ReportToIrs == "Y" || record.Errors == "Y")
                {
                    results.Add(record);
                }
            }

            _logger.LogInformation(
                "TX9517 Patronage processing completed for year {TaxYear}, corp {CorpCode}. Records: {Count}",
                taxYear, corpCode, results.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error processing TX9517 Patronage data for year {TaxYear}",
                taxYear);
        }

        return results;
    }

    /// <summary>
    /// Query PAYAAS (Patronage Yearly Accumulator) joined with PAMAST (Patronage Account Master).
    /// Returns patronage amounts and withholding for the specified year.
    /// </summary>
    private async Task<List<PatronageRecord>> QueryPatronageYearlyAsync(
        int taxYear,
        string corpCode)
    {
        var rows = new List<PatronageRecord>();

        try
        {
            await using var conn = new OdbcConnection(_cs);
            await conn.OpenAsync();

            // SQL adapted from TX9517: Query PAYAAS joined with PAMAST
            const string sql = @"
                SELECT PQPNTID, PQACCT, PQATYP,
                       PQQLTXBL, PQNQLTXBL, PQWHFED,
                       PQWHST, PMCIS, PMSP1099
                FROM {0}/PAYAAS
                JOIN {0}/PAMAST ON PMACCT = PQACCT
                WHERE PQPNTID = ? AND PQYEAR = ?
                ORDER BY PQPNTID, PQACCT";

            await using var cmd = new OdbcCommand(string.Format(sql, _lib), conn);
            cmd.Parameters.AddWithValue("?", corpCode.PadRight(10));
            cmd.Parameters.AddWithValue("?", taxYear);

            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                rows.Add(new PatronageRecord
                {
                    PontId = rdr.IsDBNull(0) ? "" : rdr.GetString(0).Trim(),
                    AccountNumber = rdr.IsDBNull(1) ? 0 : rdr.GetDecimal(1),
                    AccountType = rdr.IsDBNull(2) ? "" : rdr.GetString(2).Trim(),
                    QualifiedAmount = rdr.IsDBNull(3) ? 0 : rdr.GetDecimal(3),
                    NonQualifiedAmount = rdr.IsDBNull(4) ? 0 : rdr.GetDecimal(4),
                    FederalWithholding = rdr.IsDBNull(5) ? 0 : rdr.GetDecimal(5),
                    StateWithholding = rdr.IsDBNull(6) ? 0 : rdr.GetDecimal(6),
                    Cis = rdr.IsDBNull(7) ? 0 : rdr.GetDecimal(7),
                    Suppress1099 = rdr.IsDBNull(8) ? "" : rdr.GetString(8).Trim(),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to query PAYAAS for year {TaxYear}, corp {CorpCode}",
                taxYear, corpCode);
        }

        return rows;
    }

    /// <summary>
    /// Determine if 1099-PATR record should be reported to IRS.
    /// Rules:
    /// - Patronage amount must be >= $10.00
    /// - Account customer (CIS) must be valid (> 0)
    /// - Suppression flag must not be 'Y'
    /// </summary>
    private void DeterminePatronageReportStatus(
        TaxDetailRecord record,
        PatronageRecord patronage)
    {
        // Check: Patronage amount must be >= $10
        if (record.PatRef < 10)
        {
            record.ReportToIrs = "N";
            record.NonRptReason = "Disbursed amount is less than $10.00";
            return;
        }

        // Check: Customer CIS must be valid
        if (patronage.Cis <= 0)
        {
            record.ReportToIrs = "N";
            record.NonRptReason = "Patronage Customer is invalid";
            record.Errors = "Y";
            return;
        }

        // Passed all checks
        record.ReportToIrs = "Y";
        record.NonRptReason = "";
    }

    /// <summary>Record structure for PAYAAS query results.</summary>
    private class PatronageRecord
    {
        public string PontId { get; set; } = string.Empty;
        public decimal AccountNumber { get; set; }
        public string AccountType { get; set; } = string.Empty;
        public decimal QualifiedAmount { get; set; }
        public decimal NonQualifiedAmount { get; set; }
        public decimal FederalWithholding { get; set; }
        public decimal StateWithholding { get; set; }
        public decimal Cis { get; set; }
        public string Suppress1099 { get; set; } = string.Empty;
    }
}
