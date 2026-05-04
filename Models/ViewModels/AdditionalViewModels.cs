using Tx9501.Models.Entities;
using System.ComponentModel.DataAnnotations;

namespace Tx9501.Models.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
// TX9515 web-vs-IBM-i field comparison tool
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One field difference between the web-built row and the IBM i TXRDTL row.</summary>
public class FieldDiff
{
    public string FieldName  { get; set; } = string.Empty;
    public string IbmiValue  { get; set; } = string.Empty;
    public string WebValue   { get; set; } = string.Empty;
    public bool   IsMatch    { get; set; }
}

/// <summary>Field comparison for one member number (one TXRDTL row keyed by MbrNo).</summary>
public class MemberCompareRow
{
    public decimal          MbrNo  { get; set; }
    public string           MbrSub { get; set; } = string.Empty;
    public bool             OnlyInIbmi { get; set; }
    public bool             OnlyInWeb  { get; set; }
    public List<FieldDiff>  Diffs  { get; set; } = new();
    public int              DiffCount => Diffs.Count(d => !d.IsMatch);
}

/// <summary>View model for the Admin/Compare page.</summary>
public class CompareViewModel
{
    // Filters
    public string TaxYear      { get; set; } = string.Empty;
    public string FormName     { get; set; } = string.Empty;
    public string AssociationId { get; set; } = string.Empty;
    public int    MaxRows      { get; set; } = 100;

    // F12 return target
    public string ReturnController { get; set; } = "Admin";
    public string ReturnAction     { get; set; } = "Index";

    // Results
    public List<MemberCompareRow> Rows          { get; set; } = new();
    public int IbmiCount  { get; set; }
    public int WebCount   { get; set; }
    public int MatchCount { get; set; }
    public int DiffCount  { get; set; }
    public int OnlyIbmi   { get; set; }
    public int OnlyWeb    { get; set; }
    public string? ErrorMessage { get; set; }
    public bool Ran { get; set; }
}


