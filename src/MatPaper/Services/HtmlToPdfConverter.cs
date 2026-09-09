using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MatPaper.Services;

/// <summary>
/// Converts an e-mail body (HTML or plain text) into a self-contained, archival
/// PDF. The HTML is parsed with AngleSharp and reduced to readable text while
/// preserving basic block structure (headings, paragraphs and list items become
/// separate lines). Rendering is done with QuestPDF: a labelled header block
/// (From / To / Date / Subject), a divider and the wrapped body text.
/// This converter is best-effort and never throws on odd markup.
/// </summary>
public sealed class HtmlToPdfConverter
{
    // Block-level elements whose boundaries introduce a line break in the
    // extracted text. Inline elements simply contribute their text.
    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "br", "div", "dd", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3",
        "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre",
        "section", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul"
    };

    // Elements whose textual content is never part of the readable body.
    private static readonly HashSet<string> IgnoredElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "title", "noscript", "template", "iframe", "object"
    };

    /// <summary>
    /// Convenience overload for callers that only have a subject and body.
    /// </summary>
    public byte[] Convert(string html, string subject)
    {
        return Convert(html, subject, string.Empty, string.Empty, null);
    }

    /// <summary>
    /// Produces an archival PDF of an e-mail.
    /// </summary>
    /// <param name="html">The e-mail body; HTML or plain text.</param>
    /// <param name="subject">The e-mail subject.</param>
    /// <param name="from">The sender address/display value.</param>
    /// <param name="to">The recipient address/display value.</param>
    /// <param name="sentDate">The moment the e-mail was sent, if known.</param>
    /// <returns>The rendered PDF as a byte array.</returns>
    public byte[] Convert(string html, string subject, string from, string to, DateTimeOffset? sentDate)
    {
        var paragraphs = ExtractParagraphs(html);
        var dateText = sentDate.HasValue
            ? sentDate.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : string.Empty;

        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(text => text.FontSize(10).LineHeight(1.35f));

                page.Header().Element(header => ComposeHeader(header, subject, from, to, dateText));

                page.Content().PaddingTop(12).Column(column =>
                {
                    column.Spacing(8);

                    if (paragraphs.Count == 0)
                    {
                        column.Item().Text(text =>
                        {
                            text.Span("(No content)").Italic().FontColor(Colors.Grey.Medium);
                        });
                        return;
                    }

                    foreach (var paragraph in paragraphs)
                    {
                        column.Item().Text(paragraph);
                    }
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(8).FontColor(Colors.Grey.Medium));
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });

        return document.GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, string subject, string from, string to, string dateText)
    {
        container.Column(column =>
        {
            column.Spacing(4);

            column.Item().Text(text =>
            {
                text.Span("E-Mail").FontSize(9).SemiBold().FontColor(Colors.Grey.Darken1).LetterSpacing(0.1f);
            });

            AddHeaderRow(column, "From", from);
            AddHeaderRow(column, "To", to);
            AddHeaderRow(column, "Date", dateText);
            AddHeaderRow(column, "Subject", subject);

            column.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
        });
    }

    private static void AddHeaderRow(ColumnDescriptor column, string label, string value)
    {
        column.Item().Row(row =>
        {
            row.ConstantItem(70).Text(text =>
            {
                text.Span(label).SemiBold().FontColor(Colors.Grey.Darken2);
            });

            row.RelativeItem().Text(text =>
            {
                text.Span(string.IsNullOrWhiteSpace(value) ? "-" : value.Trim());
            });
        });
    }

    /// <summary>
    /// Reduces the input to a list of readable paragraphs. HTML is parsed and
    /// walked; plain text is split on blank lines. Whitespace is collapsed and
    /// entities are decoded (AngleSharp does this during parsing).
    /// </summary>
    private static List<string> ExtractParagraphs(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new List<string>();
        }

        if (!LooksLikeHtml(input))
        {
            return SplitPlainText(input);
        }

        try
        {
            var parser = new HtmlParser();
            var document = parser.ParseDocument(input);
            var body = (INode?)document.Body ?? document.DocumentElement;

            if (body is null)
            {
                return SplitPlainText(input);
            }

            var builder = new StringBuilder();
            Walk(body, builder);
            return SplitIntoParagraphs(builder.ToString());
        }
        catch
        {
            // Never throw on odd markup; fall back to a plain-text reading.
            return SplitPlainText(input);
        }
    }

    private static void Walk(INode node, StringBuilder builder)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child.NodeType)
            {
                case NodeType.Text:
                    AppendCollapsed(builder, child.TextContent);
                    break;

                case NodeType.Element:
                    var element = (AngleSharp.Dom.IElement)child;
                    var tag = element.LocalName;

                    if (IgnoredElements.Contains(tag))
                    {
                        continue;
                    }

                    var isBlock = BlockElements.Contains(tag);

                    if (string.Equals(tag, "li", StringComparison.OrdinalIgnoreCase))
                    {
                        EnsureLineBreak(builder);
                        builder.Append("• ");
                    }
                    else if (isBlock)
                    {
                        EnsureLineBreak(builder);
                    }

                    if (string.Equals(tag, "br", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(tag, "hr", StringComparison.OrdinalIgnoreCase))
                    {
                        EnsureLineBreak(builder);
                        continue;
                    }

                    Walk(element, builder);

                    if (isBlock)
                    {
                        EnsureLineBreak(builder);
                    }

                    break;
            }
        }
    }

    private static void AppendCollapsed(StringBuilder builder, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var previousWasSpace = builder.Length == 0 || builder[^1] == ' ' || builder[^1] == '\n';

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }

                continue;
            }

            builder.Append(ch);
            previousWasSpace = false;
        }
    }

    private static void EnsureLineBreak(StringBuilder builder)
    {
        if (builder.Length == 0)
        {
            return;
        }

        // Trim a trailing space so lines do not end with padding.
        while (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
        }

        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append('\n');
        }
    }

    private static List<string> SplitIntoParagraphs(string text)
    {
        var paragraphs = new List<string>();

        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                paragraphs.Add(trimmed);
            }
        }

        return paragraphs;
    }

    private static List<string> SplitPlainText(string input)
    {
        var normalized = input.Replace("\r\n", "\n").Replace('\r', '\n');
        var paragraphs = new List<string>();

        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                paragraphs.Add(trimmed);
            }
        }

        return paragraphs;
    }

    private static bool LooksLikeHtml(string input)
    {
        // Heuristic: contains an angle-bracket tag. Entity-only strings are
        // still treated as plain text, which is fine for archival purposes.
        var lessThan = input.IndexOf('<');
        if (lessThan < 0 || lessThan == input.Length - 1)
        {
            return false;
        }

        for (var i = lessThan + 1; i < input.Length; i++)
        {
            var ch = input[i];
            if (ch == '/' || char.IsLetter(ch) || ch == '!')
            {
                return true;
            }

            if (!char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        return false;
    }
}
