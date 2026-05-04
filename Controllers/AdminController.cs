using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tx9501.Data;
using Tx9501.Models.Entities;
using Tx9501.Models.ViewModels;
using Tx9501.Services;

namespace Tx9501.Controllers;

[Authorize]
public sealed class AdminController : Controller
{
    private const int DefaultPageSize = 50;
    private const int MinPageSize = 10;
    private const int MaxPageSize = 200;

    private readonly LocalDbContext _db;
    private readonly IBuildTaxDataService _buildSvc;

    public AdminController(LocalDbContext db, IBuildTaxDataService buildSvc)
    {
        _db = db;
        _buildSvc = buildSvc;
    }

    [HttpGet]
    public async Task<IActionResult> StagedTax(
        string? taxYear = null,
        string? form = null,
        string? assoc = null,
        int page = 1,
        int pageSize = DefaultPageSize)
    {
        var taxYearFilter = (taxYear ?? string.Empty).Trim();
        var formFilter = (form ?? string.Empty).Trim();
        var assocFilter = (assoc ?? string.Empty).Trim();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, MinPageSize, MaxPageSize);

        var allRowsQuery = _db.TaxDetails.AsNoTracking();

        if (!string.IsNullOrEmpty(taxYearFilter))
            allRowsQuery = allRowsQuery.Where(x => x.TaxYear == taxYearFilter);

        if (!string.IsNullOrEmpty(formFilter))
            allRowsQuery = allRowsQuery.Where(x => x.Form == formFilter);

        if (!string.IsNullOrEmpty(assocFilter))
            allRowsQuery = allRowsQuery.Where(x => x.Asa == assocFilter);

        var totalCount = await allRowsQuery.CountAsync();
        var skip = (page - 1) * pageSize;
        if (skip >= totalCount && totalCount > 0)
        {
            page = (int)Math.Ceiling(totalCount / (double)pageSize);
            skip = (page - 1) * pageSize;
        }

