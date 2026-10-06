using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MatPaper.Services;

/// <summary>
/// Renders the data of a structured e-invoice (an XRechnung without a PDF) as a readable A4
/// invoice, so it fits into the archive like any other document: thumbnail, preview, full-text
/// search. The XML stays next to the PDF and remains the authoritative document; the footer says so.
/// </summary>
public sealed class InvoicePdfRenderer
{
    public byte[] Render(InvoiceData d, bool german)
    {
        string T(string de, string en) => german ? de : en;
        var culture = CultureInfo.GetCultureInfo(german ? "de-DE" : "en-US");
        string Money(decimal? v) => v.HasValue
            ? v.Value.ToString("N2", culture) + (string.IsNullOrWhiteSpace(d.Currency) ? "" : " " + d.Currency)
            : "";
        string Day(DateTime? v) => v.HasValue ? v.Value.ToString("dd.MM.yyyy", culture) : "";

        var credit = (d.TypeCode ?? string.Empty).Contains("Credit", StringComparison.OrdinalIgnoreCase);
        var title = credit ? T("Gutschrift", "Credit note") : T("Rechnung", "Invoice");

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(10).LineHeight(1.3f));

                page.Header().Column(col =>
                {
                    col.Item().Text(title + (string.IsNullOrWhiteSpace(d.InvoiceNumber) ? "" : " " + d.InvoiceNumber)).FontSize(20).SemiBold();
                    col.Item().PaddingTop(2).Text(T("E-Rechnung (XRechnung)", "E-invoice (XRechnung)")).FontColor(Colors.Grey.Darken1);
                });

                page.Content().PaddingTop(14).Column(col =>
                {
                    col.Spacing(14);

                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Element(e => Party(e, T("Rechnungssteller", "Seller"), d.SellerName, d.SellerAddress,
                            new[] { (T("USt-IdNr.", "VAT ID"), d.SellerVatId), ("IBAN", d.SellerIban) }));
                        row.ConstantItem(16);
                        row.RelativeItem().Element(e => Party(e, T("Rechnungsempfänger", "Buyer"), d.BuyerName, d.BuyerAddress,
                            new[] { (T("Referenz / Leitweg-ID", "Reference"), d.BuyerReference) }));
                    });

                    col.Item().Element(e => Facts(e, new[]
                    {
                        (T("Rechnungsdatum", "Invoice date"), Day(d.InvoiceDate)),
                        (T("Fällig am", "Due date"), Day(d.DueDate)),
                        (T("Zahlungsbedingungen", "Payment terms"), d.PaymentTerms ?? string.Empty)
                    }));

                    if (d.Lines.Count > 0)
                    {
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(24);
                                c.RelativeColumn(5);
                                c.RelativeColumn(1.3f);
                                c.RelativeColumn(1.6f);
                                c.RelativeColumn(1f);
                                c.RelativeColumn(1.7f);
                            });

                            void Head(string text, bool right = false)
                            {
                                var cell = table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(3);
                                (right ? cell.AlignRight() : cell.AlignLeft()).Text(text).SemiBold().FontSize(9);
                            }

                            Head("#");
                            Head(T("Beschreibung", "Description"));
                            Head(T("Menge", "Qty"), true);
                            Head(T("Einzelpreis", "Unit price"), true);
                            Head(T("USt", "VAT"), true);
                            Head(T("Summe", "Total"), true);

                            var i = 0;
                            foreach (var line in d.Lines)
                            {
                                i++;
                                void Body(string text, bool right = false)
                                {
                                    var cell = table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3);
                                    (right ? cell.AlignRight() : cell.AlignLeft()).Text(text);
                                }

                                Body(i.ToString(culture));
                                Body(line.Name);
                                Body(line.Quantity.ToString("0.##", culture) + (string.IsNullOrWhiteSpace(line.Unit) ? "" : " " + UnitLabel(line.Unit, german)), true);
                                Body(line.UnitPrice.ToString("N2", culture), true);
                                Body(line.TaxPercent.ToString("0.##", culture) + " %", true);
                                Body(line.Total.HasValue ? line.Total.Value.ToString("N2", culture) : "", true);
                            }
                        });
                    }

                    col.Item().AlignRight().Width(260).Column(sum =>
                    {
                        void Row(string label, string value, bool bold = false) =>
                            sum.Item().Row(r =>
                            {
                                var l = r.RelativeItem().Text(label);
                                var v = r.ConstantItem(110).AlignRight().Text(value);
                                if (bold) { l.SemiBold(); v.SemiBold(); }
                            });

                        if (d.NetAmount.HasValue) { Row(T("Netto", "Net"), Money(d.NetAmount)); }
                        foreach (var tax in d.Taxes)
                        {
                            Row($"{T("USt", "VAT")} {tax.Percent.ToString("0.##", culture)} %", Money(tax.Amount));
                        }
                        if (d.Taxes.Count == 0 && d.TaxAmount.HasValue) { Row(T("USt", "VAT"), Money(d.TaxAmount)); }
                        if (d.GrossAmount.HasValue) { Row(T("Brutto", "Gross"), Money(d.GrossAmount), bold: true); }
                        if (d.DueAmount.HasValue && d.DueAmount != d.GrossAmount) { Row(T("Zu zahlen", "Amount due"), Money(d.DueAmount), bold: true); }
                    });

                    foreach (var note in d.Notes)
                    {
                        col.Item().Text(note).FontColor(Colors.Grey.Darken2);
                    }
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(8).FontColor(Colors.Grey.Darken1));
                    text.Span(T(
                        "Dieses PDF wurde aus der XRechnung (XML) erzeugt. Maßgeblich ist die beiliegende XML-Datei. Seite ",
                        "This PDF was generated from the XRechnung (XML). The accompanying XML file is authoritative. Page "));
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        }).GeneratePdf();
    }

    // UN/ECE Recommendation 20 codes as they appear in invoices; unknown codes are shown as they are.
    private static string UnitLabel(string code, bool german) => code.ToUpperInvariant() switch
    {
        "C62" or "H87" or "EA" => german ? "Stk" : "pcs",
        "HUR" => german ? "Std" : "h",
        "DAY" => german ? "Tag" : "day",
        "MON" => german ? "Monat" : "month",
        "ANN" => german ? "Jahr" : "year",
        "KGM" => "kg",
        "GRM" => "g",
        "MTR" => "m",
        "LTR" => "l",
        "KMT" => "km",
        "MTK" => "m²",
        "LS" => german ? "pauschal" : "lump sum",
        "SET" => "Set",
        _ => code
    };

    private static void Party(IContainer e, string heading, string? name, string? address, (string Label, string? Value)[] extra)
    {
        e.Column(c =>
        {
            c.Item().Text(heading).FontSize(8).FontColor(Colors.Grey.Darken1).SemiBold();
            c.Item().Text(string.IsNullOrWhiteSpace(name) ? "—" : name).SemiBold();
            if (!string.IsNullOrWhiteSpace(address)) { c.Item().Text(address); }
            foreach (var (label, value) in extra)
            {
                if (!string.IsNullOrWhiteSpace(value)) { c.Item().Text($"{label}: {value}"); }
            }
        });
    }

    private static void Facts(IContainer e, (string Label, string Value)[] facts)
    {
        e.Row(r =>
        {
            foreach (var (label, value) in facts.Where(f => !string.IsNullOrWhiteSpace(f.Value)))
            {
                r.RelativeItem().Column(c =>
                {
                    c.Item().Text(label).FontSize(8).FontColor(Colors.Grey.Darken1).SemiBold();
                    c.Item().Text(value);
                });
            }
        });
    }
}
