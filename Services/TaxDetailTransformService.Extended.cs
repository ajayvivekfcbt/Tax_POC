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
        bool isCurrentYear)
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

        // Check for "goofy" SSNs - zero them out if detected
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

        record.BorrName = deposit.BorrName;
        record.BorrAddr = deposit.BorrAddr;
        record.BorrAddrX = deposit.BorrAddrX;
        record.BorrCity = deposit.BorrCity;
        record.BorrState = deposit.BorrState;
        record.BorrZip = deposit.BorrZip;

        // Check for foreign address
        if (string.IsNullOrWhiteSpace(deposit.BorrState) && deposit.BorrZip == 0)
        {
            record.Foreign = "Y";
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
        string formName)
    {
        var mbrNo = cr.CrpYet == "H" && cr.CpScis != "" ? cr.CpsAct : cr.CrLact;
        var cisCis = cr.CrpYet == "H" && cr.CpScis != "" ? cr.CpScis : cr.CrLcis;

        var record = new TaxDetailRecord
        {
            TaxYear = taxYear.ToString(),
            Form = formName,  // "1099-DIV" or "1099-PATR"
            Asa = corpCode.PadRight(3)[..3],
            MbrNo = mbrNo,
            MbrSub = "000",
            Dept = cr.CpBrch,
        };

        // TODO: Query customer master for address, SSN, etc.
        // For now, set placeholders that would come from CSMASTPR lookup
        record.SsiDn = cr.CpOtin;
        
        // Check for tax ID difference between record holder and beneficial owner
        // If CpOtin (beneficial owner TIN) differs from owner TIN, flag in error field for review
        if (formName == "1099-PATR")
        {
            record.PatWth = cr.CpAmwh;
            record.PatRef = cr.CpAmds;
            
            // TaxIdDifference: If beneficial owner TIN differs from account owner TIN
            // Flag with Errors='Y' for manual review
            if (cr.CpOtin > 0)
            {
                record.Errors = "Y";
            }
        }
        else if (formName == "1099-DIV")
        {
            // Route amounts based on asset type (D=Dividend, P=Patronage reference)
            record.DivRcv = cr.CpAmds;
            record.DivWth = cr.CpAmwh;
            
            if (cr.CpOtin > 0)
            {
                record.Errors = "Y";
            }
        }

        // Set report-to-IRS flag and reason
        DetermineCapitalReductionReportStatus(record, cr, formName);

        return record;
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
