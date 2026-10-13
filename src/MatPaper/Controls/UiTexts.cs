using Microsoft.AspNetCore.Html;
using Microsoft.Extensions.Localization;

namespace MatPaper.Controls;

public static partial class Ui
{
    /// <summary>
    /// The words the toolkit's script puts on the page by itself - the buttons of its dialogs, the messages of its form check - as attributes of the
    /// <c>html</c> element, which the script reads (<c>data-dlg-*</c>, <c>data-msg-*</c>, <c>data-copied</c>). The server knows the language of the user, the
    /// script does not (the toolkit is a copy, rule CODE-03: texts come from the app as data, never edited into the copy).
    /// Usage: <c>&lt;html … @Ui.RootTexts(L)&gt;</c>
    /// </summary>
    public static IHtmlContent RootTexts(IStringLocalizer l)
    {
        var texts = new (string Attribute, string Text)[]
        {
            ("data-dlg-ok", l["OK"].Value),
            ("data-dlg-cancel", l["Cancel"].Value),
            ("data-dlg-close", l["Close"].Value),
            ("data-dlg-confirm", l["Please confirm"].Value),
            ("data-dlg-input", l["Input"].Value),
            ("data-dlg-notice", l["Notice"].Value),
            ("data-copied", l["Copied"].Value),
            ("data-theme-label", l["Theme: {0}. Click for {1}."].Value),
            ("data-theme-name-system", l["System"].Value),
            ("data-theme-name-light", l["Light"].Value),
            ("data-theme-name-dark", l["Dark"].Value),
            ("data-msg-required", l["Please fill in this field."].Value),
            ("data-msg-type", l["Please enter a valid value."].Value),
            ("data-msg-pattern", l["The format is not valid."].Value),
        };

        return new HtmlString(string.Join(' ', texts.Select(t => $"{t.Attribute}=\"{Encode(t.Text)}\"")));
    }
}
