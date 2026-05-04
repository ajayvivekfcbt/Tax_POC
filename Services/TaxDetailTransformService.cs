using Tx9501.Models.Entities;

namespace Tx9501.Services;

/// <summary>
/// Transforms source records (LNMASTR, DDMASTR, etc.) into TXRDTL tax detail records
/// following the TX9515 program logic.
/// </summary>
public sealed partial class TaxDetailTransformService
{
    private readonly ILogger<TaxDetailTransformService> _logger;
    private readonly TaxDetailSourceService? _sourceService;

    public TaxDetailTransformService(
        ILogger<TaxDetailTransformService> logger,
        TaxDetailSourceService? sourceService = null)
    {
        _logger = logger;
        _sourceService = sourceService;
    }

    /// <summary>
    /// Transform a LNMASTR loan record into a TXRDTL 1098 tax detail record.
    /// Replaces the $PAID_DETAIL subroutine logic from TX9515.
    /// </summary>
    public async Task<TaxDetailRecord> TransformLoanMasterTo1098(
        int taxYear,
        string corpCode,
        LoanMasterRecord loan,
        bool isCurrentYear,
        string parentLib = "")
    {
        var record = new TaxDetailRecord
        {
            TaxYear = taxYear.ToString(),
            Form = "1098",
            Asa = corpCode.PadRight(3)[..3],
            MbrNo = loan.MbrNo,
            MbrSub = "000",  // Default, may be overridden by customer lookup
            Dept = loan.Dept,
        };

        // $GET_CUST equivalent: look up authoritative TIN/name/address from CSMASTPR.
        // Falls back to LNMASTR fields when CSMASTPR is unavailable or parentLib is unknown.
        if (_sourceService != null && !string.IsNullOrWhiteSpace(parentLib) && loan.CisNo > 0)
        {
            var cm = await _sourceService.QueryCustomerMasterAsync(loan.CisNo.ToString("0"), parentLib);
            if (cm != null)
            {
                ApplyCustomerMaster(record, cm);
            }
            else
            {
                ApplyLoanMasterCustomerFields(record, loan);
            }
        }
        else
        {
            ApplyLoanMasterCustomerFields(record, loan);
        }

        // Calculate INTPD and POINTS based on current/prior year
        if (isCurrentYear)
        {
            record.IntPd = loan.YtdInt + loan.YtdLcp + loan.YtdPin + loan.YtdPpp;
            record.Points = loan.YPoint;
        }
        else
        {
            record.IntPd = loan.PtdInt + loan.PyrlCp + loan.PtdPin + loan.PtdPpp;
            record.Points = loan.PPoint;
        }

        // Add A/R interest if source service is available
        if (_sourceService != null)
        {
            var arInterest = await _sourceService.QueryARecordInterestAsync(loan.MbrNo, taxYear);
            if (arInterest != null)
            {
                record.IntPd += isCurrentYear ? arInterest.YearToDateInterest : arInterest.PriorYearInterest;
            }

            // Query unpaid principal from month-end file
            var eoyJulian = GetEndOfYearJulianDate(taxYear);
            var monthEnd = await _sourceService.QueryMonthEndBalanceAsync(loan.MbrNo, eoyJulian);
            if (monthEnd != null)
            {
                record.UnpPrn = monthEnd.UnpaidPrincipal;
            }

            // Query collateral/property count
            var collateralCount = await _sourceService.QueryCollateralCountAsync(loan.MbrNo);
            if (collateralCount > 0)
            {
                record.SecNum = collateralCount;
            }
        }

        // Set report-to-IRS flag and reason
        DetermineLoan1098ReportStatus(record, loan);

        // Set origination date from available sources
        SetOriginationDate(record, loan);

        return record;
    }

    /// <summary>
    /// Transform a LNMASTR loan record into a TXRDTL 1099-A tax detail record.
    /// Reuses shared loan/address/security logic from the 1098 transform path.
    /// </summary>
    public async Task<TaxDetailRecord> TransformLoanMasterTo1099A(
        int taxYear,
        string corpCode,
        LoanMasterRecord loan,
        bool isCurrentYear,
        string parentLib = "")
    {
        var record = await TransformLoanMasterTo1098(taxYear, corpCode, loan, isCurrentYear, parentLib);

        record.Form = "1099-A";
        record.IntPd = 0;
        record.Points = 0;

        if (record.UnpPrn <= 0)
        {
            record.UnpPrn = loan.OrgBal;
        }

        if (record.FmVal <= 0)
        {
            record.FmVal = loan.OrgBal;
        }

        if (string.IsNullOrWhiteSpace(record.DteAqr))
        {
            record.DteAqr = record.OrigDate;
        }

        if (string.IsNullOrWhiteSpace(record.PrDesc))
        {
            record.PrDesc = string.IsNullOrWhiteSpace(record.SecDesc)
                ? "Secured property"
                : record.SecDesc;
        }

        DetermineLoan1099AReportStatus(record, loan);

        return record;
    }

