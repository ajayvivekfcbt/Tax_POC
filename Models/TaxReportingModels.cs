namespace Tx9501.Models;

/// <summary>
/// Mirrors the IBM i TXRCTL physical file record format.
/// Control string layout (57 chars):
///   Positions  1- 4 : TAXYR  - Tax Year
///   Positions  5-34 : TAXDES - Tax Year Description
///   Positions 35-44 : TAXSTA - Tax Status
///   Positions 45-57 : (reserved filler)
/// </summary>
public class TaxControlRecord
{
    public string TaxYear        { get; set; } = string.Empty;
    public string TaxDescription { get; set; } = string.Empty;
    public string TaxStatus      { get; set; } = string.Empty;

    /// <summary>
    /// Serialize to the 57-character control string used when calling IBM i programs,
    /// matching the &amp;TXRCTL parameter layout in tx9501cl.clp.
    /// </summary>
    public string ToControlString()
    {
        var year  = (TaxYear        ?? string.Empty).PadRight(4)[..4];
        var desc  = (TaxDescription ?? string.Empty).PadRight(30)[..30];
        var sta   = (TaxStatus      ?? string.Empty).PadRight(10)[..10];
        return year + desc + sta + new string(' ', 13);   // 4+30+10+13 = 57
    }

    /// <summary>Deserialize from the 57-character IBM i control string.</summary>
    public static TaxControlRecord FromControlString(string ctrl)
    {
        if (string.IsNullOrEmpty(ctrl) || ctrl.Length < 44)
            return new TaxControlRecord();

        return new TaxControlRecord
        {
            TaxYear        = ctrl[..4].Trim(),
            TaxDescription = ctrl.Length >= 34 ? ctrl[4..34].Trim() : string.Empty,
            TaxStatus      = ctrl.Length >= 44 ? ctrl[34..44].Trim() : string.Empty
        };
    }
}

/// <summary>A single tax form entry shown on the main menu.</summary>
public record TaxFormEntry(string FormName, string Description);

// ---------------------------------------------------------------------------
// TX9500 equivalent – tax year selection screen
// ---------------------------------------------------------------------------
/// <summary>View model for the year-selection screen (replaces TX9500 prompt).</summary>
public class YearSelectViewModel
{
    public string  TaxYear      { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public IList<string> AvailableTaxYears { get; set; } = new List<string>();
}

// ---------------------------------------------------------------------------
// TX9501FM SF01 equivalent – main tax-form menu
// ---------------------------------------------------------------------------
/// <summary>
/// View model for the Tax Reporting System Main Menu (SF01 in TX9501FM).
/// The list of forms mirrors the IBM i &F_* / &D_* variables from tx9501cl.clp.
/// 1099-DIV was removed per modification MV1 (ITWR-8245).
/// 1099-NEC was added per modification AP1 (ITWR-10925).
/// </summary>
public class MainMenuViewModel
{
    public TaxControlRecord TaxControl  { get; set; } = new();
    public string           SystemName  { get; set; } = Environment.MachineName;
    public string           UserName    { get; set; } = string.Empty;
    public string?          ErrorMessage { get; set; }
    public string?          StatusMessage { get; set; }

    /// <summary>
    /// Available tax forms in the order displayed on the IBM i main menu.
    /// </summary>
    public IReadOnlyList<TaxFormEntry> TaxForms { get; } = new List<TaxFormEntry>
    {
        new("1098",      "Mortgage Interest"),
        new("1099-INT",  "Interest Income"),
        // 1099-DIV removed (MV1)
        new("1099-PATR", "Patronages"),
        new("1099-A",    "Acq./Abandonment of Secured Prop."),
        new("1099-MISC", "Miscellaneous Income"),
        new("1099-NEC",  "Non-Employee Compensation"),   // AP1
    };
}

// ---------------------------------------------------------------------------
// TX9501FM SF02 equivalent – single-form action menu
// ---------------------------------------------------------------------------
/// <summary>
/// View model for the Single Form Menu (SF02 in TX9501FM).
/// The action descriptions mirror the IBM i &D_* variables in tx9501cl.clp.
/// </summary>
public class FormMenuViewModel
{
    public TaxControlRecord TaxControl      { get; set; } = new();
    public string           FormName        { get; set; } = string.Empty;
    public string           FormDescription { get; set; } = string.Empty;

    /// <summary>
    /// Equivalent to IBM i IN81 indicator – true only when FormName == "1098"
    /// so the letter-printing option is shown.
    /// </summary>
    public bool   ShowLetterOption { get; set; }

    public string? StatusMessage { get; set; }
    public string? ErrorMessage  { get; set; }

    /// <summary>Display info about currently selected associations.</summary>
    public string SelectedAssociationsDisplay { get; set; } = string.Empty;

    /// <summary>True when validation is running in background.</summary>
    public bool IsValidationRunning { get; set; }

    // Action descriptions – mirrors IBM i &D_* CHGVAR assignments
    public string D_Maintain { get; } = "Maintain Tax Records";
    public string D_Edit     { get; } = "Validate Tax Records (check for errors) – select associations";
    public string D_Clear    { get; } = "Clear Tax Records";
    public string D_Build    { get; } = "Build Tax Records (load from application to Tax Reporting System)";
    public string D_PrtDtl   { get; } = "Print Detail Report";
    public string D_PrtExc   { get; } = "Print Exclusion Report";
    public string D_PrtErr   { get; } = "Print Error Report";
    public string D_Summary  { get; } = "Review Summary of Tax Data";
    public string D_Letter   { get; } = "Print Letters for borrowers not receiving a 1098 form";
}
