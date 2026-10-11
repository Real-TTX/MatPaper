using System.Globalization;
using MatPaper.Configuration;
using MatPaper.Services;
using MatPaper.Services.Barcodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;
using Microsoft.Net.Http.Headers;

namespace MatPaper.Pages.Documents;

/// <summary>
/// Sheets of labels with archive numbers (a QR code or bar code and the number) to print and stick on the papers, and
/// separator sheets to put between the documents of a stack that is scanned in one go. The numbers continue where the
/// last sheet ended, so no number is printed twice.
/// </summary>
public class LabelsModel : PageModel
{
    private const string CustomFormat = "custom";

    private readonly ArchiveNumberService _numbers;
    private readonly AppConfig _config;
    private readonly IStringLocalizer<SharedResource> _l;

    public LabelsModel(ArchiveNumberService numbers, AppConfig config, IStringLocalizer<SharedResource> l)
    {
        _numbers = numbers;
        _config = config;
        _l = l;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        /// <summary>The id of a sheet in the list, or "custom".</summary>
        public string Format { get; set; } = "avery-l4731";

        /// <summary>The first number on the sheets.</summary>
        public long Start { get; set; }

        public int Count { get; set; } = 189;

        public string Kind { get; set; } = "Qr";
        public bool ShowText { get; set; } = true;

        /// <summary>A frame around every label: for a test print on plain paper.</summary>
        public bool Border { get; set; }

        /// <summary>Labels already used at the start of the first sheet.</summary>
        public int Skip { get; set; }

        // Lengths in millimetres are typed as text and read here: a number field would send 2.54 and the binder of a
        // German page would take that for 254.

        /// <summary>Shifts the print by this many millimetres (right / down): for a printer that does not hit the labels.</summary>
        public string? OffsetX { get; set; } = "0";
        public string? OffsetY { get; set; } = "0";

        // an own grid (Format = custom)
        public int Columns { get; set; } = 3;
        public int Rows { get; set; } = 8;
        public string? LabelWidth { get; set; } = "70";
        public string? LabelHeight { get; set; } = "36";
        public string? GapX { get; set; } = "0";
        public string? GapY { get; set; } = "0";

        /// <summary>A label printer: one label per page, the page as big as the label.</summary>
        public bool OnePerPage { get; set; }

        // separator sheets
        public int SeparatorCount { get; set; } = 5;
        public string SeparatorKind { get; set; } = "Qr";
    }

    public long NextFree { get; private set; }
    public long LastPrinted { get; private set; }
    public bool Enabled { get; private set; }
    public string SeparatorText { get; private set; } = string.Empty;
    public string ExampleText { get; private set; } = string.Empty;

    public IReadOnlyList<LabelFormat> Formats => LabelSheets.Formats;

    public List<SelectListItem> KindOptions => new()
    {
        new(_l["QR code"].Value, nameof(BarcodeKind.Qr)),
        new(_l["Barcode (Code 128)"].Value, nameof(BarcodeKind.Code128))
    };

    public async Task OnGetAsync(CancellationToken ct)
    {
        await FillAsync(ct);
        Input.Start = NextFree;
    }

