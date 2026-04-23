namespace Tx9501.Models.Entities;

/// <summary>
/// Maps to IBM i physical file TXRDTL (I.R.S. Reporting Detail File).
/// Key: TAXYR + FORM + ASA + MBRNO + SSIDN + MBRSUB
/// Fields are sourced from TXFREF field reference file.
/// </summary>
public class TaxDetailRecord
{
    public string   TaxYear       { get; set; } = string.Empty;   // TAXYR  4A
    public string   Form          { get; set; } = string.Empty;   // FORM   9A
    public string   Asa           { get; set; } = string.Empty;   // ASA    3A  (association code)
    public decimal  MbrNo         { get; set; }                   // MBRNO 11P0 (member/account no)
    public string   MbrSub        { get; set; } = string.Empty;   // MBRSUB 3A  (sub-ID, CR3)
    public decimal  SsiDn         { get; set; }                   // SSIDN  9P0 (SSN/EIN number)
    public string   AsaLib        { get; set; } = string.Empty;   // ASA_LIB 10A
    public string   AssnId        { get; set; } = string.Empty;   // ASSNID 10A
    public string   AsaRpt        { get; set; } = string.Empty;   // ASARPT  1A  (report flag)
    public string   PCorp         { get; set; } = string.Empty;   // PCORP   3A
    public string   AsaRptLib     { get; set; } = string.Empty;   // ASARPT_LIB
    public string   Customer      { get; set; } = string.Empty;   // CUSTOMER
    public decimal  Dept          { get; set; }                   // DEPT    3P0 (branch)
    public string   MultiLoan     { get; set; } = string.Empty;   // MULTLN  1A
    public string   Foreign       { get; set; } = string.Empty;   // FOREGN  1A  (Y/N)
    public string   CorrIn        { get; set; } = string.Empty;   // CORRIN  1A  (correction flag, MV3)
    public string   Errors        { get; set; } = string.Empty;   // ERRORS  1A  (Y/N)
    public string   ReportToIrs   { get; set; } = string.Empty;   // RPT_TO_IRS 1A
    public string   NonRptReason  { get; set; } = string.Empty;   // NONRPT_RSN
    public string   ChangeDate    { get; set; } = string.Empty;   // CHANGE_DT

    // Borrower name & address
    public string   BorrName      { get; set; } = string.Empty;   // BNM    40A
    public string   BorrAddrX     { get; set; } = string.Empty;   // BADX   40A (addr line 2)
    public string   BorrAddr      { get; set; } = string.Empty;   // BAD    40A
    public string   BorrCity      { get; set; } = string.Empty;   // BCTY   20A
    public string   BorrState     { get; set; } = string.Empty;   // BST     2A
    public decimal  BorrZip       { get; set; }                   // BZP     9P0
    public string   SsiDc         { get; set; } = string.Empty;   // SSIDC   1A  (S=SSN, E=EIN)

    // Amount fields (all 12,2 packed)
    public decimal  IntPd         { get; set; }   // INTPD   – 1098 interest paid
    public decimal  Points        { get; set; }   // POINTS  – 1098 points
    public string   MtgAcqDt      { get; set; } = string.Empty;   // MTGACQDT 8A (MV2)
    public decimal  InterN        { get; set; }   // INTERN  – 1099-INT
    public decimal  ErnWth        { get; set; }   // ERNWTH  – 1099-INT withholding
    public decimal  DivRcv        { get; set; }   // DIVRCV  – 1099-DIV dividends
    public decimal  DivWth        { get; set; }   // DIVWTH  – 1099-DIV withholding
    public decimal  PatRef        { get; set; }   // PATREF  – 1099-PATR patronage
    public decimal  PatWth        { get; set; }   // PATWTH  – 1099-PATR withholding
    public decimal  FmVal         { get; set; }   // FMVAL   – 1099-A fair market value
    public decimal  UnpPrn        { get; set; }   // UNPPRN  – 1099-A unpaid principal
    public string   DteAqr        { get; set; } = string.Empty;   // DTEAQR  – 1099-A date acquired
    public string   PrDesc        { get; set; } = string.Empty;   // PRDESC  – 1099-A property desc
    public string   LibInd        { get; set; } = string.Empty;   // LIBIND
    public decimal  Compen        { get; set; }   // COMPEN  – 1099-MISC/NEC compensation
    public decimal  Rents         { get; set; }   // RENTS   – 1099-MISC rents
    public decimal  MedPay        { get; set; }   // MEDPAY  – 1099-MISC medical
    public decimal  LglPay        { get; set; }   // LGLPAY  – 1099-MISC/NEC legal
    public decimal  Other         { get; set; }   // OTHER   – 1099-MISC other
    public decimal  WthHeld       { get; set; }   // WTHHELD – 1099-MISC withholding (CR1)

    // 1098 security / property fields (CR2)
    public string   OrigDate      { get; set; } = string.Empty;   // ORIGDATE 8A (YYYYMMDD)
    public string   SecSame       { get; set; } = string.Empty;   // SECSAME  1A (Y/N)
    public string   SecAddr       { get; set; } = string.Empty;   // SECADDR 40A
    public string   SecDesc       { get; set; } = string.Empty;   // SECDESC 40A
    public string   SecOther      { get; set; } = string.Empty;   // SECOTHER 40A
    public decimal  SecNum        { get; set; }                   // SECNUM   5P0 (MV1)
}
