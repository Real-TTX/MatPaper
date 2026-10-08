using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages;

/// <summary>
/// Creates a correspondent, tag, document type or project from the "+" of a picker, without leaving
/// the form. A name that already exists (any case) answers with the existing entry, so a double click
/// or a typo-free retry never makes a duplicate.
/// </summary>
public class QuickCreateModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;

    public QuickCreateModel(AppDbContext db, CurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public void OnGet()
    {
    }

    /// <summary>Names a document's text suggests for its correspondent; an entry that exists already comes with its id.</summary>
    public async Task<IActionResult> OnGetSuggestAsync(long documentId)
    {
        var text = await _db.Documents.AsNoTracking()
            .AccessibleTo(_currentUser)
            .Where(d => d.Id == documentId)
            .Select(d => d.OcrText)
            .FirstOrDefaultAsync();

        var known = await LoadKnownAsync();
        return new JsonResult(new { ok = true, items = Describe(text, known) });
    }

    /// <summary>The same for a list of documents (the inbox asks once for the whole page).</summary>
    public async Task<IActionResult> OnGetSuggestManyAsync(string ids)
    {
        var wanted = (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var id) ? id : 0).Where(id => id > 0).Take(100).ToList();
        var texts = await _db.Documents.AsNoTracking()
            .AccessibleTo(_currentUser)
            .Where(d => wanted.Contains(d.Id))
            .Select(d => new { d.Id, d.OcrText })
            .ToListAsync();

        var known = await LoadKnownAsync();
        var result = texts.ToDictionary(t => t.Id.ToString(), t => Describe(t.OcrText, known));
        return new JsonResult(new { ok = true, documents = result });
    }

    private async Task<List<(long Id, string Name)>> LoadKnownAsync()
        => (await _db.Correspondents.AsNoTracking()
            .Where(c => c.UpdateState != UpdateState.Deleted)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync()).Select(c => (c.Id, c.Name)).ToList();

    private static List<object> Describe(string? text, List<(long Id, string Name)> known)
        => CorrespondentSuggester.Suggest(text).Select(name =>
        {
            var hit = known.Cast<(long Id, string Name)?>().FirstOrDefault(k => CorrespondentSuggester.SameName(k!.Value.Name, name));
            return (object)new { name, id = hit?.Id, existing = hit?.Name };
        }).ToList();

    public async Task<IActionResult> OnPostAsync(string kind, string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > 200)
        {
            return new JsonResult(new { ok = false });
        }

        var now = DateTime.UtcNow;
        var uid = _currentUser.UserId;
        var lower = name.ToLower();
        BaseEntity? entity;
        string display;

        switch (kind)
        {
            case "correspondent":
                entity = await _db.Correspondents.FirstOrDefaultAsync(x => x.UpdateState != UpdateState.Deleted && x.Name.ToLower() == lower)
                    ?? _db.Correspondents.Add(new Correspondent { Name = name, UpdateState = UpdateState.Created }).Entity;
                display = ((Correspondent)entity).Name;
                break;
            case "tag":
                entity = await _db.Tags.FirstOrDefaultAsync(x => x.UpdateState != UpdateState.Deleted && x.Name.ToLower() == lower)
                    ?? _db.Tags.Add(new Tag { Name = name, UpdateState = UpdateState.Created }).Entity;
                display = ((Tag)entity).Name;
                break;
            case "document-type":
                entity = await _db.DocumentTypes.FirstOrDefaultAsync(x => x.UpdateState != UpdateState.Deleted && x.Name.ToLower() == lower)
                    ?? _db.DocumentTypes.Add(new DocumentType { Name = name, UpdateState = UpdateState.Created }).Entity;
                display = ((DocumentType)entity).Name;
                break;
            case "project":
                entity = await _db.Projects.FirstOrDefaultAsync(x => x.UpdateState != UpdateState.Deleted && x.Name.ToLower() == lower)
                    ?? _db.Projects.Add(new Project { Name = name, UpdateState = UpdateState.Created, OwnerId = uid }).Entity;
                display = ((Project)entity).Name;
                break;
            default:
                return new JsonResult(new { ok = false });
        }

        if (_db.Entry(entity).State == EntityState.Added)
        {
            entity.CreateDate = now;
            entity.CreateUserId = uid;
            entity.UpdateDate = now;
            entity.UpdateUserId = uid;
            await _db.SaveChangesAsync();
        }

        return new JsonResult(new { ok = true, id = entity.Id, name = display });
    }
}
