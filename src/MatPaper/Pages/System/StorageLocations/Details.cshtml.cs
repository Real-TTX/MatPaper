using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.System.StorageLocations;

/// <summary>Read-only view of a storage location: where it points, how files are named, its storage searches and what lies in it.</summary>
public class DetailsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IStringLocalizer<SharedResource> _l;
    private readonly Fmt _fmt;

    public DetailsModel(AppDbContext db, IStringLocalizer<SharedResource> l, Fmt fmt)
    {
        _db = db;
        _l = l;
        _fmt = fmt;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public StorageLocation Location { get; private set; } = new();
    public TaskRunsPanel Runs { get; private set; } = null!;
    public TaskRun? LastRun { get; private set; }
    public List<(string Label, string Value)> Place { get; } = new();
    public List<(string Label, string Value)> Files { get; } = new();
    public int Filed { get; private set; }
    public int Found { get; private set; }
    public int InInbox { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var loc = await _db.StorageLocations.AsNoTracking().Include(s => s.Connection).Include(s => s.DefaultOwner)
            .FirstOrDefaultAsync(s => s.Id == Id && s.UpdateState != UpdateState.Deleted);
        if (loc is null)
        {
            return NotFound();
        }

        Location = loc;
        ViewData["Breadcrumb"] = _l["System / Storage locations / View"].Value;
        ViewData["Title"] = loc.Name;

        Place.Add((_l["Type"].Value, loc.Kind switch { StorageKind.Smb => "SMB", StorageKind.Cloud => _l["Cloud drive"].Value, _ => _l["Local"].Value }));
        Place.Add((_l["Location"].Value, loc.DisplayRoot));
        if (loc.Connection is not null) { Place.Add((_l["Connection"].Value, loc.Connection.Name)); }
        Place.Add((_l["Default"].Value, loc.IsDefault ? _l["Yes"].Value : _l["No"].Value));

        Files.Add((_l["Path template"].Value, loc.PathTemplate));
        Files.Add((_l["Unique ID in the file name"].Value, loc.IdInFileName ? _l["Yes"].Value : _l["No"].Value));
        Files.Add((_l["Metadata files"].Value, loc.WriteMetadataFiles ? _l["Yes"].Value : _l["No"].Value));
        Files.Add((_l["Search file types"].Value, string.IsNullOrWhiteSpace(loc.ScanExtensions) ? _l["All files"].Value : loc.ScanExtensions));
        var sched = CronSchedule.Describe(loc.ScanCron, _fmt.TimeZone);
        Files.Add((_l["Automatic search"].Value, sched.Text));
        Files.Add((_l["Found documents go to"].Value, loc.DefaultIsCommon ? _l["Common area"].Value : (loc.DefaultOwner?.Username ?? _l["Whoever starts the search"].Value)));

        var docs = _db.Documents.AsNoTracking().Where(d => d.StorageLocationId == Id && d.UpdateState != UpdateState.Deleted);
        Filed = await docs.CountAsync(d => !d.IsStaged && d.ReviewState == ReviewState.Reviewed);
        Found = await docs.CountAsync(d => d.Origin == DocumentOrigin.StorageScan);
        InInbox = await docs.CountAsync(d => d.ReviewState == ReviewState.Pending);

        Runs = new TaskRunsPanel(
            TaskRunKind.Scan, Id, $"/System/StorageLocations?handler=Scan&id={Id}", _l["Search now"].Value,
            null, null, await TaskRunsPanel.LoadRunsAsync(_db, TaskRunKind.Scan, Id));
        LastRun = Runs.Runs.FirstOrDefault();
        return Page();
    }
}
