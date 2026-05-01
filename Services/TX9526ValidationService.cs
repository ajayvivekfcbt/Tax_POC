using Microsoft.EntityFrameworkCore;
using Tx9501.Data;
using Tx9501.Models.Entities;

namespace Tx9501.Services;

/// <summary>
/// TX9526 Equivalent: Validate tax detail records and flag those with errors.
/// Runs validation rules on TXRDTL records before submission to tax authorities.
/// </summary>
public sealed class TX9526ValidationService
{
    private readonly LocalDbContext _db;
    private readonly ILogger<TX9526ValidationService> _logger;

    // Goofy SSNs that should trigger validation errors
    private static readonly decimal[] GoofySSNs = new[]
    {
        111111111m,
        222222222m,
        333333333m,
        444444444m,
        555555555m,
        666666666m,
        777777777m,
        888888888m,
        999999999m,
    };

    public TX9526ValidationService(
        LocalDbContext db,
        ILogger<TX9526ValidationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Validate all tax detail records for the given tax year and form.
    /// Flags records with errors based on business rules.
    /// </summary>
    public async Task<int> ValidateRecordsAsync(
        int taxYear,
        string formName,
        IEnumerable<string> associations)
    {
        var taxYearStr = taxYear.ToString();
        var assocList = associations
            .Select(a => a.Trim())
            .Where(a => !string.IsNullOrEmpty(a))
            .ToList();

        // Query records to validate
        var query = _db.TaxDetails
            .Where(d => d.TaxYear == taxYearStr && d.Form == formName);

        if (assocList.Count > 0)
        {
            query = query.Where(d => assocList.Contains(d.Asa));
        }

        var records = await query.ToListAsync();
        var recordsWithErrors = 0;

        foreach (var record in records)
        {
            // Only validate records marked for reporting to IRS
            if (record.ReportToIrs != "Y")
                continue;

            var hadErrors = !string.IsNullOrEmpty(record.Errors) && record.Errors == "Y";

            // Run validation rules
            var hasErrors = false;

            // 1. Validate Tax ID Type
            if (record.SsiDc != "S" && record.SsiDc != "E")
            {
                hasErrors = true;
            }

            // 2. Validate Tax ID Number
            if (!hasErrors && (record.SsiDn <= 0 || GoofySSNs.Contains(record.SsiDn)))
            {
                hasErrors = true;
            }

            // 3. Validate Name and Address
            if (!hasErrors)
            {
                var nameMissing = string.IsNullOrWhiteSpace(record.BorrName);
                var addrMissing = string.IsNullOrWhiteSpace(record.BorrAddr);
                var cityMissing = string.IsNullOrWhiteSpace(record.BorrCity);

                var foreignAddress = record.Foreign == "Y";
                var stateMissing = string.IsNullOrWhiteSpace(record.BorrState);
                var zipInvalid = record.BorrZip < 1000000;

                if (nameMissing || addrMissing || cityMissing ||
                    (!foreignAddress && (stateMissing || zipInvalid)))
                {
                    hasErrors = true;
                }
            }

            // 4. Form-specific validation

            // For 1098: If security address is provided, secured property count must be valid
            if (!hasErrors && record.Form == "1098")
            {
                var hasSecurityAddr = !string.IsNullOrWhiteSpace(record.SecAddr);
                var propertyCountInvalid = record.SecNum <= 0;

                if (hasSecurityAddr && propertyCountInvalid)
                {
                    hasErrors = true;
                }
            }

            // Update error flag
            if (hasErrors && !hadErrors)
            {
                record.Errors = "Y";
                recordsWithErrors++;
            }
            else if (!hasErrors && hadErrors)
            {
                record.Errors = "";
            }
        }

        // Save changes
        if (recordsWithErrors > 0)
        {
            await _db.SaveChangesAsync();
        }

        _logger.LogInformation(
            "TX9526 Validation completed for year {TaxYear}, form {FormName}. " +
            "Records flagged with errors: {Count}",
            taxYear, formName, recordsWithErrors);

        return recordsWithErrors;
    }

    /// <summary>
    /// Generate validation report summarizing errors found.
    /// </summary>
    public async Task<ValidationReport> GenerateValidationReportAsync(
        int taxYear,
        string formName)
    {
        var taxYearStr = taxYear.ToString();

        var recordsToReport = await _db.TaxDetails
            .Where(d => d.TaxYear == taxYearStr && d.Form == formName && d.ReportToIrs == "Y")
            .ToListAsync();

        var recordsWithErrors = recordsToReport
            .Where(r => r.Errors == "Y")
            .ToList();

        var errorsByType = new Dictionary<string, int>
        {
            { "Tax ID Type Invalid", 0 },
            { "Tax ID Number Invalid (Goofy SSN or Zero)", 0 },
            { "Name/Address Missing or Invalid", 0 },
            { "Form 1098 Security Property Count Invalid", 0 },
        };

        foreach (var record in recordsWithErrors)
        {
            if (record.SsiDc != "S" && record.SsiDc != "E")
            {
                errorsByType["Tax ID Type Invalid"]++;
            }

            if (record.SsiDn <= 0 || GoofySSNs.Contains(record.SsiDn))
            {
                errorsByType["Tax ID Number Invalid (Goofy SSN or Zero)"]++;
            }

            var nameMissing = string.IsNullOrWhiteSpace(record.BorrName);
            var addrMissing = string.IsNullOrWhiteSpace(record.BorrAddr);
            var cityMissing = string.IsNullOrWhiteSpace(record.BorrCity);

            if (nameMissing || addrMissing || cityMissing)
            {
                errorsByType["Name/Address Missing or Invalid"]++;
            }
            else if (record.Foreign != "Y" && 
                     (string.IsNullOrWhiteSpace(record.BorrState) || record.BorrZip < 1000000))
            {
                errorsByType["Name/Address Missing or Invalid"]++;
            }

            if (record.Form == "1098")
            {
                var hasSecurityAddr = !string.IsNullOrWhiteSpace(record.SecAddr);
                if (hasSecurityAddr && record.SecNum <= 0)
                {
                    errorsByType["Form 1098 Security Property Count Invalid"]++;
                }
            }
        }

        return new ValidationReport
        {
            TaxYear = taxYear,
            FormName = formName,
            TotalRecordsReported = recordsToReport.Count,
            TotalRecordsWithErrors = recordsWithErrors.Count,
            ErrorsPercentage = recordsToReport.Count > 0 
                ? Math.Round((decimal)recordsWithErrors.Count / recordsToReport.Count * 100, 2)
                : 0,
            ErrorsByType = errorsByType,
        };
    }

    /// <summary>Validation report summary.</summary>
    public class ValidationReport
    {
        public int TaxYear { get; set; }
        public string FormName { get; set; } = string.Empty;
        public int TotalRecordsReported { get; set; }
        public int TotalRecordsWithErrors { get; set; }
        public decimal ErrorsPercentage { get; set; }
        public Dictionary<string, int> ErrorsByType { get; set; } = new();
    }
}
