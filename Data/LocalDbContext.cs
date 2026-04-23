using Microsoft.EntityFrameworkCore;
using Tx9501.Models.Entities;
using Tx9501.Models.ViewModels;

namespace Tx9501.Data;

/// <summary>
/// SQLite-backed local staging store.
/// Records created through the web UI are written here; IBM i remains the
/// authoritative source for all pre-existing records.
/// New records can be reviewed here before being synced to IBM i.
/// </summary>
public class LocalDbContext : DbContext
{
    public LocalDbContext(DbContextOptions<LocalDbContext> opts) : base(opts) { }

    public DbSet<LocalTaxYear>   TaxYears   { get; set; } = null!;
    public DbSet<LocalTaxDetail> TaxDetails { get; set; } = null!;
    public DbSet<LocalTaxAudit>  TaxAudits  { get; set; } = null!;
    public DbSet<LocalExtract>   Extracts   { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<LocalTaxYear>(e =>
        {
            e.HasIndex(x => x.TaxYear).IsUnique();
        });

        mb.Entity<LocalTaxDetail>(e =>
        {
            e.HasIndex(x => new { x.TaxYear, x.Form, x.Asa, x.MbrNo, x.MbrSub })
             .IsUnique();
        });

        mb.Entity<LocalTaxAudit>(e =>
        {
            e.HasIndex(x => new { x.TaxYear, x.Form, x.Asa, x.MbrNo, x.MbrSub });
        });

        mb.Entity<LocalExtract>(e =>
        {
            e.HasIndex(x => new { x.TaxYear, x.ExtSeq }).IsUnique();
        });
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Local entity: tax year (mirrors TXRCTL)
// ─────────────────────────────────────────────────────────────────────────────

public class LocalTaxYear
{
    public int      Id          { get; set; }                         // PK auto-increment
    public int      TaxYear     { get; set; }
    public string   Description { get; set; } = string.Empty;
    public string   Status      { get; set; } = "IN PROCESS";
    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;

    public TaxYearRow ToRow() => new()
    {
        TaxYear     = TaxYear,
        Description = Description,
        Status      = Status + " (local)"
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Local entity: tax detail record (mirrors TXRDTL)
// ─────────────────────────────────────────────────────────────────────────────

public class LocalTaxDetail
{
    public int      Id          { get; set; }   // PK auto-increment

    // ── Composite key ────────────────────────────────────────────────────
    public string   TaxYear     { get; set; } = string.Empty;
    public string   Form        { get; set; } = string.Empty;
    public string   Asa         { get; set; } = string.Empty;
    public decimal  MbrNo       { get; set; }
    public string   MbrSub      { get; set; } = string.Empty;

    // ── Identity ─────────────────────────────────────────────────────────
    public decimal  SsiDn       { get; set; }
    public string   SsiDc       { get; set; } = string.Empty;

    // ── Borrower name / address ──────────────────────────────────────────
    public string   BorrName    { get; set; } = string.Empty;
    public string   BorrAddr    { get; set; } = string.Empty;
    public string   BorrAddrX   { get; set; } = string.Empty;
    public string   BorrCity    { get; set; } = string.Empty;
    public string   BorrState   { get; set; } = string.Empty;
    public decimal  BorrZip     { get; set; }

    // ── Flags ────────────────────────────────────────────────────────────
    public string   Errors      { get; set; } = string.Empty;
    public string   ReportToIrs { get; set; } = string.Empty;
    public string   CorrIn      { get; set; } = string.Empty;
    public string   Foreign     { get; set; } = string.Empty;
    public string   ChangeDate  { get; set; } = string.Empty;
    public decimal  Dept        { get; set; }

    // ── 1098 amounts ─────────────────────────────────────────────────────
    public decimal  IntPd       { get; set; }
    public decimal  Points      { get; set; }

    // ── 1099-INT amounts ─────────────────────────────────────────────────
    public decimal  InterN      { get; set; }
    public decimal  ErnWth      { get; set; }

    // ── 1099-DIV amounts ─────────────────────────────────────────────────
    public decimal  DivRcv      { get; set; }
    public decimal  DivWth      { get; set; }

    // ── 1099-PATR amounts ────────────────────────────────────────────────
    public decimal  PatRef      { get; set; }
    public decimal  PatWth      { get; set; }

    // ── 1099-A amounts / fields ──────────────────────────────────────────
    public decimal  FmVal       { get; set; }
    public decimal  UnpPrn      { get; set; }
    public string   DteAqr      { get; set; } = string.Empty;
    public string   PrDesc      { get; set; } = string.Empty;

    // ── 1099-MISC / NEC amounts ──────────────────────────────────────────
    public decimal  Compen      { get; set; }
    public decimal  Rents       { get; set; }
    public decimal  MedPay      { get; set; }
    public decimal  LglPay      { get; set; }
    public decimal  Other       { get; set; }
    public decimal  WthHeld     { get; set; }

    // ── 1098 security / property (CR2) ───────────────────────────────────
    public string   OrigDate    { get; set; } = string.Empty;
    public string   SecSame     { get; set; } = string.Empty;
    public string   SecAddr     { get; set; } = string.Empty;
    public string   SecDesc     { get; set; } = string.Empty;
    public string   SecOther    { get; set; } = string.Empty;
    public decimal  SecNum      { get; set; }
    public string   MtgAcqDt   { get; set; } = string.Empty;

    // ── Tracking ─────────────────────────────────────────────────────────
    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt   { get; set; } = DateTime.UtcNow;

    // ── Conversion ───────────────────────────────────────────────────────

    public static LocalTaxDetail FromRecord(TaxDetailRecord r) => new()
    {
        TaxYear     = r.TaxYear,    Form        = r.Form,
        Asa         = r.Asa,        MbrNo       = r.MbrNo,     MbrSub   = r.MbrSub,
        SsiDn       = r.SsiDn,      SsiDc       = r.SsiDc,
        BorrName    = r.BorrName,   BorrAddr    = r.BorrAddr,  BorrAddrX = r.BorrAddrX,
        BorrCity    = r.BorrCity,   BorrState   = r.BorrState, BorrZip   = r.BorrZip,
        Errors      = r.Errors,     ReportToIrs = r.ReportToIrs,
        CorrIn      = r.CorrIn,     Foreign     = r.Foreign,
        ChangeDate  = r.ChangeDate, Dept        = r.Dept,
        IntPd       = r.IntPd,      Points      = r.Points,
        InterN      = r.InterN,     ErnWth      = r.ErnWth,
        DivRcv      = r.DivRcv,     DivWth      = r.DivWth,
        PatRef      = r.PatRef,     PatWth      = r.PatWth,
        FmVal       = r.FmVal,      UnpPrn      = r.UnpPrn,
        DteAqr      = r.DteAqr,     PrDesc      = r.PrDesc,
        Compen      = r.Compen,     Rents       = r.Rents,
        MedPay      = r.MedPay,     LglPay      = r.LglPay,
        Other       = r.Other,      WthHeld     = r.WthHeld,
        OrigDate    = r.OrigDate,   SecSame     = r.SecSame,
        SecAddr     = r.SecAddr,    SecDesc     = r.SecDesc,
        SecOther    = r.SecOther,   SecNum      = r.SecNum,    MtgAcqDt = r.MtgAcqDt,
        CreatedAt   = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    public TaxDetailRecord ToRecord() => new()
    {
        TaxYear     = TaxYear,    Form        = Form,
        Asa         = Asa,        MbrNo       = MbrNo,       MbrSub    = MbrSub,
        SsiDn       = SsiDn,      SsiDc       = SsiDc,
        BorrName    = BorrName,   BorrAddr    = BorrAddr,    BorrAddrX = BorrAddrX,
        BorrCity    = BorrCity,   BorrState   = BorrState,   BorrZip   = BorrZip,
        Errors      = Errors,     ReportToIrs = ReportToIrs,
        CorrIn      = CorrIn,     Foreign     = Foreign,
        ChangeDate  = ChangeDate, Dept        = Dept,
        IntPd       = IntPd,      Points      = Points,
        InterN      = InterN,     ErnWth      = ErnWth,
        DivRcv      = DivRcv,     DivWth      = DivWth,
        PatRef      = PatRef,     PatWth      = PatWth,
        FmVal       = FmVal,      UnpPrn      = UnpPrn,
        DteAqr      = DteAqr,     PrDesc      = PrDesc,
        Compen      = Compen,     Rents       = Rents,
        MedPay      = MedPay,     LglPay      = LglPay,
        Other       = Other,      WthHeld     = WthHeld,
        OrigDate    = OrigDate,   SecSame     = SecSame,
        SecAddr     = SecAddr,    SecDesc     = SecDesc,
        SecOther    = SecOther,   SecNum      = SecNum,      MtgAcqDt  = MtgAcqDt
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Local entity: tax audit record (mirrors TXRAUD)
// ─────────────────────────────────────────────────────────────────────────────

public class LocalTaxAudit
{
    public int      Id          { get; set; }
    public string   TaxYear     { get; set; } = string.Empty;
    public string   Form        { get; set; } = string.Empty;
    public string   Asa         { get; set; } = string.Empty;
    public decimal  MbrNo       { get; set; }
    public string   MbrSub      { get; set; } = string.Empty;
    public decimal  SsiDn       { get; set; }
    public string   SsiDc       { get; set; } = string.Empty;
    public string   BorrName    { get; set; } = string.Empty;
    public string   BorrAddr    { get; set; } = string.Empty;
    public string   BorrAddrX   { get; set; } = string.Empty;
    public string   BorrCity    { get; set; } = string.Empty;
    public string   BorrState   { get; set; } = string.Empty;
    public decimal  BorrZip     { get; set; }
    public string   Errors      { get; set; } = string.Empty;
    public string   ReportToIrs { get; set; } = string.Empty;
    public string   CorrIn      { get; set; } = string.Empty;
    public string   Foreign     { get; set; } = string.Empty;
    public string   ChangeDate  { get; set; } = string.Empty;
    public decimal  Dept        { get; set; }
    public decimal  IntPd       { get; set; }
    public decimal  Points      { get; set; }
    public decimal  InterN      { get; set; }
    public decimal  ErnWth      { get; set; }
    public decimal  DivRcv      { get; set; }
    public decimal  DivWth      { get; set; }
    public decimal  PatRef      { get; set; }
    public decimal  PatWth      { get; set; }
    public decimal  FmVal       { get; set; }
    public decimal  UnpPrn      { get; set; }
    public string   DteAqr      { get; set; } = string.Empty;
    public string   PrDesc      { get; set; } = string.Empty;
    public decimal  Compen      { get; set; }
    public decimal  Rents       { get; set; }
    public decimal  MedPay      { get; set; }
    public decimal  LglPay      { get; set; }
    public decimal  Other       { get; set; }
    public decimal  WthHeld     { get; set; }
    public string   OrigDate    { get; set; } = string.Empty;
    public string   SecSame     { get; set; } = string.Empty;
    public string   SecAddr     { get; set; } = string.Empty;
    public string   SecDesc     { get; set; } = string.Empty;
    public string   SecOther    { get; set; } = string.Empty;
    public decimal  SecNum      { get; set; }
    public string   MtgAcqDt    { get; set; } = string.Empty;
    public DateTime AuditCreatedAt { get; set; } = DateTime.UtcNow;

    public static LocalTaxAudit FromRecord(TaxDetailRecord r) => new()
    {
        TaxYear     = r.TaxYear,    Form        = r.Form,
        Asa         = r.Asa,        MbrNo       = r.MbrNo,     MbrSub   = r.MbrSub,
        SsiDn       = r.SsiDn,      SsiDc       = r.SsiDc,
        BorrName    = r.BorrName,   BorrAddr    = r.BorrAddr,  BorrAddrX = r.BorrAddrX,
        BorrCity    = r.BorrCity,   BorrState   = r.BorrState, BorrZip   = r.BorrZip,
        Errors      = r.Errors,     ReportToIrs = r.ReportToIrs,
        CorrIn      = r.CorrIn,     Foreign     = r.Foreign,
        ChangeDate  = r.ChangeDate, Dept        = r.Dept,
        IntPd       = r.IntPd,      Points      = r.Points,
        InterN      = r.InterN,     ErnWth      = r.ErnWth,
        DivRcv      = r.DivRcv,     DivWth      = r.DivWth,
        PatRef      = r.PatRef,     PatWth      = r.PatWth,
        FmVal       = r.FmVal,      UnpPrn      = r.UnpPrn,
        DteAqr      = r.DteAqr,     PrDesc      = r.PrDesc,
        Compen      = r.Compen,     Rents       = r.Rents,
        MedPay      = r.MedPay,     LglPay      = r.LglPay,
        Other       = r.Other,      WthHeld     = r.WthHeld,
        OrigDate    = r.OrigDate,   SecSame     = r.SecSame,
        SecAddr     = r.SecAddr,    SecDesc     = r.SecDesc,
        SecOther    = r.SecOther,   SecNum      = r.SecNum,    MtgAcqDt = r.MtgAcqDt,
        AuditCreatedAt = DateTime.UtcNow
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Local entity: IRS extract control record (mirrors TXIRST)
// ─────────────────────────────────────────────────────────────────────────────

public class LocalExtract
{
    public int      Id        { get; set; }   // PK auto-increment
    public string   TaxYear   { get; set; } = string.Empty;
    public decimal  ExtSeq    { get; set; }
    public string   ExtDesc   { get; set; } = string.Empty;
    public string   ExtDate   { get; set; } = string.Empty;
    public string   ExtSelDat { get; set; } = string.Empty;
    public string   XmtrName  { get; set; } = string.Empty;
    public string   XmtrName2 { get; set; } = string.Empty;
    public long     BRecsT    { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ExtractControlRecord ToRecord() => new()
    {
        TaxYear   = TaxYear,
        ExtSeq    = ExtSeq,
        ExtDesc   = ExtDesc + " (local)",
        ExtDate   = ExtDate,
        ExtSelDat = ExtSelDat,
        XmtrName  = XmtrName,
        XmtrName2 = XmtrName2,
        BRecsT    = BRecsT
    };
}
