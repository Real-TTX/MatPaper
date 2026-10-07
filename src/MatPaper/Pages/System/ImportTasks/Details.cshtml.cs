using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.System.ImportTasks;

/// <summary>Read-only view of an import task: what it does, its runs, and what it brought in.</summary>
public class DetailsModel : PageModel
{
    private const int RecentDocuments = 10;

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

    public ImportTask Task { get; private set; } = new();

    /// <summary>The group the rule belongs to, if any.</summary>
    public ImportGroup? Group { get; private set; }
    public TaskRunsPanel Runs { get; private set; } = null!;
    public TaskRun? LastRun { get; private set; }

    /// <summary>Label/value pairs describing where the task reads from.</summary>
    public List<(string Label, string Value)> Source { get; } = new();

    /// <summary>Label/value pairs describing how imported documents are filed.</summary>
    public List<(string Label, string Value)> Filing { get; } = new();

    public int DocumentCount { get; private set; }
    public IReadOnlyList<RecentDocument> Documents { get; private set; } = Array.Empty<RecentDocument>();

    public record RecentDocument(long Id, Guid Token, string Title, string FileName, DateTime Imported, bool HasThumbnail, ReviewState Review, bool IsStaged);

    public async Task<IActionResult> OnGetAsync()
    {
        var task = await _db.ImportTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == Id && t.UpdateState != UpdateState.Deleted);
        if (task is null)
        {
            return NotFound();
        }

        Task = task;
        ViewData["Breadcrumb"] = _l["System / Import tasks / View"].Value;
        ViewData["Title"] = task.Name;

