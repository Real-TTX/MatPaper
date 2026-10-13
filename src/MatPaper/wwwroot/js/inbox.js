// The inbox (Pages/Inbox): what the toolkit does not bring.
//
//  - phones: project, tags and filing sit behind "More fields" (data-inbox-more);
//  - documents that are still being processed (OCR): ask for their state and reload once suggestions are there - unless the person has started
//    editing a row, then a note offers the reload instead;
//  - the selection: the toolkit (data-selectable) ticks, counts and shows the bar; this adds the marks on the cards, "select all N" over all pages
//    and the label of the button that opens the values for the selection.
(function () {
    "use strict";

    var doc = document;

    // ---- More fields (phones)
    doc.addEventListener("click", function (event) {
        var toggle = event.target.closest && event.target.closest("[data-inbox-more]");
        if (!toggle) { return; }
        var item = toggle.closest(".mp-inbox-item");
        if (!item) { return; }
        var open = item.classList.toggle("is-expanded");
        toggle.setAttribute("aria-expanded", open ? "true" : "false");
    });

    // ---- While documents are still being processed
    (function () {
        var pending = Array.prototype.map.call(
            doc.querySelectorAll('.mp-inbox-item[data-ocr="pending"]'),
            function (el) { return el.getAttribute("data-doc-id"); });
        if (!pending.length) { return; }

        var dirty = false;
        doc.addEventListener("input", function () { dirty = true; }, true);
        doc.addEventListener("change", function () { dirty = true; }, true);

        function tick() {
            var qs = pending.map(function (id) { return "ids=" + encodeURIComponent(id); }).join("&");
            fetch("/Inbox?handler=Status&" + qs, { credentials: "same-origin", headers: { "Accept": "application/json" } })
                .then(function (r) { return r.ok ? r.json() : []; })
                .then(function (rows) {
                    // A row the endpoint no longer returns is gone (deleted, reassigned): treat that as done so the poll cannot run forever.
                    var seen = {};
                    rows.forEach(function (r) { seen[r.id] = r.state; });
                    var done = pending.some(function (id) { return seen[id] === undefined || seen[id] !== "pending"; });
                    if (!done) { setTimeout(tick, 3000); return; }
                    if (!dirty) { location.reload(); return; }
                    var note = doc.getElementById("inbox-refresh");
                    if (note) { note.hidden = false; }
                })
                .catch(function () { setTimeout(tick, 5000); });
        }

        setTimeout(tick, 3000);
    })();

    // ---- The selection
    (function () {
        var box = doc.querySelector("[data-selectable]");
        var bar = box && box.querySelector("[data-bulk-bar]");
        var matchingButton = doc.getElementById("inbox-select-matching");
        var matchingFlag = doc.getElementById("inbox-all-matching");
        if (!bar || !matchingButton || !matchingFlag) { return; }

        var count = bar.querySelector("[data-bulk-count]");
        var total = parseInt(matchingButton.getAttribute("data-total"), 10) || 0;
        var matching = false;

        function rows() { return Array.prototype.slice.call(box.querySelectorAll("input[data-select-row]")); }

        function setMatching(on) {
            matching = on;
            matchingFlag.value = on ? "true" : "false";
        }

        // runs after the toolkit has ticked and counted
        function refresh() {
            var all = rows();
            var n = all.filter(function (r) { return r.checked; }).length;
            if (n < all.length) { setMatching(false); }
            if (matching && count) { count.textContent = String(total); }
            // the whole page is ticked and there is more behind it
            matchingButton.hidden = !(all.length > 0 && n === all.length && total > all.length);
            matchingButton.textContent = matching
                ? (matchingButton.getAttribute("data-t-page") || "Only this page")
                : (matchingButton.getAttribute("data-t-all") || "Select all {0}").replace("{0}", String(total));
            all.forEach(function (r) {
                var item = r.closest(".mp-inbox-item");
                if (item) { item.classList.toggle("is-selected", r.checked); }
            });
        }

        doc.addEventListener("change", function (event) {
            var t = event.target;
            if (t && t.matches && (t.matches("input[data-select-row]") || t.matches("input[data-select-all]"))) {
                setMatching(false);
                // the toolkit's own handler is registered first (it was loaded first), so its count is there when this runs
                refresh();
            }
        });
        matchingButton.addEventListener("click", function () { setMatching(!matching); refresh(); });

        // ---- the values to set for the selection: the button says how many are set
        var dialog = doc.getElementById("inbox-bulk-dialog");
        var openButton = doc.getElementById("inbox-bulk-open");
        if (dialog && openButton) {
            dialog.addEventListener("close", function () {
                var set = 0;
                dialog.querySelectorAll(".mat-picker__input").forEach(function (input) { if (input.value !== "") { set++; } });
                var label = openButton.getAttribute("data-t-label") || "";
                var text = openButton.querySelector("span");
                if (text) { text.textContent = set > 0 ? label.replace(" …", "") + " (" + set + ")" : label; }
            });
        }

        refresh();
    })();
})();
