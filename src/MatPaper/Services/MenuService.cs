using MatPaper.Controls;
using MatPaper.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MatPaper.Services;

/// <summary>
/// The menus of the shell as data (<see cref="NavSet"/>): the everyday work of every user, with "Add document" as its main action (SHELL-19) and, for an
/// administrator, the administration behind the switcher at the end of the menu (SHELL-15). The layout draws them; the badge of the inbox says how many
/// documents wait there.
/// </summary>
public sealed class MenuService(CurrentUser user, AppDbContext db, IStringLocalizer<SharedResource> l)
{
    public const string WorkId = "work";
    public const string AdminId = "admin";

    public async Task<IReadOnlyList<NavSet>> BuildAsync(CancellationToken ct = default)
    {
        var pending = 0;
        if (user.UserId is long uid)
        {
            // The badge is no reason to lose the whole page (the page of an error is drawn with this menu): without the database it shows no number.
            try
            {
                pending = await db.Documents.InInboxOf(uid, user.IsAdmin).CountAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                pending = 0;
            }
        }

        var menus = new List<NavSet> { Work(pending) };
        if (user.IsAdmin)
        {
            menus.Add(Administration());
        }

        return menus;
    }

    private NavSet Work(int pending) => new(WorkId, l["Documents"].Value, "file-text",
    [
        new(null,
        [
            new(l["Add document"].Value, "/Documents/Upload", "plus", Primary: true),
            new(l["Dashboard"].Value, "/", "dashboard"),
            new(l["Inbox"].Value, "/Inbox", "inbox", pending > 0 ? NavBadge.Count(pending, "accent", l["to review"].Value) : null),
            new(l["Documents"].Value, "/Documents", "file-text"),
        ]),
        new(l["Organise"].Value,
        [
            new(l["Correspondents"].Value, "/Correspondents", "contact-round"),
            new(l["Tags"].Value, "/Tags", "tag"),
            new(l["Projects"].Value, "/Projects", "folder-kanban"),
        ]),
    ]);

    private NavSet Administration() => new(AdminId, l["Administration"].Value, "wrench",
    [
        new(null, [new(l["Overview"].Value, "/System", "dashboard")]),
        new(l["People and places"].Value,
        [
            new(l["Users"].Value, "/System/Users", "user"),
            new(l["Storage locations"].Value, "/System/StorageLocations", "hard-drive"),
            new(l["Document types"].Value, "/System/DocumentTypes", "shapes"),
        ]),
        new(l["Automation"].Value,
        [
            new(l["Connections"].Value, "/System/Connections", "plug"),
            new(l["Import tasks"].Value, "/System/ImportTasks", "download"),
            new(l["Export tasks"].Value, "/System/ExportTasks", "upload"),
            new(l["Task history"].Value, "/System/TaskRuns", "clock"),
        ]),
        new(l["Archive"].Value,
        [
            new(l["Archive numbers"].Value, "/System/ArchiveNumbers", "scan-barcode"),
            new(l["Blocklist"].Value, "/System/Blocklist", "shield"),
            new(l["Settings"].Value, "/System/Settings", "sliders"),
        ]),
    ]);
}