    /// <summary>
    /// Determine if the 1098 record should be reported to IRS and set reason if not.
    /// Mirrors the SELECT/WHEN logic in TX9515 $PAID_DETAIL.
    /// </summary>
    private void DetermineLoan1098ReportStatus(TaxDetailRecord record, LoanMasterRecord loan)
    {
        // Check 1: SD1098 flag must be 'Y'
        if (loan.Sd1098 != "Y")
        {
            record.ReportToIrs = "N";
            record.NonRptReason = "LNMASTR flagged to not report 1098";
            return;
        }

        // Check 2: SSIDC = 'E' means corporation (use record value which may come from CSMASTPR)
        if (record.SsiDc == "E")
        {
            record.ReportToIrs = "N";
            record.NonRptReason = "Interest paid by corporation";
            return;
        }

        // Check 3: Total interest + points must be >= $600
        if (record.IntPd + record.Points < 600)
        {
            record.ReportToIrs = "N";
            record.NonRptReason = "Interest + Points less than $600.00";
            return;
        }

        // Passed all checks
        record.ReportToIrs = "Y";
        record.NonRptReason = "";
    }

    /// <summary>
    /// Determine 1099-A reportability for loan-derived records.
    /// </summary>
    private void DetermineLoan1099AReportStatus(TaxDetailRecord record, LoanMasterRecord loan)
    {
        if (loan.Sd1098 != "Y")
        {
            record.ReportToIrs = "N";
            record.NonRptReason = "LNMASTR flagged to not report tax form";
            return;
        }

        // Use the enriched output record SSIDC so CSMASTPR overrides are respected.
        if (record.SsiDc == "E")
        {
            record.ReportToIrs = "N";
            record.NonRptReason = "Borrower is a corporation";
            return;
        }

        if (record.UnpPrn <= 0 && record.FmVal <= 0)
        {
            record.ReportToIrs = "N";
            record.NonRptReason = "No unpaid principal or fair market value";
            return;
        }

        record.ReportToIrs = "Y";
        record.NonRptReason = "";
    }

    /// <summary>
    /// Set the origination date from ORGD7, ENTD7, or FPDT7.
    /// Mirrors the logic in TX9515 $PAID_DETAIL.
    /// </summary>
    private void SetOriginationDate(TaxDetailRecord record, LoanMasterRecord loan)
    {
        DateTime? origDate = null;

        // Try ORGD7 (Origination Date in CYYDDD format)
        if (loan.OrgDate7 > 0)
        {
            origDate = ConvertJulianDateToDateTime(loan.OrgDate7);
        }
        // Try ENTD7 (Entry Date in CYYDDD format)
        else if (loan.EntDate7 > 0)
        {
            origDate = ConvertJulianDateToDateTime(loan.EntDate7);
        }
        // Try FPDT7 (First Payment Date in CYYDDD format)
        else if (loan.FpDate7 > 0)
        {
            origDate = ConvertJulianDateToDateTime(loan.FpDate7);
        }
        // Default to Jan 1, 2001
        else
        {
            origDate = new DateTime(2001, 1, 1);
        }

        record.OrigDate = origDate?.ToString("yyyyMMdd") ?? "";
    }

    /// <summary>
    /// Detect "goofy" SSNs (all same digit: 111111111, 222222222, etc.) and return true.
    /// </summary>
    private bool IsGoofySSN(decimal ssn)
    {
        if (ssn <= 0) return false;
        
        var ssnStr = ssn.ToString("000000000");
        if (ssnStr.Length < 9) return false;

        var first = ssnStr[0];
        return ssnStr.All(c => c == first);
    }

    /// <summary>
    /// Applies source-record (LNMASTR) customer fields when CSMASTPR is unavailable.
    /// Mirrors the fallback path in $SET_CUST.
    /// </summary>
    private void ApplyLoanMasterCustomerFields(TaxDetailRecord record, LoanMasterRecord loan)
    {
        if (IsGoofySSN(loan.SsiDn))
        {
            record.SsiDn = 0;
            record.SsiDc = "";
        }
        else
        {
            record.SsiDn = loan.SsiDn;
            record.SsiDc = loan.SsiDc;
        }

        record.BorrName  = loan.BorrName;
        record.BorrAddr  = loan.BorrAddr;
        record.BorrAddrX = loan.BorrAddrX;
        record.BorrCity  = loan.BorrCity;
        record.BorrState = loan.BorrState;
        record.BorrZip   = loan.BorrZip;

        if (string.IsNullOrWhiteSpace(loan.BorrState) && loan.BorrZip == 0)
            record.Foreign = "Y";
    }