        var rows = (await allRowsQuery
            .Select(x => new StagedTaxRowViewModel
            {
                TaxYear = x.TaxYear,
                Form = x.Form,
                Asa = x.Asa,
                MbrNo = x.MbrNo,
                MbrSub = x.MbrSub,
                BorrName = x.BorrName,
                Errors = x.Errors,
                ReportToIrs = x.ReportToIrs,
                IntPd = x.IntPd,
                Compen = x.Compen,
                UpdatedAt = x.UpdatedAt
            })
            .ToListAsync())
            .OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.TaxYear)
            .ThenBy(x => x.Form)
            .ThenBy(x => x.Asa)
            .ThenBy(x => x.MbrNo)
            .ThenBy(x => x.MbrSub)
            .Skip(skip)
            .Take(pageSize)
            .ToList();

        var formOptions = await _db.TaxDetails
            .AsNoTracking()
            .Select(x => x.Form)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();

        var assocOptionsQuery = _db.TaxDetails.AsNoTracking();
        if (!string.IsNullOrEmpty(taxYearFilter))
            assocOptionsQuery = assocOptionsQuery.Where(x => x.TaxYear == taxYearFilter);
        if (!string.IsNullOrEmpty(formFilter))
            assocOptionsQuery = assocOptionsQuery.Where(x => x.Form == formFilter);

        var assocOptions = await assocOptionsQuery
            .Select(x => x.Asa)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();

        var vm = new StagedTaxViewModel
        {
            TaxYearFilter = taxYearFilter,
            FormFilter = formFilter,
            AssocFilter = assocFilter,
            PageNumber = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalStagedTaxDetails = await _db.TaxDetails.AsNoTracking().CountAsync(),
            TotalStagedTaxAudits = await _db.TaxAudits.AsNoTracking().CountAsync(),
            AvailableForms = formOptions,
            AvailableAssociations = assocOptions,
            Rows = rows
        };

        return View(vm);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Compare: field-level diff between IBM i TXRDTL and web-built SQLite rows
    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // Index: Admin dashboard
    // ─────────────────────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult Index()
    {
        var actions = new List<(string Title, string Description, string Action, string Controller)>
        {
            ("Staged Tax Records", "View all tax records staged in SQLite from recent builds", "StagedTax", "Admin"),
            ("Compare with IBM i", "Compare web-built rows field-by-field against IBM i TXRDTL", "Compare", "Admin"),
        };
        ViewData["Title"] = "Admin Dashboard";
        return View("Index", actions);
    }

    [HttpGet]
    public IActionResult Compare(
        string? taxYear = null,
        string? formName = null,
        string? assoc = null,
        string? returnController = null,
        string? returnAction = null)
    {
        var vm = new CompareViewModel
        {
            TaxYear = taxYear ?? string.Empty,
            FormName = formName ?? string.Empty,
            AssociationId = assoc ?? string.Empty,
            ReturnController = string.IsNullOrWhiteSpace(returnController) ? "Admin" : returnController.Trim(),
            ReturnAction = string.IsNullOrWhiteSpace(returnAction) ? "Index" : returnAction.Trim(),
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [ActionName("Compare")]
    public async Task<IActionResult> ComparePost(
        string taxYear,
        string formName,
        string assoc,
        int maxRows = 100,
        string? returnController = null,
        string? returnAction = null)
    {
        var vm = new CompareViewModel
        {
            TaxYear = taxYear,
            FormName = formName,
            AssociationId = assoc,
            MaxRows = maxRows,
            ReturnController = string.IsNullOrWhiteSpace(returnController) ? "Admin" : returnController.Trim(),
            ReturnAction = string.IsNullOrWhiteSpace(returnAction) ? "Index" : returnAction.Trim(),
            Ran = true,
        };

        try
        {
            var assocList = new[] { assoc.Trim() };

            // Fetch IBM i TXRDTL rows (limited by maxRows for performance)
            var ibmiRows = await _buildSvc.GetIbmiTxrdtlRowsAsync(taxYear, formName, assocList, selectAll: false, maxRows: maxRows);

            // Fetch web-built SQLite rows
            var assocTrimmed = assoc.Trim();
            var webRows = await _db.TaxDetails.AsNoTracking()
                .Where(r => r.TaxYear == taxYear
                         && r.Form == formName.Trim()
                         && r.Asa == assocTrimmed)
                .ToListAsync();

            vm.IbmiCount = ibmiRows.Count;
            vm.WebCount  = webRows.Count;

            // Match rows by SSIDN after converting SSIDN to a whole number.
            static long WholeSsidn(decimal value) => (long)decimal.Truncate(value);

            var ibmiBySsidn = ibmiRows
                .GroupBy(r => WholeSsidn(r.SsiDn))
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(x => x.MbrNo).ThenBy(x => x.MbrSub).ToList());

            var webBySsidn = webRows
                .GroupBy(r => WholeSsidn(r.SsiDn))
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(x => x.MbrNo).ThenBy(x => x.MbrSub).ToList());

            foreach (var ssidn in ibmiBySsidn.Keys.Union(webBySsidn.Keys).OrderBy(x => x))
            {
                var ibmiList = ibmiBySsidn.TryGetValue(ssidn, out var ib) ? ib : new List<TaxDetailRecord>();
                var webList = webBySsidn.TryGetValue(ssidn, out var wb) ? wb : new List<LocalTaxDetail>();
                var pairCount = Math.Max(ibmiList.Count, webList.Count);

                for (var i = 0; i < pairCount; i++)
                {
                    var ibmi = i < ibmiList.Count ? ibmiList[i] : null;
                    var web = i < webList.Count ? webList[i] : null;

                    vm.Rows.Add(new MemberCompareRow
                    {
                        MbrNo = ibmi?.MbrNo ?? web?.MbrNo ?? 0,
                        MbrSub = ibmi?.MbrSub ?? web?.MbrSub ?? string.Empty,
                        OnlyInIbmi = ibmi != null && web == null,
                        OnlyInWeb = ibmi == null && web != null,
                        Diffs = BuildSideBySideFields(ibmi, web)
                    });
                }
            }

            vm.Rows = vm.Rows.OrderBy(r => r.MbrNo).ThenBy(r => r.MbrSub).ToList();
            vm.MatchCount = vm.Rows.Count(r => !r.OnlyInIbmi && !r.OnlyInWeb && r.DiffCount == 0);
            vm.DiffCount = vm.Rows.Count - vm.MatchCount;
            vm.OnlyIbmi = vm.Rows.Count(r => r.OnlyInIbmi);
            vm.OnlyWeb = vm.Rows.Count(r => r.OnlyInWeb);
        }
        catch (Exception)
        {
            vm.ErrorMessage = "Error comparing records. Please try again.";
        }

        return View(vm);
    }

    private static List<FieldDiff> BuildSideBySideFields(TaxDetailRecord? ibmi, LocalTaxDetail? web)
    {
        static string S(object? v) => v?.ToString()?.Trim() ?? "";

        static string WholeS(decimal? v) => v.HasValue ? decimal.Truncate(v.Value).ToString() : "";

        var fields = new (string Name, string IbmiVal, string WebVal)[]
        {
            ("SsiDn",       WholeS(ibmi?.SsiDn),    WholeS(web?.SsiDn)),
            ("SsiDc",       S(ibmi?.SsiDc),         S(web?.SsiDc)),
            ("BorrName",    S(ibmi?.BorrName),      S(web?.BorrName)),
            ("BorrAddr",    S(ibmi?.BorrAddr),      S(web?.BorrAddr)),
            ("BorrAddrX",   S(ibmi?.BorrAddrX),     S(web?.BorrAddrX)),
            ("BorrCity",    S(ibmi?.BorrCity),      S(web?.BorrCity)),
            ("BorrState",   S(ibmi?.BorrState),     S(web?.BorrState)),
            ("BorrZip",     S(ibmi?.BorrZip),       S(web?.BorrZip)),
            ("MbrSub",      S(ibmi?.MbrSub),        S(web?.MbrSub)),
            ("Dept",        S(ibmi?.Dept),          S(web?.Dept)),
            ("IntPd",       S(ibmi?.IntPd),         S(web?.IntPd)),
            ("Points",      S(ibmi?.Points),        S(web?.Points)),
            ("InterN",      S(ibmi?.InterN),        S(web?.InterN)),
            ("ErnWth",      S(ibmi?.ErnWth),        S(web?.ErnWth)),
            ("DivRcv",      S(ibmi?.DivRcv),        S(web?.DivRcv)),
            ("DivWth",      S(ibmi?.DivWth),        S(web?.DivWth)),
            ("PatRef",      S(ibmi?.PatRef),        S(web?.PatRef)),
            ("PatWth",      S(ibmi?.PatWth),        S(web?.PatWth)),
            ("UnpPrn",      S(ibmi?.UnpPrn),        S(web?.UnpPrn)),
            ("FmVal",       S(ibmi?.FmVal),         S(web?.FmVal)),
            ("DteAqr",      S(ibmi?.DteAqr),        S(web?.DteAqr)),
            ("PrDesc",      S(ibmi?.PrDesc),        S(web?.PrDesc)),
            ("OrigDate",    S(ibmi?.OrigDate),      S(web?.OrigDate)),
            ("SecAddr",     S(ibmi?.SecAddr),       S(web?.SecAddr)),
            ("SecDesc",     S(ibmi?.SecDesc),       S(web?.SecDesc)),
            ("SecNum",      S(ibmi?.SecNum),        S(web?.SecNum)),
            ("ReportToIrs", S(ibmi?.ReportToIrs),   S(web?.ReportToIrs)),
            ("NonRptReason",S(ibmi?.NonRptReason),  S(web?.NonRptReason)),
            ("Foreign",     S(ibmi?.Foreign),       S(web?.Foreign)),
        };

        return fields.Select(f => new FieldDiff
        {
            FieldName = f.Name,
            IbmiValue = f.IbmiVal,
            WebValue  = f.WebVal,
            IsMatch   = string.Equals(f.IbmiVal, f.WebVal, StringComparison.Ordinal),
        }).ToList();
    }
}