/// <summary>A selectable form option (radio-button row).</summary>
public class FormOption
{
    public string FormCode    { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>A selectable action row on a form-action menu.</summary>
public class FormAction
{
    public string ActionCode  { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class LoginViewModel
{
    [Required(ErrorMessage = "Username is required.")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public string ReturnUrl { get; set; } = string.Empty;
}

// ─────────────────────────────────────────────────────────────────────────────
// TX9500 / TX9512 – Year select (subfile list of tax years)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Row in the TX9500/TX9512 year-select subfile.</summary>
public class TaxYearRow
{
    public int    TaxYear        { get; set; }
    public string Description    { get; set; } = string.Empty;
    public string Status         { get; set; } = string.Empty;
}

/// <summary>View model for TX9500FM SFLLC (year select list).</summary>
public class YearSelectListViewModel
{
    /// <summary>All available tax years (newest first).</summary>
    public IList<TaxYearRow> Years         { get; set; } = new List<TaxYearRow>();
    public string?           ErrorMessage  { get; set; }
    public string?           StatusMessage { get; set; }
    /// <summary>Whether the Add (F6) option is available (TX9500 only, not TX9512).</summary>
    public bool              AllowAdd      { get; set; } = true;
    /// <summary>True for TX9512 audit mode (only option 1=Process).</summary>
    public bool              IsAuditMode   { get; set; }
    /// <summary>POST-back: year selected by user.</summary>
    public int?              SelectedYear  { get; set; }
    /// <summary>POST-back: option entered by user (1/2/9).</summary>
    public string?           SelectedOption { get; set; }
}

/// <summary>View model for TX9500FM SF01 – Add/Change a tax year.</summary>
public class TaxYearAddChangeViewModel
{
    [Required]
    [Range(1900, 2999, ErrorMessage = "Enter a valid 4-digit tax year.")]
    public int    TaxYear     { get; set; }

    [Required(ErrorMessage = "Description is required.")]
    [StringLength(40)]
    public string? Description { get; set; }

    public bool   IsChange    { get; set; }
    public bool   ReturnToYearSelect { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// TX9505 – Association Select (subfile)
// ─────────────────────────────────────────────────────────────────────────────

public class AssociationSelectViewModel
{
    /// <summary>All authorised associations for the current user.</summary>
    public IList<AssociationRow> Associations    { get; set; } = new List<AssociationRow>();
    /// <summary>Corp codes the user has selected (checked).</summary>
    public List<string>          SelectedCorps   { get; set; } = new();
    /// <summary>True when the user checked "ALL".</summary>
    public bool                  SelectAll       { get; set; }
    /// <summary>Where to redirect after selection.</summary>
    public string                ReturnAction    { get; set; } = "Index";
    public string                ReturnController { get; set; } = "TaxReporting";
    /// <summary>Optional Tax Year context carried across redirects.</summary>
    public string                ReturnTaxYear   { get; set; } = string.Empty;
    /// <summary>Optional Form context carried across redirects.</summary>
    public string                ReturnFormName  { get; set; } = string.Empty;
    /// <summary>F12 cancel destination controller.</summary>
    public string                CancelController { get; set; } = "TaxReporting";
    /// <summary>F12 cancel destination action.</summary>
    public string                CancelAction     { get; set; } = "MainMenu";
    /// <summary>Optional explicit F12 cancel controller override from caller flow.</summary>
    public string                CancelReturnController { get; set; } = string.Empty;
    /// <summary>Optional explicit F12 cancel action override from caller flow.</summary>
    public string                CancelReturnAction     { get; set; } = string.Empty;
}

// ─────────────────────────────────────────────────────────────────────────────
// TX9506 – Pre-Clear Warning
// ─────────────────────────────────────────────────────────────────────────────

public class PreClearWarningViewModel
{
    public string                TaxYear          { get; set; } = string.Empty;
    public string                FormName         { get; set; } = string.Empty;
    public IList<AssociationRow> Associations     { get; set; } = new List<AssociationRow>();
    public bool                  IsAll            { get; set; }
    public string                ReturnController { get; set; } = string.Empty;
    public string                ReturnAction     { get; set; } = string.Empty;
    /// <summary>POST-back: user confirmed the clear operation.</summary>
    public bool                  Confirmed        { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// TX9503 – Association Menu
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>SF01 – Association main form menu (1099-A / MISC / NEC).</summary>
public class AssocMainMenuViewModel
{
    public string       TaxYear         { get; set; } = string.Empty;
    public FormOption[] FormOptions     { get; set; } = Array.Empty<FormOption>();
    public string?      SelectedForm    { get; set; }
    public string?      SelectedFormDesc { get; set; }
    public bool         ExitPressed     { get; set; }
    public bool         BackPressed     { get; set; }
}

/// <summary>SF02 – Association form action menu.</summary>
public class AssocFormMenuViewModel
{
    public string       TaxYear        { get; set; } = string.Empty;
    public string       SelectedForm   { get; set; } = string.Empty;
    public FormAction[] Actions        { get; set; } = Array.Empty<FormAction>();
    public string?      SelectedAction { get; set; }
    public bool         ExitPressed    { get; set; }
    public bool         BackPressed    { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// TX9511 / TX9512 – Audit Menu
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>SF01 – Audit main form menu (all forms).</summary>
public class AuditMainMenuViewModel
{
    public string       TaxYear       { get; set; } = string.Empty;
    public FormOption[] FormOptions   { get; set; } = Array.Empty<FormOption>();
    public string?      SelectedForm  { get; set; }
    public bool         ExitPressed   { get; set; }
}

/// <summary>SF02 – Audit form action menu (print/summary only).</summary>
public class AuditFormMenuViewModel
{
    public string       TaxYear        { get; set; } = string.Empty;
    public string       SelectedForm   { get; set; } = string.Empty;
    public FormAction[] Actions        { get; set; } = Array.Empty<FormAction>();
    public string?      SelectedAction { get; set; }
    public bool         ExitPressed    { get; set; }
    public bool         BackPressed    { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// TX9520 – Summary display
// ─────────────────────────────────────────────────────────────────────────────

public class SummaryRow
{
    public string  Assoc    { get; set; } = string.Empty;
    public int     CountYes { get; set; }
    public decimal Amt1Yes  { get; set; }
    public decimal Amt2Yes  { get; set; }
    public decimal Amt3Yes  { get; set; }
    public int     CountNo  { get; set; }
    public decimal Amt1No   { get; set; }
    public decimal Amt2No   { get; set; }
    public decimal Amt3No   { get; set; }
}

public class SummaryViewModel
{
    public string            TaxYear  { get; set; } = string.Empty;
    public string            FormName { get; set; } = string.Empty;
    public string            Amt1Header { get; set; } = "Amt 1";
    public string            Amt2Header { get; set; } = "Amt 2";
    public IList<SummaryRow> Rows     { get; set; } = new List<SummaryRow>();
}

// ─────────────────────────────────────────────────────────────────────────────
// TX9525 – Maintain Tax Records
// ─────────────────────────────────────────────────────────────────────────────

public class MaintainSelectViewModel
{
    public string  TaxYear  { get; set; } = string.Empty;
    public string  FormName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Association is required.")]
    [StringLength(3)]
    public string  Assoc    { get; set; } = string.Empty;

    public decimal MemberNo  { get; set; }

    public string? MemberSub { get; set; }

    public bool AddPressed { get; set; }
}

public class MaintainRecordViewModel
{
    public TaxDetailRecord Record      { get; set; } = new();
    public string          Mode        { get; set; } = "ADD";  // "ADD" or "CHANGE"
    public string          FormName    { get; set; } = string.Empty;
    public string          TaxDescription { get; set; } = string.Empty;
    public string          TaxStatus   { get; set; } = string.Empty;
    public string          ActionClicked { get; set; } = string.Empty;  // "Save", "Delete", "Exit"
    public bool            ReturnToErrorReport { get; set; }

    // Customer info window (CUSTWDW from TX9525FM)
    public string          CustomerIdCode   { get; set; } = string.Empty;  // Taxpayer ID type (S/E)
    public string          CustomerIdNum    { get; set; } = string.Empty;  // Taxpayer ID number
    public string          CustomerName1    { get; set; } = string.Empty;  // Name line 1
    public string          CustomerName2    { get; set; } = string.Empty;  // Name line 2
    public string          CustomerName3    { get; set; } = string.Empty;  // Address line 1
    public string          CustomerName4    { get; set; } = string.Empty;  // Address line 2
}

public enum TaxDetailListMode
{
    Detail,
    Error,
    Exclusion
}

public class PagedResult<T>
{
    public IList<T> Items { get; set; } = new List<T>();
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public int TotalCount { get; set; }
    public int TotalPages => TotalCount <= 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;
}

public class PagedTaxDetailListViewModel
{
    public string TaxYear { get; set; } = string.Empty;
    public string FormName { get; set; } = string.Empty;
    public string FormDescription { get; set; } = string.Empty;
    public string Assoc { get; set; } = string.Empty;
    public string ScreenTitle { get; set; } = string.Empty;
    public string ProgramName { get; set; } = string.Empty;
    public string BackAction { get; set; } = string.Empty;
    public string SelectedAssociationFilter { get; set; } = string.Empty;
    public IList<string> AvailableAssociationFilters { get; set; } = new List<string>();
    public IList<TaxDetailRecord> Rows { get; set; } = new List<TaxDetailRecord>();
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public int TotalCount { get; set; }
    public int TotalPages => TotalCount <= 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;
    public int FirstRowNumber => TotalCount == 0 ? 0 : ((PageNumber - 1) * PageSize) + 1;
    public int LastRowNumber => TotalCount == 0 ? 0 : Math.Min(PageNumber * PageSize, TotalCount);
}

public class LetterCandidateListViewModel
{
    public string TaxYear { get; set; } = string.Empty;
    public string FormName { get; set; } = "1098";
    public string ScreenTitle { get; set; } = "Tax Reporting System - Non-1098 Interest Letters";
    public string ProgramName { get; set; } = "TX9591R";
    public string SelectedAssociationFilter { get; set; } = string.Empty;
    public IList<string> AvailableAssociationFilters { get; set; } = new List<string>();
    public IList<TaxDetailRecord> Rows { get; set; } = new List<TaxDetailRecord>();
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public int TotalCount { get; set; }
    public decimal TotalInterestAmount { get; set; }
    public int TotalCustomerCount { get; set; }
    public int TotalPages => TotalCount <= 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;
    public int FirstRowNumber => TotalCount == 0 ? 0 : ((PageNumber - 1) * PageSize) + 1;
    public int LastRowNumber => TotalCount == 0 ? 0 : Math.Min(PageNumber * PageSize, TotalCount);
}

// ─────────────────────────────────────────────────────────────────────────────
// TX9560 / TX9562 – Work with Extracts
// ─────────────────────────────────────────────────────────────────────────────

public class ExtractListViewModel
{
    public string                      TaxYear        { get; set; } = string.Empty;
    public IList<ExtractControlRecord> Extracts       { get; set; } = new List<ExtractControlRecord>();
    public bool                        ExitPressed    { get; set; }
    public bool                        AddPressed     { get; set; }
    public bool                        ExecutePressed { get; set; }
    public string?                     SelectedOption { get; set; }
    public long?                       SelectedSeq    { get; set; }
}

public class ExtractDefineViewModel
{
    public string   TaxYear      { get; set; } = string.Empty;
    public long?    ExtSeq       { get; set; }

    [Required(ErrorMessage = "Description is required.")]
    [StringLength(40)]
    public string   Description  { get; set; } = string.Empty;
    public string   SelectDate   { get; set; } = string.Empty;

    [StringLength(40)]
    public string   XmtrName     { get; set; } = string.Empty;

    [StringLength(40)]
    public string   XmtrName2    { get; set; } = string.Empty;
    
    // Form and Association Filters
    public string[]      FormOptions    { get; set; } = Array.Empty<string>();
    public List<string>  SelectedForms  { get; set; } = new();
    public List<string>  AssocOptions   { get; set; } = new();
    public List<string>  SelectedAssocs { get; set; } = new();
    
    public bool     IsReadOnly   { get; set; }
    public bool     CancelPressed { get; set; }
}

public class ExtractCreateViewModel
{
    public string TaxYear { get; set; } = string.Empty;

    public long NextExtSeq { get; set; }

    [Required(ErrorMessage = "Description is required.")]
    [StringLength(40)]
    public string Description { get; set; } = string.Empty;

    [DataType(DataType.Date)]
    public DateTime SelectDate { get; set; } = DateTime.Today;

    public string ExtractedAtDisplay { get; set; } = string.Empty;

    [StringLength(40)]
    public string TransmitterName { get; set; } = string.Empty;

    [StringLength(40)]
    public string CompanyName { get; set; } = string.Empty;

    [StringLength(40)]
    public string CompanyName2 { get; set; } = string.Empty;

    [StringLength(40)]
    public string MailAddress { get; set; } = string.Empty;

    [StringLength(40)]
    public string MailCity { get; set; } = string.Empty;

    [StringLength(2)]
    public string MailState { get; set; } = string.Empty;

    [StringLength(9)]
    public string MailZip { get; set; } = string.Empty;

    [StringLength(40)]
    public string ContactName { get; set; } = string.Empty;

    [StringLength(20)]
    public string ContactPhone { get; set; } = string.Empty;

    [StringLength(60)]
    public string ContactEmail { get; set; } = string.Empty;

    [StringLength(20)]
    public string TaxId { get; set; } = string.Empty;

    [StringLength(10)]
    public string ControlCode { get; set; } = string.Empty;

    [StringLength(10)]
    public string MediaNumber { get; set; } = string.Empty;

    [StringLength(2)]
    public string ReplaceCode { get; set; } = string.Empty;

    [StringLength(30)]
    public string ReplaceFile { get; set; } = string.Empty;

    [StringLength(1)]
    public string TestFile { get; set; } = string.Empty;

    [StringLength(1)]
    public string PriorYear { get; set; } = string.Empty;

    public bool CancelPressed { get; set; }
}

public class ExtractSetupViewModel
{
    public string        TaxYear        { get; set; } = string.Empty;
    public long          ExtSeq         { get; set; }
    public string[]      FormOptions    { get; set; } = Array.Empty<string>();
    public List<string>  SelectedForms  { get; set; } = new();
    public List<string>  AssocOptions   { get; set; } = new();
    public List<string>  SelectedAssocs { get; set; } = new();
    public bool          AllAssociations { get; set; }
    public bool          CancelPressed  { get; set; }
    public bool          BuildPressed   { get; set; }
}

/// <summary>Single association summary row with record count.</summary>
public class DetailReportAssociationSummary
{
    public string AssociationCode { get; set; } = string.Empty;
    public int RecordCount { get; set; }
}

/// <summary>Detail report summary view model – shows record counts per association.</summary>
public class DetailReportSummaryViewModel
{
    public string TaxYear { get; set; } = string.Empty;
    public string FormName { get; set; } = string.Empty;
    public string FormDescription { get; set; } = string.Empty;
    public string ScreenTitle { get; set; } = string.Empty;
    public string ProgramName { get; set; } = string.Empty;
    public int TotalRecordCount { get; set; }
    public IList<DetailReportAssociationSummary> AssociationSummaries { get; set; } = new List<DetailReportAssociationSummary>();
}

public class ExtractSubmitViewModel
{
    public string TaxYear { get; set; } = string.Empty;
    public long    ExtSeq { get; set; }
    public string RunDescription { get; set; } = string.Empty;
    public string RunDate { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public List<string> Lines { get; set; } = new();
    public string SelectedForm { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public bool          CancelPressed  { get; set; }
    public bool          BuildPressed   { get; set; }
}

public class ExtractFileViewerViewModel
{
    public string TaxYear { get; set; } = string.Empty;
    public long    ExtSeq { get; set; }
    public string RunDescription { get; set; } = string.Empty;
    public string RunDate { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public List<string> Lines { get; set; } = new();
    public string SelectedForm { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}

public class ExtractFormSummaryRow
{
    public string FormType { get; set; } = string.Empty;
    public int ARecords { get; set; }
    public int BRecords { get; set; }
    public int CRecords { get; set; }
    public int TotalRecords { get; set; }
}

public class ExtractFileSummaryViewModel
{
    public string TaxYear { get; set; } = string.Empty;
    public long ExtSeq { get; set; }
    public string RunDescription { get; set; } = string.Empty;
    public string RunDate { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public List<ExtractFormSummaryRow> Summaries { get; set; } = new();
    public string ErrorMessage { get; set; } = string.Empty;
}

// ─────────────────────────────────────────────────────────────────────────────
// Admin - SQLite staging browser
// ─────────────────────────────────────────────────────────────────────────────

public class StagedTaxRowViewModel
{
    public string TaxYear { get; set; } = string.Empty;
    public string Form { get; set; } = string.Empty;
    public string Asa { get; set; } = string.Empty;
    public decimal MbrNo { get; set; }
    public string MbrSub { get; set; } = string.Empty;
    public string BorrName { get; set; } = string.Empty;
    public string Errors { get; set; } = string.Empty;
    public string ReportToIrs { get; set; } = string.Empty;
    public long IntPd { get; set; }
    public decimal Compen { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class StagedTaxViewModel
{
    public string TaxYearFilter { get; set; } = string.Empty;
    public string FormFilter { get; set; } = string.Empty;
    public string AssocFilter { get; set; } = string.Empty;
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public int TotalCount { get; set; }
    public int TotalPages => TotalCount <= 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public int FirstRowNumber => TotalCount == 0 ? 0 : ((PageNumber - 1) * PageSize) + 1;
    public int LastRowNumber => TotalCount == 0 ? 0 : Math.Min(PageNumber * PageSize, TotalCount);
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;

    public int TotalStagedTaxDetails { get; set; }
    public int TotalStagedTaxAudits { get; set; }
    public IList<string> AvailableForms { get; set; } = new List<string>();
    public IList<string> AvailableAssociations { get; set; } = new List<string>();
    public IList<StagedTaxRowViewModel> Rows { get; set; } = new List<StagedTaxRowViewModel>();
}
