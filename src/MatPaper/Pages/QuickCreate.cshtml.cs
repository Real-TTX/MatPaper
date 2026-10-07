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

        var suggestions = CorrespondentSuggester.Suggest(text);
        var known = await _db.Correspondents.AsNoTracking()
            .Where(c => c.UpdateState != UpdateState.Deleted)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync();

        var items = suggestions.Select(name =>
        {
            var hit = known.FirstOrDefault(k => k.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? known.FirstOrDefault(k => k.Name.Length >= 4 && (name.Contains(k.Name, StringComparison.OrdinalIgnoreCase) || k.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
            return new { name, id = hit?.Id, existing = hit?.Name };
        }).ToList();
        return new JsonResult(new { ok = true, items });
    }

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
