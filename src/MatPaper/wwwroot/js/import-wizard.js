// Import-task wizard: step navigation, the choice between a single rule and a group, and the dry-run preview. Everything lives in one
// form, so Save/Test/Run keep working and the server sees the complete task no matter which step is open. The markup is the toolkit's
// (cards, tabs as the step list, choice cards); the classes mp-wizard__* are the wizard's own.
(function () {
    "use strict";

    document.addEventListener("DOMContentLoaded", function () {
        var form = document.getElementById("import-wizard");
        if (!form) { return; }

        var steps = Array.prototype.slice.call(form.querySelectorAll(".mp-wizard__step"));
        var panels = Array.prototype.slice.call(form.querySelectorAll(".mp-wizard__panel"));
        var backBtn = document.getElementById("wizard-back");
        var nextBtn = document.getElementById("wizard-next");
        var typeInputs = form.querySelectorAll('[name="Input.Type"]');
        var stepField = document.getElementById("wizard-active-step");
        // A postback (Test connection, validation error) re-renders the page; the step
        // the user was on travels in a hidden field so they land back there.
        var rawStep = parseInt(stepField && stepField.value, 10);
        var current = isNaN(rawStep) ? 0 : rawStep;

        // Editing an existing rule can show the sections as tabs (a jump list instead of Back/Next); creating one keeps the step-by-step wizard.
        var sectionsMode = form.getAttribute("data-mode") === "sections";

        function t(name, fallback) { return form.getAttribute("data-t-" + name) || fallback; }
        function fill(template, a) { return String(template).replace("{0}", a); }

        function currentType() {
            for (var i = 0; i < typeInputs.length; i++) {
                if (typeInputs[i].checked) { return typeInputs[i].value; }
            }
            return "2";
        }

        // A group that fixes the source (connection, folder) takes those steps away from its rules.
        var groupSelect = form.querySelector('[name="Input.GroupId"]');
        var groupSources = {};
        try { groupSources = JSON.parse(form.getAttribute("data-group-sources") || "{}"); } catch (e) { groupSources = {}; }
        function sourceFromGroup() {
            return !!(groupSelect && groupSelect.value && groupSources[groupSelect.value] !== undefined);
        }
        if (groupSelect) { groupSelect.addEventListener("change", function () { render(); }); }

        // A panel may declare data-for-type; without it the step applies to every type.
        function appliesToType(panel, type) {
            if (panel.hasAttribute("data-hide-with-source") && sourceFromGroup()) { return false; }
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
                if (step === current) { chip.setAttribute("aria-current", "step"); } else { chip.removeAttribute("aria-current"); }
                var num = chip.querySelector(".mp-wizard__num");
                if (num) { num.textContent = index === -1 ? "" : String(index + 1); }
            });

            if (stepField) { stepField.value = String(current); }

            if (sectionsMode) { return; }
            var pos = visible.indexOf(current);
            if (backBtn) { backBtn.disabled = pos <= 0; }
            if (nextBtn) { nextBtn.disabled = pos === visible.length - 1; }
        }

        function panelOf(step) {
            for (var i = 0; i < panels.length; i++) {
                if (parseInt(panels[i].getAttribute("data-step"), 10) === step) { return panels[i]; }
            }
            return null;
        }

        function wireSections() {
            // A section with a problem opens first, so the message is not hidden behind another one.
            var withError = panels.filter(function (p) { return p.querySelector(".mat-field.is-invalid, .mat-error:not(:empty)"); })[0];
            var hash = parseInt((location.hash || "").replace("#tab", ""), 10);
            if (withError) { current = parseInt(withError.getAttribute("data-step"), 10); }
            else if (hash && panelOf(hash)) { current = hash; }
            render();
        }

        function go(step) {
            current = step;
            render();
            if (sectionsMode) {
                if (history.replaceState) { history.replaceState(null, "", "#tab" + step); }
                return;
            }
            form.scrollIntoView({ block: "start", behavior: "smooth" });
        }

        function step(delta) {
            var visible = visibleSteps();
            var pos = visible.indexOf(current) + delta;
            if (pos >= 0 && pos < visible.length) { go(visible[pos]); }
        }

        if (backBtn) { backBtn.addEventListener("click", function () { step(-1); }); }
        if (nextBtn) { nextBtn.addEventListener("click", function () { step(1); }); }
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
            ["move"].forEach(function (value) {
                var option = postAction.querySelector('option[value="' + value + '"]');
                if (option) { option.hidden = isPop3; option.disabled = isPop3; }
            });
            if (isPop3 && postAction.value === "move") {
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

        // ---- Single rule or group: a group that already has a rule decides the kind of source, so the type cards are not offered then.
        (function scope() {
            var tiles = document.getElementById("scope-tiles");
            var pick = document.getElementById("group-pick");
            var note = document.getElementById("group-target-note");
            if (!tiles || !pick || !groupSelect) { return; }
            var typeMap = {};
            try { typeMap = JSON.parse(pick.getAttribute("data-group-types") || "{}"); } catch (e) { typeMap = {}; }

            function syncType() {
                var fixed = groupSelect.value && typeMap[groupSelect.value] !== undefined ? String(typeMap[groupSelect.value]) : null;
                var typeBox = document.getElementById("type-pick");
                var typeNote = document.getElementById("type-fixed-note");
                if (typeBox) { typeBox.hidden = fixed !== null; }
                if (typeNote) { typeNote.hidden = fixed === null; }
                if (fixed !== null) {
                    var radio = document.querySelector('input[name="Input.Type"][value="' + fixed + '"]');
                    if (radio && !radio.checked) { radio.checked = true; radio.dispatchEvent(new Event("change", { bubbles: true })); }
                }
            }

            function apply(value) {
                var group = value === "group";
                pick.hidden = !group;
                if (!group) { groupSelect.value = ""; }
                else if (!groupSelect.value && groupSelect.options.length > 1) { groupSelect.selectedIndex = 1; }
                groupSelect.dispatchEvent(new Event("change", { bubbles: true }));
                if (note) { note.hidden = !group; }
                syncType();
            }

            tiles.addEventListener("change", function (e) { if (e.target.name === "rule-scope") { apply(e.target.value); } });
            groupSelect.addEventListener("change", function () { if (note) { note.hidden = !groupSelect.value; } syncType(); });
            syncType();
        })();

        syncPostAction();
        render();
        if (sectionsMode) { wireSections(); }
        form.classList.remove("is-booting");

        // The source picker dialog lives in browse-dialog.js (shared with the storage-location
        // editor); the preview below reuses its request helper.
        function postJson(url, extra) { return window.MatPaperBrowse.postJson(form, url, extra); }
        var escapeHtml = window.MatPaperBrowse.escapeHtml;

        // ---- Preview --------------------------------------------------------
        var previewBtn = document.getElementById("preview-run");
        var previewBox = document.getElementById("preview-result");
        var previewUrl = form.getAttribute("data-preview-url");

        function alertBox(message) {
            return '<div class="mat-alert mat-alert--danger" role="alert"><svg class="mat-icon" aria-hidden="true" focusable="false"><use href="#mat-i-x-circle"></use></svg><div class="mat-alert__body">' +
                escapeHtml(message) + "</div></div>";
        }

        if (previewBtn) {
            previewBtn.addEventListener("click", function () {
                previewBox.hidden = false;
                previewBox.innerHTML = '<p class="mat-muted">' + escapeHtml(t("loading", "Loading…")) + "</p>";
                previewBtn.disabled = true;

                postJson(previewUrl)
                    .then(function (res) { previewBox.innerHTML = renderPreview(res); wireMore(); })
                    .catch(function (err) {
                        previewBox.innerHTML = alertBox(String(err && err.message ? err.message : err));
                    })
                    .finally(function () { previewBtn.disabled = false; });
            });
        }

        // "Show more" reveals the next 25 rows of an already loaded preview.
        function wireMore() {
            var button = previewBox.querySelector("[data-preview-more]");
            if (!button) { return; }
            button.addEventListener("click", function () {
                var hiddenRows = previewBox.querySelectorAll("tr[data-extra][hidden]");
                for (var i = 0; i < hiddenRows.length && i < 25; i++) { hiddenRows[i].hidden = false; }
                var left = previewBox.querySelectorAll("tr[data-extra][hidden]").length;
                if (left === 0) { button.remove(); } else { button.querySelector("span").textContent = t("show-more", "Show more") + " (" + left + ")"; }
            });
        }

        function renderPreview(res) {
            if (!res.ok) {
                return alertBox(res.error || "");
            }
            if (!res.total) {
                return '<p class="mat-muted">' + escapeHtml(t("no-match", "No items match the current rules.")) +
                    " · " + escapeHtml(fill(t("scanned", "{0} scanned"), res.scanned)) + "</p>";
            }

            var notes = [fill(t("scanned", "{0} scanned"), res.scanned)];
            if (res.items.length < res.total) {
                notes.push(fill(t("showing", "showing the first {0}"), res.items.length));
            }
            if (res.truncated) {
                notes.push(fill(t("more", "scan stopped at {0} — there may be more"), res.scanned));
            }

            var head = "<p><strong>" + escapeHtml(fill(t("matches", "{0} match(es)"), res.total)) + "</strong> · " +
                '<span class="mat-muted">' + escapeHtml(notes.join(" · ")) + "</span></p>";

            // Column meaning differs per source, so the header follows the chosen type.
            var mail = currentType() === "0" || currentType() === "1";
            var columns = mail
                ? [t("col-subject", "Subject"), t("col-from", "From"), t("col-date", "Date"), t("col-attachments", "Attachments")]
                : [t("col-name", "Name"), t("col-path", "Path"), t("col-date", "Date"), t("col-size", "Size")];

            var thead = "<thead><tr>" + columns.map(function (c) {
                return '<th scope="col">' + escapeHtml(c) + "</th>";
            }).join("") + "</tr></thead>";

            var PAGE = 25;
            var rows = res.items.map(function (item, index) {
                return "<tr" + (index >= PAGE ? ' hidden data-extra="1"' : "") + '><td data-label="' + escapeHtml(columns[0]) + '">' + escapeHtml(item.title) + "</td>" +
                    '<td data-label="' + escapeHtml(columns[1]) + '">' + escapeHtml(item.detail || "—") + "</td>" +
                    '<td data-label="' + escapeHtml(columns[2]) + '">' + escapeHtml(item.date || "—") + "</td>" +
                    '<td data-label="' + escapeHtml(columns[3]) + '">' + escapeHtml(item.extra || "—") + "</td></tr>";
            }).join("");

            var more = res.items.length > PAGE
                ? '<div><button type="button" class="mat-btn mat-btn--secondary" data-preview-more><span>' + escapeHtml(t("show-more", "Show more")) +
                  " (" + (res.items.length - PAGE) + ")</span></button></div>"
                : "";

            return head + '<div class="mat-table-wrap"><table class="mat-table mat-table--compact">' + thead + "<tbody>" + rows + "</tbody></table></div>" + more;
        }
    });
})();

// Attachment presets: one click sets the extension list; the button that matches the field is pressed.
(function () {
    "use strict";
    var field = document.getElementById("Input_AttachmentExtensions");
    var chips = document.querySelectorAll("[data-ext-preset]");
    if (!field || chips.length === 0) { return; }

    function normalized(value) {
        return value.split(",").map(function (p) { return p.trim().toLowerCase(); }).filter(Boolean).sort().join(",");
    }

    function mark() {
        var current = normalized(field.value);
        chips.forEach(function (chip) {
            var on = normalized(chip.getAttribute("data-ext-preset")) === current;
            chip.classList.toggle("is-active", on);
            chip.setAttribute("aria-pressed", on ? "true" : "false");
        });
    }

    chips.forEach(function (chip) {
        chip.addEventListener("click", function () {
            field.value = chip.getAttribute("data-ext-preset");
            field.dispatchEvent(new Event("input", { bubbles: true }));
            mark();
        });
    });
    field.addEventListener("input", mark);
    mark();
})();
