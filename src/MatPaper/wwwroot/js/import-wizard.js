// Import-task wizard: step navigation, the source picker dialog and the dry-run
// preview. Everything lives in one form, so Save/Test/Run keep working and the
// server sees the complete task no matter which step is open.
(function () {
    "use strict";

    document.addEventListener("DOMContentLoaded", function () {
        var form = document.getElementById("import-wizard");
        if (!form) { return; }

        var steps = Array.prototype.slice.call(form.querySelectorAll(".wizard__step"));
        var panels = Array.prototype.slice.call(form.querySelectorAll(".wizard__panel"));
        var backBtn = document.getElementById("wizard-back");
        var nextBtn = document.getElementById("wizard-next");
        var typeInputs = form.querySelectorAll('[name="Input.Type"]');
        var stepField = document.getElementById("wizard-active-step");
        // A postback (Test connection, validation error) re-renders the page; the step
        // the user was on travels in a hidden field so they land back there.
        var current = parseInt(stepField && stepField.value, 10) || 1;

        function t(name, fallback) { return form.getAttribute("data-t-" + name) || fallback; }
        function fill(template, a) { return String(template).replace("{0}", a); }

        function currentType() {
            for (var i = 0; i < typeInputs.length; i++) {
                if (typeInputs[i].checked) { return typeInputs[i].value; }
            }
            return "2";
        }

        // A panel may declare data-for-type; without it the step applies to every type.
        function appliesToType(panel, type) {
            var only = panel.getAttribute("data-for-type");
            return !only || only.split(",").indexOf(type) !== -1;
        }

        function visibleSteps() {
            var type = currentType();
            return panels
                .filter(function (p) { return appliesToType(p, type); })
                .map(function (p) { return parseInt(p.getAttribute("data-step"), 10); });
        }

        function render() {
            var visible = visibleSteps();
            if (visible.indexOf(current) === -1) {
                current = visible[0];
            }

            panels.forEach(function (panel) {
                var step = parseInt(panel.getAttribute("data-step"), 10);
                panel.hidden = step !== current;
            });

            steps.forEach(function (chip) {
                var step = parseInt(chip.getAttribute("data-goto"), 10);
                var index = visible.indexOf(step);
                chip.hidden = index === -1;
                chip.classList.toggle("is-active", step === current);
                chip.classList.toggle("is-done", index !== -1 && index < visible.indexOf(current));
                var num = chip.querySelector(".wizard__num");
                if (num) { num.textContent = index === -1 ? "" : String(index + 1); }
            });

            if (stepField) { stepField.value = String(current); }

            var pos = visible.indexOf(current);
            backBtn.disabled = pos <= 0;
            nextBtn.disabled = pos === visible.length - 1;
        }

        function go(step) {
            current = step;
            render();
            form.scrollIntoView({ block: "start", behavior: "smooth" });
        }

        function step(delta) {
            var visible = visibleSteps();
            var pos = visible.indexOf(current) + delta;
            if (pos >= 0 && pos < visible.length) { go(visible[pos]); }
        }

        backBtn.addEventListener("click", function () { step(-1); });
        nextBtn.addEventListener("click", function () { step(1); });
        steps.forEach(function (chip) {
            chip.addEventListener("click", function () {
                var target = parseInt(chip.getAttribute("data-goto"), 10);
                if (visibleSteps().indexOf(target) !== -1) { go(target); }
            });
        });

        // ---- Type-driven defaults -----------------------------------------
        var portField = form.querySelector('[name="Input.Port"]');
        var sslField = form.querySelector('[name="Input.UseSsl"]');
        var postAction = form.querySelector('[name="Input.MailPostAction"]');

        function defaultPort() {
            var imap = currentType() === "0";
            var ssl = sslField ? sslField.checked : true;
            return imap ? (ssl ? 993 : 143) : (ssl ? 995 : 110);
        }

        // Only rewrite the port while it still holds a default, never a typed one.
        function syncPort() {
            if (!portField) { return; }
            var known = [993, 143, 995, 110, 0];
            var value = parseInt(portField.value, 10);
            if (isNaN(value) || known.indexOf(value) !== -1) {
                portField.value = defaultPort();
            }
        }

        // POP3 has neither "seen" flags nor folders.
        function syncPostAction() {
            if (!postAction) { return; }
            var isPop3 = currentType() === "1";
            ["markseen", "move"].forEach(function (value) {
                var option = postAction.querySelector('option[value="' + value + '"]');
                if (option) { option.hidden = isPop3; option.disabled = isPop3; }
            });
            if (isPop3 && (postAction.value === "markseen" || postAction.value === "move")) {
                postAction.value = "none";
                postAction.dispatchEvent(new Event("change", { bubbles: true }));
            }
        }

        function markSelectedTile() {
            for (var i = 0; i < typeInputs.length; i++) {
                var tile = typeInputs[i].closest(".type-tile");
                if (tile) { tile.classList.toggle("is-selected", typeInputs[i].checked); }
            }
        }

        for (var i = 0; i < typeInputs.length; i++) {
            typeInputs[i].addEventListener("change", function () {
                syncPort();
                syncPostAction();
                markSelectedTile();
                render();
            });
        }
        markSelectedTile();
        if (sslField) { sslField.addEventListener("change", syncPort); }

        syncPostAction();
        render();

        // ---- Source picker -------------------------------------------------
        var dialog = document.getElementById("browse-dialog");
        var browseUrl = form.getAttribute("data-browse-url");
        var state = { scope: null, targetId: null, path: "", stack: [] };

        // One place for "POST the form, expect JSON": status and content type are checked
        // so a redirect to the login page cannot surface as a JSON syntax error.
        function postJson(url, extra) {
            var data = new FormData(form);
            if (extra) {
                Object.keys(extra).forEach(function (k) { data.append(k, extra[k]); });
            }

            var controller = typeof AbortController === "function" ? new AbortController() : null;
            var timer = controller ? setTimeout(function () { controller.abort(); }, 60000) : null;

            return fetch(url, {
                method: "POST",
                credentials: "same-origin",
                headers: { "RequestVerificationToken": token() },
                body: data,
                signal: controller ? controller.signal : undefined
            }).then(function (r) {
                if (timer) { clearTimeout(timer); }
                var type = r.headers.get("content-type") || "";
                if (!r.ok || type.indexOf("json") === -1) {
                    throw new Error(t("request-failed", "The request failed — please reload the page."));
                }
                return r.json();
            });
        }

        function token() {
            var el = form.querySelector('input[name="__RequestVerificationToken"]');
            return el ? el.value : "";
        }

        function targetInput() {
            return state.targetId ? document.getElementById(state.targetId) : null;
        }

        function setList(html) {
            dialog.querySelector(".browse-dialog__list").innerHTML = html;
        }

        function setPath(text) {
            var el = dialog.querySelector(".browse-dialog__path");
            el.textContent = text || "/";
        }

        function load(path) {
            state.path = path || "";
            setPath(state.path);
            setList('<li class="browse-dialog__empty">' + t("loading", "Loading…") + "</li>");

            postJson(browseUrl, { scope: state.scope, path: state.path })
                .then(function (res) {
                    if (!res.ok) {
                        setList('<li class="browse-dialog__error">' + escapeHtml(res.error || "") + "</li>");
                        return;
                    }
                    var flatScope = state.scope === "smb-shares" || state.scope === "mail-folders";
                    var up = flatScope || !state.path || state.path === "/" ? "" : renderUpRow();
                    if (!res.entries.length) {
                        setList(up + '<li class="browse-dialog__empty">' + t("empty", "Nothing found.") + "</li>");
                        return;
                    }
                    setList(up + res.entries.map(renderEntry).join(""));
                })
                .catch(function (err) {
                    setList('<li class="browse-dialog__error">' + escapeHtml(String(err)) + "</li>");
                });
        }

        function parentOf(path) {
            var clean = String(path || "").replace(/[/]+$/, "");
            var cut = Math.max(clean.lastIndexOf("/"), clean.lastIndexOf("\\"));
            if (cut <= 0) { return state.scope === "local-folders" ? "/" : ""; }
            return clean.slice(0, cut);
        }

        function renderUpRow() {
            return '<li class="browse-dialog__item browse-dialog__item--up">' +
                '<button type="button" class="browse-dialog__up">↑ ' + escapeHtml(t("up", "One level up")) + "</button></li>";
        }

        function renderEntry(entry) {
            var count = entry.count === null || entry.count === undefined
                ? ""
                : '<span class="badge badge--muted">' + entry.count + "</span>";
            var open = entry.hasChildren
                ? '<button type="button" class="icon-btn browse-dialog__open" data-path="' + escapeHtml(entry.path) + '" title="›">›</button>'
                : "";
            return '<li class="browse-dialog__item">' +
                '<button type="button" class="browse-dialog__pick" data-path="' + escapeHtml(entry.path) + '">' +
                escapeHtml(entry.name) + "</button>" + count + open + "</li>";
        }

        function escapeHtml(value) {
            return String(value).replace(/[&<>"']/g, function (c) {
                return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
            });
        }

        function openDialog(scope, targetId, title) {
            state.scope = scope;
            state.targetId = targetId;
            state.stack = [];
            dialog.querySelector(".browse-dialog__title").textContent = title;
            // Shares are a flat list; folder pickers start where the field points.
            var start = scope === "smb-shares" ? "" : (targetInput() ? targetInput().value : "");
            // Flat lists (shares, mail folders) have neither a path line nor "use this folder".
            var flat = scope === "smb-shares" || scope === "mail-folders";
            dialog.querySelector(".browse-dialog__footer").hidden = flat;
            dialog.querySelector(".browse-dialog__path").hidden = flat;
            if (typeof dialog.showModal === "function") { dialog.showModal(); }
            load(scope === "local-folders" && !start ? "/" : start);
        }

        function choose(path) {
            var input = targetInput();
            if (input) {
                input.value = path;
                input.dispatchEvent(new Event("change", { bubbles: true }));
            }
            dialog.close();
        }

        form.querySelectorAll("[data-browse]").forEach(function (button) {
            button.addEventListener("click", function () {
                var scope = button.getAttribute("data-browse");
                var targetId = button.getAttribute("data-browse-target");
                if (!targetId) {
                    targetId = {
                        "smb-shares": "Input_SmbShare",
                        "smb-folders": "Input_SmbPath",
                        "local-folders": "Input_SourcePath",
                        "mail-folders": "Input_Folder"
                    }[scope];
                }
                var title = scope === "smb-shares" ? t("shares", "Shares") : t("folders", "Folders");
                openDialog(scope, targetId, title);
            });
        });

        // Clicking the backdrop (the dialog element itself, outside its content) closes it.
        dialog.addEventListener("mousedown", function (ev) {
            if (ev.target === dialog) { dialog.close(); }
        });

        dialog.addEventListener("click", function (ev) {
            var pick = ev.target.closest(".browse-dialog__pick");
            if (pick) { choose(pick.getAttribute("data-path")); return; }

            var open = ev.target.closest(".browse-dialog__open");
            if (open) { state.stack.push(state.path); load(open.getAttribute("data-path")); return; }

            if (ev.target.closest(".browse-dialog__up")) { load(parentOf(state.path)); return; }
            if (ev.target.closest(".browse-dialog__close")) { dialog.close(); return; }
            if (ev.target.closest(".browse-dialog__use")) { choose(state.path); }
        });

        // ---- Preview --------------------------------------------------------
        var previewBtn = document.getElementById("preview-run");
        var previewBox = document.getElementById("preview-result");
        var previewUrl = form.getAttribute("data-preview-url");

        if (previewBtn) {
            previewBtn.addEventListener("click", function () {
                previewBox.hidden = false;
                previewBox.innerHTML = '<p class="form-help">' + t("loading", "Loading…") + "</p>";
                previewBtn.disabled = true;

                postJson(previewUrl)
                    .then(function (res) { previewBox.innerHTML = renderPreview(res); })
                    .catch(function (err) {
                        previewBox.innerHTML = '<div class="form-summary">' + escapeHtml(String(err)) + "</div>";
                    })
                    .finally(function () { previewBtn.disabled = false; });
            });
        }

        function renderPreview(res) {
            if (!res.ok) {
                return '<div class="form-summary">' + escapeHtml(res.error || "") + "</div>";
            }
            if (!res.total) {
                return '<p class="form-help">' + t("no-match", "No items match the current rules.") +
                    " · " + fill(t("scanned", "{0} scanned"), res.scanned) + "</p>";
            }

            var notes = [fill(t("scanned", "{0} scanned"), res.scanned)];
            if (res.items.length < res.total) {
                notes.push(fill(t("showing", "showing the first {0}"), res.items.length));
            }
            if (res.truncated) {
                notes.push(fill(t("more", "scan stopped at {0} — there may be more"), res.scanned));
            }

            var head = '<p class="preview__summary"><strong>' +
                fill(t("matches", "{0} match(es)"), res.total) + "</strong> · " +
                escapeHtml(notes.join(" · ")) + "</p>";

            // Column meaning differs per source, so the header follows the chosen type.
            var mail = currentType() === "0" || currentType() === "1";
            var columns = mail
                ? [t("col-subject", "Subject"), t("col-from", "From"), t("col-date", "Date"), t("col-attachments", "Attachments")]
                : [t("col-name", "Name"), t("col-path", "Path"), t("col-date", "Date"), t("col-size", "Size")];

            var thead = "<thead><tr>" + columns.map(function (c) {
                return "<th>" + escapeHtml(c) + "</th>";
            }).join("") + "</tr></thead>";

            var rows = res.items.map(function (item) {
                return "<tr><td>" + escapeHtml(item.title) + "</td>" +
                    "<td>" + escapeHtml(item.detail || "—") + "</td>" +
                    "<td>" + escapeHtml(item.date || "—") + "</td>" +
                    "<td>" + escapeHtml(item.extra || "—") + "</td></tr>";
            }).join("");

            return head + '<div class="data-table-wrap"><table class="data-table">' + thead + "<tbody>" + rows + "</tbody></table></div>";
        }
    });
})();
