using Tx9501.Models.Entities;

namespace Tx9501.Services;

/// <summary>
/// Extended transformation methods for 1099-INT and 1099-DIV.
/// </summary>
public partial class TaxDetailTransformService
{
    /// <summary>
    /// Transform a DDMASTR deposit record into a TXRDTL 1099-INT tax detail record.
    /// Replaces the $EARN_DETAIL subroutine logic from TX9515.
    /// </summary>
    public async Task<TaxDetailRecord> TransformDepositMasterTo1099Int(
        int taxYear,
        string corpCode,
        DepositMasterRecord deposit,
        bool isCurrentYear,
        string parentLib = "")
    {
        var record = new TaxDetailRecord
        {
            TaxYear = taxYear.ToString(),
            Form = "1099-INT",
            Asa = corpCode.PadRight(3)[..3],
            MbrNo = deposit.MbrNo,
            MbrSub = "000",
            Dept = deposit.Dept,
        };

        // $GET_CUST + $SET_CUST equivalent: prefer CSMASTPR for authoritative TIN/name/address.
        if (_sourceService != null && !string.IsNullOrWhiteSpace(parentLib) && deposit.CisNo > 0)
        {
            var cm = await _sourceService.QueryCustomerMasterAsync(deposit.CisNo.ToString("0"), parentLib);
            if (cm != null)
            {
                ApplyCustomerMaster(record, cm);
            }
            else
            {
                ApplyDepositMasterCustomerFields(record, deposit);
            }
        }
        else
        {
            ApplyDepositMasterCustomerFields(record, deposit);
        }

        // Calculate INTERN and ERNWTH based on OID code and current/prior year
        // OID = Original Issue Discount
        if (isCurrentYear)
        {
            if (deposit.OidCod == "Y")
            {
                record.InterN = deposit.YtdOid;
            }
            else
            {
                record.InterN = deposit.YtdInt;
            }
            record.ErnWth = deposit.YtdFwh;
        }
        else
        {
            if (deposit.OidCod == "Y")
            {
                record.InterN = deposit.LyrOid;
            }
            else
            {
                record.InterN = deposit.LyrInt;
            }
            record.ErnWth = deposit.LyrFwh;
        }

        // Set report-to-IRS flag and reason
        DetermineDeposit1099IntReportStatus(record, deposit);

        return record;
    }

    /// <summary>
    /// Transform capital reduction records into TXRDTL 1099-DIV or 1099-PATR records.
    /// Replaces the $CR_DETAIL subroutine logic from TX9515.
    /// </summary>
    public async Task<TaxDetailRecord> TransformCapitalReductionToForm(
        int taxYear,
        string corpCode,
        CapitalReductionRecord cr,
        string formName,
        string parentLib = "")
    {
        var mbrNo  = cr.CrpYet == "H" && cr.CpScis != "" ? cr.CpsAct : cr.CrLact;
        var cisCis = cr.CrpYet == "H" && cr.CpScis != "" ? cr.CpScis : cr.CrLcis;

        var record = new TaxDetailRecord
        {
            TaxYear = taxYear.ToString(),
            Form    = formName,   // "1099-DIV" or "1099-PATR"
            Asa     = corpCode.PadRight(3)[..3],
            MbrNo   = mbrNo,
            MbrSub  = "000",
            Dept    = cr.CpBrch,
        };

        // $GET_CUST equivalent: look up authoritative TIN/name/address from CSMASTPR.
        // cpOtin is the beneficial-owner override TIN from SHCRPR; when non-zero and
        // different from CSTIN, it takes precedence ($GET_CUST logic lines 1014-1022).
        if (_sourceService != null && !string.IsNullOrWhiteSpace(parentLib) && !string.IsNullOrWhiteSpace(cisCis) && cisCis != "0")
        {
            var cm = await _sourceService.QueryCustomerMasterAsync(cisCis, parentLib);
            if (cm != null)
            {
                ApplyCustomerMaster(record, cm, cr.CpOtin);
            }
            else
            {
                // Fallback: use CpOtin as TIN if available
                record.SsiDn = cr.CpOtin > 0 ? cr.CpOtin : 0;
            }
        }
        else
        {
            record.SsiDn = cr.CpOtin > 0 ? cr.CpOtin : 0;
        }

        // Amounts + form-specific logic
        if (formName == "1099-PATR")
        {
            record.PatWth = cr.CpAmwh;
            record.PatRef = cr.CpAmds;

            // Legacy Sr_CheckSSN: if CpOtin=0 and form is PATR, keep CSTIN (already applied above).
            // TaxIdDifference flag: if beneficial-owner TIN differs from record holder TIN.
            if (cr.CpOtin > 0 && _sourceService != null && !string.IsNullOrWhiteSpace(parentLib))
            {
                // If we have a CM record, check whether CpOtin differed from CSTIN
                // (ApplyCustomerMaster already used CpOtin when it differed, so Errors flag tracks difference)
                record.Errors = "Y";
            }
        }
        else if (formName == "1099-DIV")
        {
            record.DivRcv = cr.CpAmds;
            record.DivWth = cr.CpAmwh;

            if (cr.CpOtin > 0)
                record.Errors = "Y";
        }

        // Set report-to-IRS flag and reason
        DetermineCapitalReductionReportStatus(record, cr, formName);

        return record;
    }

    /// <summary>
    /// Applies deposit master (DDMASTR) customer fields when CSMASTPR is unavailable.
    /// Mirrors the fallback path in $SET_CUST for 1099-INT records.
    /// </summary>
    private void ApplyDepositMasterCustomerFields(TaxDetailRecord record, DepositMasterRecord deposit)
    {
        if (IsGoofySSN(deposit.SsiDn))
        {
            record.SsiDn = 0;
            record.SsiDc = "";
        }
        else
        {
            record.SsiDn = deposit.SsiDn;
            record.SsiDc = deposit.SsiDc;
        }

        record.BorrName  = deposit.BorrName;
        record.BorrAddr  = deposit.BorrAddr;
        record.BorrAddrX = deposit.BorrAddrX;
        record.BorrCity  = deposit.BorrCity;
        record.BorrState = deposit.BorrState;
        record.BorrZip   = deposit.BorrZip;

        if (string.IsNullOrWhiteSpace(deposit.BorrState) && deposit.BorrZip == 0)
            record.Foreign = "Y";
    }

    /// <summary>
    /// Determine if the 1099-INT record should be reported to IRS.
    /// Minimum threshold: $10.00
    /// </summary>
    private void DetermineDeposit1099IntReportStatus(TaxDetailRecord record, DepositMasterRecord deposit)
    {
        // Check: Total interest + withholdings must be >= $10
        if (record.InterN + record.ErnWth < 10)
        {
            record.ReportToIrs = "N";
            record.NonRptReason = "Interest earned is less than $10.00";
            return;
        }

        // Passed all checks
        record.ReportToIrs = "Y";
        record.NonRptReason = "";
    }

    /// <summary>
    /// Determine if the 1099-DIV or 1099-PATR record should be reported to IRS.
    /// Minimum threshold: $10.00
    /// </summary>
    private void DetermineCapitalReductionReportStatus(
        TaxDetailRecord record,
        CapitalReductionRecord cr,
        string formName)
    {
        decimal reportAmount = formName == "1099-PATR" 
            ? record.PatRef 
            : record.DivRcv;

        // Check: Amount must be >= $10
        if (reportAmount < 10)
        {
            record.ReportToIrs = "N";
            record.NonRptReason = "Disbursed amount is less than $10.00";
            return;
        }

        // Passed all checks
        record.ReportToIrs = "Y";
        record.NonRptReason = "";
    }
}
