// Import-task wizard: step navigation and the dry-run
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
        var rawStep = parseInt(stepField && stepField.value, 10);
        var current = isNaN(rawStep) ? 0 : rawStep;

        // Editing an existing rule shows every section on one page (a jump list instead of Back/Next);
        // creating one keeps the step-by-step wizard.
        var sectionsMode = form.getAttribute("data-mode") === "sections";
        var phone = window.matchMedia("(max-width: 800px)");

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
            if (sectionsMode) { renderSections(visible); return; }
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
            if (backBtn) { backBtn.disabled = pos <= 0; }
            if (nextBtn) { nextBtn.disabled = pos === visible.length - 1; }
        }

        // ---- tabs mode (editing): the sections are tabs, one visible at a time
        function panelOf(step) {
            for (var i = 0; i < panels.length; i++) {
                if (parseInt(panels[i].getAttribute("data-step"), 10) === step) { return panels[i]; }
            }
            return null;
        }

        function renderSections(visible) {
            if (visible.indexOf(current) === -1) { current = visible[0]; }
            panels.forEach(function (panel) {
                var step = parseInt(panel.getAttribute("data-step"), 10);
                panel.hidden = step !== current;
            });
            steps.forEach(function (chip) {
                var step = parseInt(chip.getAttribute("data-goto"), 10);
                chip.hidden = visible.indexOf(step) === -1;
                chip.classList.toggle("is-active", step === current);
                chip.setAttribute("role", "tab");
                chip.setAttribute("aria-selected", step === current ? "true" : "false");
            });
            if (stepField) { stepField.value = String(current); }
        }

        function wireSections() {
            panels.forEach(function (panel) { panel.classList.add("is-section"); });
            // A tab with a problem opens first, so the message is not hidden behind another tab.
            var withError = panels.filter(function (p) { return p.querySelector(".field-error:not(:empty), .input-validation-error, .field-validation-error"); })[0];
            var hash = parseInt((location.hash || "").replace("#tab", ""), 10);
            if (withError) { current = parseInt(withError.getAttribute("data-step"), 10); }
            else if (hash && panelOf(hash)) { current = hash; }
            renderSections(visibleSteps());
        }

        function go(step) {
            current = step;
            if (sectionsMode) {
                renderSections(visibleSteps());
                if (history.replaceState) { history.replaceState(null, "", "#tab" + step); }
                return;
            }
            render();
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

        if (previewBtn) {
            previewBtn.addEventListener("click", function () {
                previewBox.hidden = false;
                previewBox.innerHTML = '<p class="form-help">' + t("loading", "Loading…") + "</p>";
                previewBtn.disabled = true;

                postJson(previewUrl)
                    .then(function (res) { previewBox.innerHTML = renderPreview(res); wireMore(); })
                    .catch(function (err) {
                        previewBox.innerHTML = '<div class="form-summary">' + escapeHtml(String(err)) + "</div>";
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
                if (left === 0) { button.remove(); } else { button.textContent = t("show-more", "Show more") + " (" + left + ")"; }
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

            var PAGE = 25;
            var rows = res.items.map(function (item, index) {
                return "<tr" + (index >= PAGE ? ' hidden data-extra="1"' : "") + "><td>" + escapeHtml(item.title) + "</td>" +
                    "<td>" + escapeHtml(item.detail || "—") + "</td>" +
                    "<td>" + escapeHtml(item.date || "—") + "</td>" +
                    "<td>" + escapeHtml(item.extra || "—") + "</td></tr>";
            }).join("");

            var more = res.items.length > PAGE
                ? '<p><button type="button" class="btn btn--secondary" data-preview-more>' + escapeHtml(t("show-more", "Show more")) +
                  " (" + (res.items.length - PAGE) + ")</button></p>"
                : "";

            return head + '<div class="data-table-wrap"><table class="data-table">' + thead + "<tbody>" + rows + "</tbody></table></div>" + more;
        }
    });
})();

// Attachment presets: one click sets the extension list; the chip that matches the field is marked.
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
            chip.classList.toggle("is-active", normalized(chip.getAttribute("data-ext-preset")) === current);
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