    /// <summary>The sheets of labels as a PDF (shown in the browser, ready to print).</summary>
    public async Task<IActionResult> OnPostLabelsAsync(CancellationToken ct)
    {
        await FillAsync(ct);
        var options = _config.ResolveBarcodeOptions();

        var format = BuildFormat();
        if (Input.Start < 1 || Input.Start > 999_999_999_000)
        {
            ModelState.AddModelError("Input.Start", _l["Enter a whole number greater than zero."]);
        }

        if (Input.Count < 1 || Input.Count > LabelSheets.MaxLabels)
        {
            ModelState.AddModelError("Input.Count", _l["Enter a number between 1 and {0}.", LabelSheets.MaxLabels]);
        }

        if (format is null)
        {
            ModelState.AddModelError("Input.Format", _l["Choose a sheet from the list."]);
        }

        if (!ModelState.IsValid || format is null)
        {
            return Page();
        }

        var request = new LabelRequest(
            format, Input.Start, Input.Count, ParseKind(Input.Kind), Input.ShowText, Input.Border,
            Math.Max(0, Input.Skip), Math.Clamp(Millimetres(Input.OffsetX, 0), -20, 20), Math.Clamp(Millimetres(Input.OffsetY, 0), -20, 20));

        byte[] pdf;
        try
        {
            pdf = await Task.Run(() => LabelSheets.Render(request, options), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ModelState.AddModelError(string.Empty, _l["The labels could not be made: {0}", ex.Message]);
            return Page();
        }

        // a test print on plain paper uses up no numbers; real labels do - the next sheet goes on behind them
        if (!Input.Border)
        {
            _numbers.NotePrinted(Input.Start + Input.Count - 1);
        }

        return Inline(pdf, $"{options.AsnPrefix}-{Input.Start}-{Input.Start + Input.Count - 1}.pdf");
    }

    /// <summary>Separator sheets as a PDF.</summary>
    public async Task<IActionResult> OnPostSeparatorsAsync(CancellationToken ct)
    {
        await FillAsync(ct);
        var options = _config.ResolveBarcodeOptions();

        var texts = new SeparatorTexts(
            _l["Separator sheet"].Value,
            _l["Scan stacks of papers in one go: put this sheet between two documents."].Value,
            _l["MatPaper cuts the scan at this sheet and leaves the sheet out."].Value);

        var count = Math.Clamp(Input.SeparatorCount, 1, 500);
        var pdf = await Task.Run(() => LabelSheets.RenderSeparators(count, ParseKind(Input.SeparatorKind), options, texts), ct);
        return Inline(pdf, "separator-sheets.pdf");
    }

    private async Task FillAsync(CancellationToken ct)
    {
        ViewData["Breadcrumb"] = "Documents / Labels";
        var options = _config.ResolveBarcodeOptions();
        Enabled = options.Enabled;
        SeparatorText = options.SeparatorText;
        ExampleText = options.AsnReadable(1);
        LastPrinted = _numbers.LastPrinted;
        NextFree = await _numbers.NextFreeAsync(ct);
    }

    /// <summary>The sheet that was asked for: one of the list, or the grid typed in (null when it makes no sense).</summary>
    private LabelFormat? BuildFormat()
    {
        if (!string.Equals(Input.Format, CustomFormat, StringComparison.OrdinalIgnoreCase))
        {
            return LabelSheets.Find(Input.Format);
        }

        var width = Millimetres(Input.LabelWidth, 0);
        var height = Millimetres(Input.LabelHeight, 0);
        if (width < 5 || width > 400 || height < 5 || height > 400)
        {
            ModelState.AddModelError("Input.LabelWidth", _l["A label is between 5 and 400 mm."]);
            return null;
        }

        if (Input.OnePerPage)
        {
            // a label printer feeds a label per page: the page is as big as the label
            return new LabelFormat(CustomFormat, "label printer", 1, 1, width, height, 0, 0, width, height);
        }

        var gapX = Math.Max(0, Millimetres(Input.GapX, 0));
        var gapY = Math.Max(0, Millimetres(Input.GapY, 0));
        if (Input.Columns < 1 || Input.Columns > 20 || Input.Rows < 1 || Input.Rows > 60
            || Input.Columns * width + (Input.Columns - 1) * gapX > 210
            || Input.Rows * height + (Input.Rows - 1) * gapY > 297)
        {
            ModelState.AddModelError("Input.Columns", _l["The labels do not fit on an A4 sheet."]);
            return null;
        }

        return new LabelFormat(CustomFormat, "own", Input.Columns, Input.Rows, width, height, gapX, gapY);
    }

    /// <summary>A length typed by a person (2,54 or 2.54); the fallback when it is not a number.</summary>
    private static double Millimetres(string? text, double fallback)
        => double.TryParse((text ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : fallback;

    private static BarcodeKind ParseKind(string? kind)
        => Enum.TryParse<BarcodeKind>(kind, ignoreCase: true, out var parsed) ? parsed : BarcodeKind.Qr;

    private IActionResult Inline(byte[] pdf, string fileName)
    {
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(fileName);
        Response.Headers.ContentDisposition = disposition.ToString();
        Response.Headers.CacheControl = "no-store";
        return File(pdf, "application/pdf");
    }

}
