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

        return View(new SummaryViewModel
        {
            TaxYear  = ctrl.TaxYear,
            FormName = form,
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

        var model = new SummaryViewModel
        {
            TaxYear  = ctrl.TaxYear,
            FormName = form,
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
                        h.Cell().Element(HeaderCell).Text("Amt 1 (Y)").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text("Amt 1 (N)").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text("Amt 2 (Y)").Bold().FontColor(Colors.White);
                        h.Cell().Element(HeaderCell).Text("Amt 2 (N)").Bold().FontColor(Colors.White);
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
}
