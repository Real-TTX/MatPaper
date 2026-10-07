using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.System.TaskRuns;

/// <summary>Stops a running (or queued) task and goes back to the page the button was on.</summary>
public class CancelModel : PageModel
{
    private readonly TaskTriggerQueue _queue;
    private readonly IStringLocalizer<SharedResource> _l;

    public CancelModel(TaskTriggerQueue queue, IStringLocalizer<SharedResource> l)
    {
        _queue = queue;
        _l = l;
    }

    public IActionResult OnGet() => RedirectToPage("Index");

    public IActionResult OnPost(TaskRunKind kind, long taskId)
    {
        if (_queue.Cancel(kind, taskId))
        {
            this.Notify(_l["Stopping … the run ends at its next checkpoint."].Value);
        }
        else
        {
            this.Notify(_l["Nothing is running there."].Value, NoticeKind.Warn);
        }

        var back = Request.Headers.Referer.ToString();
        if (Uri.TryCreate(back, UriKind.Absolute, out var uri) && string.Equals(uri.Host, Request.Host.Host, StringComparison.OrdinalIgnoreCase))
        {
            return LocalRedirect(uri.PathAndQuery);
        }

        return RedirectToPage("Index");
    }
}
