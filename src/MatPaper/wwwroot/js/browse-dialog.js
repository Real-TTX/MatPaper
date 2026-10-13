// Folder/share picker dialog, shared by the import wizard and the storage-location editor.
//
// A form opts in with data-browse-url: a Razor handler that receives the whole form plus
// "scope" and "path" and answers { ok, entries: [{ path, name, count, hasChildren }] } or
// { ok: false, error }. The page renders the dialog once (partial _BrowseDialog, a dialog of the toolkit). Each
// trigger button carries:
//   data-browse         scope sent to the handler (e.g. "smb-folders")
//   data-browse-target  id of the input that receives the chosen path
//   data-browse-root    optional top of the tree ("/" for container folders); default ""
//   data-browse-flat    present for a one-level list (shares, mail folders): no path line,
//                       no "one level up", no "use this folder"
//   data-browse-title   optional dialog title (default: the form's data-t-folders)
// Texts come from the form's data-t-* attributes.
(function () {
    "use strict";

    function escapeHtml(value) {
        return String(value).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
        });
    }

    function icon(name) {
        return '<svg class="mat-icon" aria-hidden="true" focusable="false"><use href="#mat-i-' + name + '"></use></svg>';
    }

    function text(form, name, fallback) {
        return (form && form.getAttribute("data-t-" + name)) || fallback;
    }

    function token(form) {
        var el = form.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : "";
    }

    // One place for "POST the form, expect JSON": status and content type are checked
    // so a redirect to the login page cannot surface as a JSON syntax error.
    function postJson(form, url, extra) {
        var data = new FormData(form);
        if (extra) {
            Object.keys(extra).forEach(function (k) { data.append(k, extra[k]); });
        }

        var controller = typeof AbortController === "function" ? new AbortController() : null;
        var timer = controller ? setTimeout(function () { controller.abort(); }, 60000) : null;

        return fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: { "RequestVerificationToken": token(form) },
            body: data,
            signal: controller ? controller.signal : undefined
        }).catch(function () {
            // The browser only says "Failed to fetch" when the connection broke or timed out.
            if (timer) { clearTimeout(timer); }
            throw new Error(text(form, "request-failed", "The request failed — please reload the page."));
        }).then(function (r) {
            if (timer) { clearTimeout(timer); }
            var type = r.headers.get("content-type") || "";
            if (!r.ok || type.indexOf("json") === -1) {
                throw new Error(text(form, "request-failed", "The request failed — please reload the page."));
            }
            return r.json();
        });
    }

    // Shared with page scripts (the wizard's preview uses the same request helper).
    window.MatPaperBrowse = { postJson: postJson, escapeHtml: escapeHtml };

    document.addEventListener("DOMContentLoaded", function () {
        var dialog = document.getElementById("browse-dialog");
        if (!dialog) { return; }

        var state = { form: null, scope: null, targetId: null, root: "", flat: false, path: "" };

        function t(name, fallback) { return text(state.form, name, fallback); }

        function targetInput() {
            return state.targetId ? document.getElementById(state.targetId) : null;
        }

        function setList(html) {
            dialog.querySelector(".mp-browse__list").innerHTML = html;
        }

        function message(kind, content) {
            return '<li class="mat-listgroup__item mp-browse__' + kind + '">' + escapeHtml(content) + "</li>";
        }

        function load(path) {
            state.path = path || "";
            dialog.querySelector(".mp-browse__path").textContent = state.path || "/";
            setList(message("empty", t("loading", "Loading…")));

            postJson(state.form, state.form.getAttribute("data-browse-url"), { scope: state.scope, path: state.path })
                .then(function (res) {
                    if (!res.ok) {
                        setList(message("error", res.error || ""));
                        return;
                    }
                    var up = state.flat || !state.path || state.path === state.root ? "" : renderUpRow();
                    if (!res.entries.length) {
                        setList(up + message("empty", t("empty", "Nothing found.")));
                        return;
                    }
                    setList(up + res.entries.map(renderEntry).join(""));
                })
                .catch(function (err) {
                    setList(message("error", String(err && err.message ? err.message : err)));
                });
        }

        function parentOf(path) {
            var clean = String(path || "").replace(/[/\\]+$/, "");
            var cut = Math.max(clean.lastIndexOf("/"), clean.lastIndexOf("\\"));
            return cut <= 0 ? state.root : clean.slice(0, cut);
        }

        function renderUpRow() {
            return '<li class="mat-listgroup__item"><button type="button" class="mat-btn mat-btn--ghost mat-btn--sm mp-browse__up">' +
                icon("arrow-up") + "<span>" + escapeHtml(t("up", "One level up")) + "</span></button></li>";
        }

        function renderEntry(entry) {
            var count = entry.count === null || entry.count === undefined
                ? ""
                : '<span class="mat-badge mat-badge--soft">' + escapeHtml(entry.count) + "</span>";
            var open = entry.hasChildren
                ? '<button type="button" class="mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm mp-browse__open" data-path="' + escapeHtml(entry.path) + '" aria-label="' + escapeHtml(entry.name) + ' ›">' + icon("chevron-right") + "</button>"
                : "";
            return '<li class="mat-listgroup__item">' + icon("folder") +
                '<button type="button" class="mat-listgroup__text mp-browse__pick" data-path="' + escapeHtml(entry.path) + '">' +
                escapeHtml(entry.name) + "</button>" + count + open + "</li>";
        }

        function open(button) {
            state.form = button.closest("form");
            state.scope = button.getAttribute("data-browse");
            state.targetId = button.getAttribute("data-browse-target");
            state.root = button.getAttribute("data-browse-root") || "";
            state.flat = button.hasAttribute("data-browse-flat");

            dialog.querySelector(".mat-dialog__title").textContent =
                button.getAttribute("data-browse-title") || t("folders", "Folders");
            dialog.querySelector(".mp-browse__footer").hidden = state.flat;
            dialog.querySelector(".mp-browse__path").hidden = state.flat;
            window.MatUi.dialog.open(dialog, button);

            // Folder pickers start where the field points; flat lists always start at the top.
            var current = targetInput() ? targetInput().value.trim() : "";
            load(state.flat ? "" : (current || state.root));
        }

        function choose(path) {
            var input = targetInput();
            if (input) {
                input.value = path;
                input.dispatchEvent(new Event("input", { bubbles: true }));
                input.dispatchEvent(new Event("change", { bubbles: true }));
            }
            window.MatUi.dialog.close(dialog);
        }

        // The buttons that open it (a delegated listener: the page may add some later).
        document.addEventListener("click", function (ev) {
            var button = ev.target.closest ? ev.target.closest("form[data-browse-url] [data-browse]") : null;
            if (button) { ev.preventDefault(); open(button); }
        });

        dialog.addEventListener("click", function (ev) {
            var pick = ev.target.closest(".mp-browse__pick");
            if (pick) { choose(pick.getAttribute("data-path")); return; }

            var openBtn = ev.target.closest(".mp-browse__open");
            if (openBtn) { load(openBtn.getAttribute("data-path")); return; }

            if (ev.target.closest(".mp-browse__up")) { load(parentOf(state.path)); return; }
            if (ev.target.closest(".mp-browse__use")) { choose(state.path); }
        });
    });
})();
