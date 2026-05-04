using Microsoft.AspNetCore.Mvc;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Tx9501.Models;
using Tx9501.Models.ViewModels;
using Tx9501.Services;

namespace Tx9501.Controllers;

/// <summary>
/// Replaces tx9520.sqlrpgle + tx9520fm.dspf — Tax record summary subfile.
/// </summary>
public class SummaryController : Controller
{
    private readonly ISummaryService _sumSvc;

    public SummaryController(ISummaryService sumSvc)
    {
        _sumSvc = sumSvc;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var ctrl = LoadControl();
        if (ctrl is null) return RedirectToAction("YearSelect", "TaxReporting");

        var form = HttpContext.Session.GetString("SelectedForm") ?? "";
        var rows = await _sumSvc.GetSummaryAsync(ctrl.TaxYear, form);
        var headers = GetAmountHeaders(form);

        return View(new SummaryViewModel
        {
            TaxYear  = ctrl.TaxYear,
            FormName = form,
            Amt1Header = headers.Amt1,
            Amt2Header = headers.Amt2,
            Rows     = rows
        });
    }

    [HttpGet]
    public async Task<IActionResult> DownloadPdf()
    {
        var ctrl = LoadControl();
        if (ctrl is null) return RedirectToAction("YearSelect", "TaxReporting");

        var form = HttpContext.Session.GetString("SelectedForm") ?? "";
        var rows = await _sumSvc.GetSummaryAsync(ctrl.TaxYear, form);
        var headers = GetAmountHeaders(form);

        var model = new SummaryViewModel
        {
            TaxYear  = ctrl.TaxYear,
            FormName = form,
            Amt1Header = headers.Amt1,
            Amt2Header = headers.Amt2,
            Rows     = rows
        };

        QuestPDF.Settings.License = LicenseType.Community;

        var pdfBytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(9).FontFamily("Courier New"));

                page.Header().Column(header =>
                {
                    header.Item().Text($"TX9520 - Tax Reporting Summary")
                        .FontSize(14).Bold().FontFamily("Arial");
                    header.Item().Text($"Tax Year: {model.TaxYear}     Form: {model.FormName}     Generated: {DateTime.Now:yyyy-MM-dd HH:mm}")
                        .FontSize(9).FontColor(Colors.Grey.Darken2);
                    header.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Grey.Darken1);
                });

                page.Content().PaddingTop(10).Table(table =>
                {
                    // Column widths
                    table.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(1.2f); // Assoc
                        cols.RelativeColumn(1.1f); // Count Y
                        cols.RelativeColumn(1.1f); // Count N
                        cols.RelativeColumn(1.8f); // Amt1 Y
                        cols.RelativeColumn(1.8f); // Amt1 N
                        cols.RelativeColumn(1.8f); // Amt2 Y
                        cols.RelativeColumn(1.8f); // Amt2 N
                    });

                    // Header row
                    static IContainer HeaderCell(IContainer c) =>
                        c.Background(Colors.Grey.Darken3).Padding(4).AlignCenter();

                    table.Header(h =>
                    {
                        h.Cell().Element(HeaderCell).Text("Assoc").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text("Count (Y)").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text("Count (N)").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text($"{model.Amt1Header} (Y)").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text($"{model.Amt1Header} (N)").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text($"{model.Amt2Header} (Y)").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text($"{model.Amt2Header} (N)").Bold().FontColor(Colors.White);
                    });

                    // Data rows
                    bool even = false;
                    foreach (var row in model.Rows)
                    {
                        even = !even;
                        var bg = even ? Colors.White : Colors.Grey.Lighten4;

                        IContainer DataCell(IContainer c) =>
                            c.Background(bg).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4);
                        IContainer NumCell(IContainer c) =>
                            c.Background(bg).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).AlignRight();

                        table.Cell().Element(DataCell).Text(row.Assoc);
                        table.Cell().Element(NumCell).Text(row.CountYes.ToString("N0"));
                        table.Cell().Element(NumCell).Text(row.CountNo.ToString("N0"));
                        table.Cell().Element(NumCell).Text(row.Amt1Yes.ToString("N2"));
                        table.Cell().Element(NumCell).Text(row.Amt1No.ToString("N2"));
                        table.Cell().Element(NumCell).Text(row.Amt2Yes.ToString("N2"));
                        table.Cell().Element(NumCell).Text(row.Amt2No.ToString("N2"));
                    }

                    // Totals row
                    IContainer TotalCell(IContainer c) =>
                        c.Background(Colors.Grey.Lighten3).BorderTop(1).BorderColor(Colors.Grey.Darken2).Padding(4);
                    IContainer TotalNumCell(IContainer c) =>
                        c.Background(Colors.Grey.Lighten3).BorderTop(1).BorderColor(Colors.Grey.Darken2).Padding(4).AlignRight();

                    table.Cell().Element(TotalCell).Text("Total").Bold();
                    table.Cell().Element(TotalNumCell).Text(model.Rows.Sum(r => r.CountYes).ToString("N0")).Bold();
                    table.Cell().Element(TotalNumCell).Text(model.Rows.Sum(r => r.CountNo).ToString("N0")).Bold();
                    table.Cell().Element(TotalNumCell).Text(model.Rows.Sum(r => r.Amt1Yes).ToString("N2")).Bold();
                    table.Cell().Element(TotalNumCell).Text(model.Rows.Sum(r => r.Amt1No).ToString("N2")).Bold();
                    table.Cell().Element(TotalNumCell).Text(model.Rows.Sum(r => r.Amt2Yes).ToString("N2")).Bold();
                    table.Cell().Element(TotalNumCell).Text(model.Rows.Sum(r => r.Amt2No).ToString("N2")).Bold();
                });

                page.Footer().AlignRight().Text(t =>
                {
                    t.Span("Page ").FontSize(8).FontColor(Colors.Grey.Darken1);
                    t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Darken1);
                    t.Span(" of ").FontSize(8).FontColor(Colors.Grey.Darken1);
                    t.TotalPages().FontSize(8).FontColor(Colors.Grey.Darken1);
                });
            });
        }).GeneratePdf();

        var fileName = $"Summary_{model.TaxYear}_{model.FormName}_{DateTime.Now:yyyyMMdd}.pdf";
        return File(pdfBytes, "application/pdf", fileName);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private TaxControlRecord? LoadControl()
    {
        var json = HttpContext.Session.GetString("TaxControl");
        return json is null ? null
            : System.Text.Json.JsonSerializer.Deserialize<TaxControlRecord>(json);
    }

    private static (string Amt1, string Amt2) GetAmountHeaders(string? formName)
    {
        return (formName ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "1098" => ("1098 Interest", "Points"),
            "1099-INT" => ("1099 Interest", "Withholdings"),
            "1099-DIV" => ("Dividends", "Withholdings"),
            "1099-PATR" => ("Patronage", "Withholdings"),
            "1099-A" => ("Fair Mkt Value", "Unpaid Principal"),
            "1099-MISC" => ("Rents", "Medical"),
            "1099-NEC" => ("Compensation", "Legal"),
            _ => ("Amt 1", "Amt 2")
        };
    }
}
