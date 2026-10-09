// The preview of a document in a dialog (desktop): any element with data-doc-preview="<token of the document>" opens
// it on click - the document list, the inbox. The dialog (Pages/Shared/_DocPreviewDialog.cshtml) shows the file in the
// browser's own viewer and has the buttons edit, share, download, open in a tab and close.
//
//   data-doc-preview="<token>"              the trigger (a button, or a link that keeps its href for the no-script case)
//   data-doc-preview-title="<title>"        shown in the head of the dialog
//   data-doc-preview-edit="<url>"           where the edit button goes
//   data-doc-preview-share="true"           the share button is shown (only for what the user may publish)
//
// A click with a modifier key (new tab, new window) is left alone, so a link still opens in a tab that way. On a phone
// the page viewer takes the click first (doc-viewer.js).
(function () {
    "use strict";

    var dialog = document.getElementById("doc-preview");
    if (!dialog || typeof dialog.showModal !== "function") { return; }

    var frame = document.getElementById("doc-preview-frame");
    var titleEl = document.getElementById("doc-preview-title");
    var openEl = document.getElementById("doc-preview-open");
    var downloadEl = document.getElementById("doc-preview-download");
    var editEl = document.getElementById("doc-preview-edit");
    var shareEl = document.getElementById("doc-preview-share");

    function open(trigger) {
        var token = trigger.getAttribute("data-doc-preview");
        var title = trigger.getAttribute("data-doc-preview-title") || "";
        var url = "/Documents/" + token + "/view";
        frame.src = url;
        openEl.href = url;
        downloadEl.href = "/Documents/" + token + "/download";

        var edit = trigger.getAttribute("data-doc-preview-edit");
        editEl.hidden = !edit;
        if (edit) { editEl.href = edit; }

        shareEl.hidden = trigger.getAttribute("data-doc-preview-share") !== "true";
        shareEl.setAttribute("data-doc-token", token);
        shareEl.setAttribute("data-doc-title", title);

        titleEl.textContent = title || dialog.getAttribute("data-t-preview") || "";
        dialog.showModal();
    }

    document.addEventListener("click", function (event) {
        var trigger = event.target && event.target.closest ? event.target.closest("[data-doc-preview]") : null;
        if (!trigger || event.defaultPrevented) { return; }
        if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) { return; }
        event.preventDefault();
        open(trigger);
    });

    document.getElementById("doc-preview-close").addEventListener("click", function () { dialog.close(); });
    dialog.addEventListener("click", function (event) { if (event.target === dialog) { dialog.close(); } });
    // however it is closed (button, backdrop, Escape): the file is let go
    dialog.addEventListener("close", function () { frame.src = "about:blank"; });
})();
