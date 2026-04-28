namespace Tx9501.Models.Entities;

/// <summary>
/// Maps to IBM i physical file TXRAUD (Tax Reporting Audit File).
/// Same field layout as TXRDTL; key adds CHANGE_DT.
/// </summary>
public class TaxAuditRecord : TaxDetailRecord
{
    // Inherits all TXRDTL fields; additional key CHANGE_DT is ChangeDate (inherited).
}

/// <summary>
/// Maps to IBM i physical file TXRASN (Tax Reporting Association Customization).
/// Key: TAXYR + ASA
/// Stores which forms are enabled per association per year.
/// </summary>
public class TaxAssociationRecord
{
    public string  TaxYear    { get; set; } = string.Empty;   // TAXYR  4A
    public string  Asa        { get; set; } = string.Empty;   // ASA    3A
    public string  P1098      { get; set; } = string.Empty;   // P1098  1A  Y/N
    public string  P1099Int   { get; set; } = string.Empty;   // P1099INT
    public string  P1099Div   { get; set; } = string.Empty;   // P1099DIV
    public string  P1099Patr  { get; set; } = string.Empty;   // P1099PATR
    public string  P1099A     { get; set; } = string.Empty;   // P1099A
    public string  P1099Misc  { get; set; } = string.Empty;   // P1099MISC
    public string  P1099Nec   { get; set; } = string.Empty;   // P1099NEC  (AP1)
}

/// <summary>
/// Maps to IBM i physical file TXIRST (Tax Reporting Extract Control).
/// Tracks extract sequence records.
/// </summary>
public class ExtractControlRecord
{
    public string  TaxYear    { get; set; } = string.Empty;   // TAXYR  4A
    public long    ExtSeq     { get; set; }                   // EXT_SEQ  5P0
    public string  ExtDesc    { get; set; } = string.Empty;   // EXT_DESC 40A
    public string  ExtDate    { get; set; } = string.Empty;   // EXT_DATE timestamp
    public string  ExtSelDat  { get; set; } = string.Empty;   // EXT_SELDAT selection date
    public string  XmtrName   { get; set; } = string.Empty;   // XMTR_NAME 40A
    public string  XmtrName2  { get; set; } = string.Empty;   // XMTR_NAME2 40A
    public long    BRecsT     { get; set; }                   // #BRECS_T record count
}

/// <summary>
/// Maps to IBM i physical file TXWRK01 (Tax Reporting Work file).
/// Used as scratch space during build operations.
/// </summary>
public class TaxWorkRecord
{
    public string  TaxYear    { get; set; } = string.Empty;
    public string  Form       { get; set; } = string.Empty;
    public string  Asa        { get; set; } = string.Empty;
    public decimal MbrNo      { get; set; }
    public string  WorkData   { get; set; } = string.Empty;
}

/// <summary>
/// Maps to IBM i physical file TXSSAP (SmartStream A/P Tax Summary).
/// Populated by TX9540 from SmartStream; used to build 1099-MISC/NEC.
/// </summary>
public class ApTaxSummaryRecord
{
    public string  Asa         { get; set; } = string.Empty;
    public decimal Dept        { get; set; }
    public string  Vendor      { get; set; } = string.Empty;
    public string  VendorSub   { get; set; } = string.Empty;
    public decimal MedPay      { get; set; }
    public decimal Compen      { get; set; }
    public decimal Rents       { get; set; }
    public decimal LglPay      { get; set; }
    public decimal Other       { get; set; }
    public decimal MiscWth     { get; set; }
    public string  TaxId       { get; set; } = string.Empty;
    public string  BorrName    { get; set; } = string.Empty;
    public string  BorrAddr    { get; set; } = string.Empty;
    public string  BorrAddrX   { get; set; } = string.Empty;
    public string  BorrCity    { get; set; } = string.Empty;
    public string  BorrState   { get; set; } = string.Empty;
    public string  Zip         { get; set; } = string.Empty;
}

/// <summary>
/// Mirrors FCMCCRL2 (Association master list) rows.
/// Read-only reference; data lives on IBM i.
/// </summary>
public class AssociationRow
{
    public decimal BranchNum   { get; set; }   // FMBNUM  3P0
    public string  BranchLib   { get; set; } = string.Empty;   // FMDLIB 10A
    public string  CorpCode    { get; set; } = string.Empty;   // FMGLPC  3A
    public string  Description { get; set; } = string.Empty;   // FMDSC  40A
    public string  AssnType    { get; set; } = string.Empty;   // FMASTY  4A
    public string  ParentLib   { get; set; } = string.Empty;   // FMPALB 10A
}
