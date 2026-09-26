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
        var current = 1;

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

        for (var i = 0; i < typeInputs.length; i++) {
            typeInputs[i].addEventListener("change", function () {
                syncPort();
                syncPostAction();
                render();
            });
        }
        if (sslField) { sslField.addEventListener("change", syncPort); }

        syncPostAction();
        render();

        // ---- Source picker -------------------------------------------------
        var dialog = document.getElementById("browse-dialog");
        var browseUrl = form.getAttribute("data-browse-url");
        var state = { scope: null, targetId: null, path: "", stack: [] };

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

            var data = new FormData(form);
            data.append("scope", state.scope);
            data.append("path", state.path);

            fetch(browseUrl, {
                method: "POST",
                credentials: "same-origin",
                headers: { "RequestVerificationToken": token() },
                body: data
            })
                .then(function (r) { return r.json(); })
                .then(function (res) {
                    if (!res.ok) {
                        setList('<li class="browse-dialog__error">' + escapeHtml(res.error || "") + "</li>");
                        return;
                    }
                    if (!res.entries.length) {
                        setList('<li class="browse-dialog__empty">' + t("empty", "Nothing found.") + "</li>");
                        return;
                    }
                    setList(res.entries.map(renderEntry).join(""));
                })
                .catch(function (err) {
                    setList('<li class="browse-dialog__error">' + escapeHtml(String(err)) + "</li>");
                });
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

        dialog.addEventListener("click", function (ev) {
            var pick = ev.target.closest(".browse-dialog__pick");
            if (pick) { choose(pick.getAttribute("data-path")); return; }

            var open = ev.target.closest(".browse-dialog__open");
            if (open) { state.stack.push(state.path); load(open.getAttribute("data-path")); return; }

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

                fetch(previewUrl, {
                    method: "POST",
                    credentials: "same-origin",
                    headers: { "RequestVerificationToken": token() },
                    body: new FormData(form)
                })
                    .then(function (r) { return r.json(); })
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

            var head = '<p class="preview__summary"><strong>' + fill(t("matches", "{0} match(es)"), res.total) +
                "</strong> · " + fill(t("scanned", "{0} scanned"), res.scanned) + "</p>";

            var rows = res.items.map(function (item) {
                return "<tr><td>" + escapeHtml(item.title) + "</td>" +
                    "<td>" + escapeHtml(item.detail || "—") + "</td>" +
                    "<td>" + escapeHtml(item.date || "—") + "</td>" +
                    "<td>" + escapeHtml(item.extra || "—") + "</td></tr>";
            }).join("");

            return head + '<div class="data-table-wrap"><table class="data-table"><tbody>' + rows + "</tbody></table></div>";
        }
    });
})();
