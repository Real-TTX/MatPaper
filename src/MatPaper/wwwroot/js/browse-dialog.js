// Folder/share picker dialog, shared by the import wizard and the storage-location editor.
//
// A form opts in with data-browse-url: a Razor handler that receives the whole form plus
// "scope" and "path" and answers { ok, entries: [{ path, name, count, hasChildren }] } or
// { ok: false, error }. The page renders the dialog once (partial _BrowseDialog). Each
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
            dialog.querySelector(".browse-dialog__list").innerHTML = html;
        }

        function load(path) {
            state.path = path || "";
            dialog.querySelector(".browse-dialog__path").textContent = state.path || "/";
            setList('<li class="browse-dialog__empty">' + escapeHtml(t("loading", "Loading…")) + "</li>");

            postJson(state.form, state.form.getAttribute("data-browse-url"), { scope: state.scope, path: state.path })
                .then(function (res) {
                    if (!res.ok) {
                        setList('<li class="browse-dialog__error">' + escapeHtml(res.error || "") + "</li>");
                        return;
                    }
                    var up = state.flat || !state.path || state.path === state.root ? "" : renderUpRow();
                    if (!res.entries.length) {
                        setList(up + '<li class="browse-dialog__empty">' + escapeHtml(t("empty", "Nothing found.")) + "</li>");
                        return;
                    }
                    setList(up + res.entries.map(renderEntry).join(""));
                })
                .catch(function (err) {
                    setList('<li class="browse-dialog__error">' + escapeHtml(String(err)) + "</li>");
                });
        }

        function parentOf(path) {
            var clean = String(path || "").replace(/[/\\]+$/, "");
            var cut = Math.max(clean.lastIndexOf("/"), clean.lastIndexOf("\\"));
            return cut <= 0 ? state.root : clean.slice(0, cut);
        }

        function renderUpRow() {
            return '<li class="browse-dialog__item browse-dialog__item--up">' +
                '<button type="button" class="browse-dialog__up">↑ ' + escapeHtml(t("up", "One level up")) + "</button></li>";
        }

        function renderEntry(entry) {
            var count = entry.count === null || entry.count === undefined
                ? ""
                : '<span class="badge badge--muted">' + escapeHtml(entry.count) + "</span>";
            var open = entry.hasChildren
                ? '<button type="button" class="icon-btn browse-dialog__open" data-path="' + escapeHtml(entry.path) + '" title="›">›</button>'
                : "";
            return '<li class="browse-dialog__item">' +
                '<button type="button" class="browse-dialog__pick" data-path="' + escapeHtml(entry.path) + '">' +
                escapeHtml(entry.name) + "</button>" + count + open + "</li>";
        }

        function open(button) {
            state.form = button.closest("form");
            state.scope = button.getAttribute("data-browse");
            state.targetId = button.getAttribute("data-browse-target");
            state.root = button.getAttribute("data-browse-root") || "";
            state.flat = button.hasAttribute("data-browse-flat");

            dialog.querySelector(".browse-dialog__title").textContent =
                button.getAttribute("data-browse-title") || t("folders", "Folders");
            dialog.querySelector(".browse-dialog__footer").hidden = state.flat;
            dialog.querySelector(".browse-dialog__path").hidden = state.flat;
            if (typeof dialog.showModal === "function") { dialog.showModal(); }

            // Folder pickers start where the field points; flat lists always start at the top.
            var current = targetInput() ? targetInput().value.trim() : "";
            load(state.flat ? "" : (current || state.root));
        }

        function choose(path) {
            var input = targetInput();
            if (input) {
                input.value = path;
                input.dispatchEvent(new Event("change", { bubbles: true }));
            }
            dialog.close();
        }

        document.querySelectorAll("form[data-browse-url] [data-browse]").forEach(function (button) {
            button.addEventListener("click", function () { open(button); });
        });

        // Clicking the backdrop (the dialog element itself, outside its content) closes it.
        dialog.addEventListener("mousedown", function (ev) {
            if (ev.target === dialog) { dialog.close(); }
        });

        dialog.addEventListener("click", function (ev) {
            var pick = ev.target.closest(".browse-dialog__pick");
            if (pick) { choose(pick.getAttribute("data-path")); return; }

            var openBtn = ev.target.closest(".browse-dialog__open");
            if (openBtn) { load(openBtn.getAttribute("data-path")); return; }

            if (ev.target.closest(".browse-dialog__up")) { load(parentOf(state.path)); return; }
            if (ev.target.closest(".browse-dialog__close")) { dialog.close(); return; }
            if (ev.target.closest(".browse-dialog__use")) { choose(state.path); }
        });
    });
})();