        if (task.GroupId is long gid)
        {
            Group = await _db.ImportGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gid && g.UpdateState != UpdateState.Deleted);
        }

        // what the rule really uses: its own settings completed by the group's target
        await DescribeAsync(ImportGroupDefaults.Effective(task, Group));

        var state = ImportSync.Load(task);
        var hasState = !string.IsNullOrWhiteSpace(task.SyncState);
        string memory = hasState && state.LastUid > 0
            ? _l["Remembers: up to message {0}.", state.LastUid].Value
            : hasState && state.Seen.Count > 0
                ? _l["Remembers {0} handled item(s).", state.Seen.Count].Value
                : _l["Nothing remembered yet."].Value;

        Runs = new TaskRunsPanel(
            TaskRunKind.Import, Id,
            $"/System/ImportTasks/Edit?id={Id}&handler=Run",
            _l["Run now"].Value,
            memory,
            $"/System/ImportTasks/Edit?id={Id}&handler=ResetSync",
            await TaskRunsPanel.LoadRunsAsync(_db, TaskRunKind.Import, Id));
        LastRun = Runs.Runs.FirstOrDefault();

        var docs = _db.Documents.AsNoTracking()
            .Where(d => d.ImportTaskId == Id && d.UpdateState != UpdateState.Deleted);
        DocumentCount = await docs.CountAsync();
        Documents = await docs
            .OrderByDescending(d => d.CreateDate)
            .Take(RecentDocuments)
            .Select(d => new RecentDocument(d.Id, d.Token, d.Title, d.OriginalFileName, d.CreateDate,
                d.ThumbnailPath != null, d.ReviewState, d.IsStaged))
            .ToListAsync();

        return Page();
    }

    private async Task DescribeAsync(ImportTask task)
    {
        const string none = "—";
        long? locationId, correspondentId, typeId, projectId, ownerId;
        List<long> tagIds;
        bool skipInbox, isCommon;
        string lookbackMode, postAction;
        int lookbackDays;
        DateTime? lookbackDate;
        long? connectionId = null;

        switch (task.Type)
        {
            case ImportTaskType.Imap:
            case ImportTaskType.Pop3:
            {
                var s = TaskSettingsJson.Read<MailImportSettings>(task.SettingsJson);
                connectionId = s.ConnectionId;
                Source.Add((_l["Mailbox"].Value, string.IsNullOrWhiteSpace(s.Username) ? s.Host : $"{s.Username} @ {s.Host}"));
                if (task.Type == ImportTaskType.Imap) { Source.Add((_l["Folder"].Value, s.Folder)); }
                AddIfSet(Source, _l["From contains"].Value, s.FromFilter);
                AddIfSet(Source, _l["To contains"].Value, s.ToFilter);
                AddIfSet(Source, _l["Subject contains"].Value, s.SubjectFilter);
                Source.Add((_l["Attachments"].Value, s.AttachmentExtensions + (s.ImportBodyAsPdf ? " + " + _l["Mail body as PDF"].Value : "")));
                locationId = s.StorageLocationId; correspondentId = s.CorrespondentId; typeId = s.DocumentTypeId;
                projectId = s.ProjectId; tagIds = s.TagIds; skipInbox = s.SkipInbox; isCommon = s.IsCommon; ownerId = s.OwnerUserId;
                lookbackMode = s.LookbackMode; lookbackDays = s.LookbackDays; lookbackDate = s.LookbackDate;
                postAction = s.PostAction;
                break;
            }
            case ImportTaskType.Smb:
            {
                var s = TaskSettingsJson.Read<SmbImportSettings>(task.SettingsJson);
                connectionId = s.ConnectionId;
                Source.Add((_l["SMB share"].Value, "\\\\" + s.Host + "\\" + s.Share));
                Source.Add((_l["Path"].Value, string.IsNullOrWhiteSpace(s.Path) ? "/" : s.Path));
                Source.Add((_l["Pattern"].Value, s.Pattern + (s.Recursive ? " (" + _l["with subfolders"].Value + ")" : "")));
                locationId = s.StorageLocationId; correspondentId = s.CorrespondentId; typeId = s.DocumentTypeId;
                projectId = s.ProjectId; tagIds = s.TagIds; skipInbox = s.SkipInbox; isCommon = s.IsCommon; ownerId = s.OwnerUserId;
                lookbackMode = s.LookbackMode; lookbackDays = s.LookbackDays; lookbackDate = s.LookbackDate;
                postAction = s.PostAction;
                break;
            }
            default:
            {
                var s = TaskSettingsJson.Read<FilesystemImportSettings>(task.SettingsJson);
                Source.Add((_l["Path"].Value, s.SourcePath));
                Source.Add((_l["Pattern"].Value, s.Pattern + (s.Recursive ? " (" + _l["with subfolders"].Value + ")" : "")));
                locationId = s.StorageLocationId; correspondentId = s.CorrespondentId; typeId = s.DocumentTypeId;
                projectId = s.ProjectId; tagIds = s.TagIds; skipInbox = s.SkipInbox; isCommon = s.IsCommon; ownerId = s.OwnerUserId;
                lookbackMode = s.LookbackMode; lookbackDays = s.LookbackDays; lookbackDate = s.LookbackDate;
                postAction = s.PostAction;
                break;
            }
        }

        if (connectionId is long cid)
        {
            var name = await _db.Connections.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync();
            if (name is not null) { Source.Insert(0, (_l["Connection"].Value, name)); }
        }

        Source.Add((_l["Period"].Value, lookbackMode switch
        {
            "days" => _l["Last {0} days", lookbackDays].Value,
            "date" => _l["From {0}", lookbackDate is { } d ? _fmt.Date(d) : none].Value,
            _ => _l["Everything"].Value
        }));
        var isMail = task.Type is ImportTaskType.Imap or ImportTaskType.Pop3;
        Source.Add((_l["After import"].Value, isMail ? DescribeMailPost(postAction, task.Type) : postAction switch
        {
            "delete" => _l["Delete at the source"].Value,
            "move" => _l["Move at the source"].Value,
            "markseen" => _l["Mark as read"].Value,
            _ => _l["Leave untouched"].Value
        }));

        Filing.Add((_l["Storage location"].Value, locationId is long lid
            ? await _db.StorageLocations.AsNoTracking().Where(x => x.Id == lid).Select(x => x.Name).FirstOrDefaultAsync() ?? none
            : _l["Default location"].Value));
        if (correspondentId is long coid)
        {
            Filing.Add((_l["Correspondent"].Value, await _db.Correspondents.AsNoTracking().Where(x => x.Id == coid).Select(x => x.Name).FirstOrDefaultAsync() ?? none));
        }
        if (typeId is long tid)
        {
            Filing.Add((_l["Document type"].Value, await _db.DocumentTypes.AsNoTracking().Where(x => x.Id == tid).Select(x => x.Name).FirstOrDefaultAsync() ?? none));
        }
        if (projectId is long pid)
        {
            Filing.Add((_l["Project"].Value, await _db.Projects.AsNoTracking().Where(x => x.Id == pid).Select(x => x.Name).FirstOrDefaultAsync() ?? none));
        }
        if (tagIds is { Count: > 0 })
        {
            var names = await _db.Tags.AsNoTracking().Where(x => tagIds.Contains(x.Id)).OrderBy(x => x.Name).Select(x => x.Name).ToListAsync();
            Filing.Add((_l["Tags"].Value, string.Join(", ", names)));
        }
        Filing.Add((_l["Lands in"].Value, isCommon ? _l["Common area"].Value : _l["Review inbox"].Value));
        if (skipInbox) { Filing.Add((_l["Review"].Value, _l["Skips the inbox"].Value)); }
        if (ownerId is long oid)
        {
            var owner = await _db.Users.AsNoTracking().Where(x => x.Id == oid).Select(x => x.Username).FirstOrDefaultAsync();
            if (owner is not null) { Filing.Add((_l["Owner"].Value, owner)); }
        }
    }

    private string DescribeMailPost(string postAction, ImportTaskType type)
    {
        var p = MailPostActions.Parse(postAction);
        var parts = new List<string>();
        if (type == ImportTaskType.Imap)
        {
            if (p.MarkSeen) { parts.Add(_l["Mark as read"].Value); }
            if (p.Flag) { parts.Add(_l["Mark as important (star)"].Value); }
            if (p.Move) { parts.Add(_l["Move at the source"].Value); }
        }
        if (p.Delete) { parts.Add(_l["Delete at the source"].Value); }
        return parts.Count == 0 ? _l["Leave untouched"].Value : string.Join(", ", parts);
    }

    private static void AddIfSet(List<(string, string)> list, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) { list.Add((label, value)); }
    }
}
