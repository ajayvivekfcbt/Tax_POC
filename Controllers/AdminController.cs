using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tx9501.Data;
using Tx9501.Models.ViewModels;

namespace Tx9501.Controllers;

[Authorize]
public sealed class AdminController : Controller
{
    private const int DefaultPageSize = 50;
    private const int MinPageSize = 10;
    private const int MaxPageSize = 200;

    private readonly LocalDbContext _db;

    public AdminController(LocalDbContext db)
    {
        _db = db;
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

        var rows = await allRowsQuery
            .OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.TaxYear)
            .ThenBy(x => x.Form)
            .ThenBy(x => x.Asa)
            .ThenBy(x => x.MbrNo)
            .ThenBy(x => x.MbrSub)
            .Skip(skip)
            .Take(pageSize)
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
            .ToListAsync();

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
}