    /// <summary>
    /// Applies customer master (CSMASTPR) fields to a tax detail record.
    /// Implements the $GET_CUST + $SET_CUST logic for all form types.
    /// cpOtin: when non-zero and different from CSTIN, use it as the TIN (beneficial owner override).
    /// </summary>
    internal void ApplyCustomerMaster(TaxDetailRecord record, CustomerMasterRecord cm, decimal cpOtin = 0)
    {
        // $GET_CUST: TIN selection
        decimal tin = (cpOtin != 0 && cpOtin != cm.CsTin) ? cpOtin : cm.CsTin;

        if (IsGoofySSN(tin))
        {
            record.SsiDn = 0;
            record.SsiDc = "";
        }
        else
        {
            record.SsiDn = tin;
            // CSTNCD: 'I' = Individual (SSN), 'T' = Trust/EIN
            record.SsiDc = cm.CsTncd switch
            {
                "I" => "S",
                "T" => "E",
                _   => ""
            };
        }

        // $SET_CUST: name
        record.BorrName = cm.CsLegn;

        // $SET_CUST: address — CSNA3 = line 1 (BAD), CSNA2 = line 2 (BADX).
        // If CSNA3 is blank but CSNA2 is not, promote CSNA2 to line 1.
        var addr1 = cm.CsNa3;
        var addr2 = cm.CsNa2;
        if (string.IsNullOrWhiteSpace(addr1) && !string.IsNullOrWhiteSpace(addr2))
        {
            addr1 = addr2;
            addr2 = "";
        }
        record.BorrAddr  = addr1;
        record.BorrAddrX = addr2;

        // $SET_CUST: city — use CSCITL as length hint into CSNA4
        if (cm.CsCitl > 0 && cm.CsCitl <= cm.CsNa4.Length)
            record.BorrCity = cm.CsNa4[..cm.CsCitl].Trim();
        else
        {
            // Fall back: use text before first space, or full CSNA4 if no spaces
            var spaceIdx = cm.CsNa4.IndexOf(' ');
            record.BorrCity = spaceIdx > 0 ? cm.CsNa4[..spaceIdx].Trim() : cm.CsNa4.Trim();
        }

        // $SET_CUST: state and foreign flag
        record.BorrState = cm.CsStat;
        if (string.IsNullOrWhiteSpace(cm.CsStat) && !string.IsNullOrWhiteSpace(cm.CsNa4))
            record.Foreign = "Y";

        // $SET_CUST: zip and MbrSub
        record.BorrZip = cm.CsZip;
        if (!string.IsNullOrWhiteSpace(cm.CsMbs))
            record.MbrSub = cm.CsMbs;
    }

    /// <summary>
    /// Calculate the Julian date for December 31 of the given tax year.
    /// Used for month-end file lookups.
    /// </summary>
    private int GetEndOfYearJulianDate(int taxYear)
    {
        var eoyDate = new DateTime(taxYear, 12, 31);
        var julianDay = eoyDate.DayOfYear;
        var century = eoyDate.Year >= 2000 ? 2 : 1;
        var yearInCentury = eoyDate.Year % 100;
        return century * 1000000 + yearInCentury * 10000 + julianDay;
    }

    /// <summary>
    /// Convert IBM i Julian date (CYYDDD format) to DateTime.
    /// CYYDDD: Century (0/1) + YY (00-99) + DDD (001-366)
    /// </summary>
    private DateTime ConvertJulianDateToDateTime(int julianDate)
    {
        if (julianDate <= 0) return new DateTime(2001, 1, 1);

        try
        {
            var century = (julianDate / 1000000) % 10;
            var year = (julianDate / 10000) % 100;
            var dayOfYear = julianDate % 1000;

            var baseYear = century == 0 ? 1900 : 2000;
            var fullYear = baseYear + year;

            return new DateTime(fullYear, 1, 1).AddDays(dayOfYear - 1);
        }
        catch
        {
            _logger.LogWarning("Failed to convert Julian date {JulianDate}", julianDate);
            return new DateTime(2001, 1, 1);
        }
    }
}
