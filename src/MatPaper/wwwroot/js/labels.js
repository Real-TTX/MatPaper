// MatPaper - the labels page: a sheet's own label count, the fields of an own grid, and keeping the numbers
// up to date once a sheet has been printed (the PDF opens in a new tab, this page stays).
(function () {
    "use strict";

    var form = document.querySelector("[data-labels-form]");
    if (!form) { return; }

    var format = form.querySelector("[data-labels-format]");
    var custom = form.querySelector("[data-labels-custom]");
    var one = form.querySelector("[data-labels-one]");
    var start = form.querySelector("[data-labels-start]");
    var count = form.querySelector("[data-labels-count]");
    var skip = form.querySelector("[data-labels-skip]");
    var border = form.querySelector("[data-labels-border]");
    var nextLabel = document.querySelector("[data-labels-next]");
    var lastLabel = document.querySelector("[data-labels-last]");

    function perPage() {
        var option = format.options[format.selectedIndex];
        var known = parseInt(option.getAttribute("data-per-page"), 10);
        if (known > 0) { return known; }
        // an own grid: its labels per sheet, or one when a label printer feeds them
        if (one && one.checked) { return 1; }
        var columns = parseInt(form.querySelector("[name='Input.Columns']").value, 10) || 1;
        var rows = parseInt(form.querySelector("[name='Input.Rows']").value, 10) || 1;
        return columns * rows;
    }

    function sync() {
        var isCustom = format.value === "custom";
        custom.hidden = !isCustom;
        var gridFields = custom.querySelectorAll(".asn-grid .form-row");
        // a label printer feeds one label per page: the grid fields do not matter then
        Array.prototype.forEach.call(gridFields, function (row, index) {
            var isGrid = index < 2 || index > 3; // columns, rows, gaps
            row.hidden = !!(one && one.checked) && isGrid;
        });
    }

    // choosing a sheet proposes a full sheet
    format.addEventListener("change", function () {
        sync();
        count.value = perPage();
        skip.value = 0;
    });
    if (one) { one.addEventListener("change", function () { sync(); count.value = perPage(); }); }
    ["Input.Columns", "Input.Rows"].forEach(function (name) {
        var field = form.querySelector("[name='" + name + "']");
        if (field) { field.addEventListener("change", function () { if (format.value === "custom") { count.value = perPage(); } }); }
    });
    sync();

    // A printed sheet has used up its numbers: the next sheet starts behind them. A test print does not.
    form.addEventListener("submit", function () {
        if (border && border.checked) { return; }
        var first = parseInt(start.value, 10);
        var total = parseInt(count.value, 10);
        if (!(first > 0) || !(total > 0)) { return; }
        window.setTimeout(function () {
            var next = first + total;
            start.value = next;
            skip.value = 0;
            if (nextLabel) { nextLabel.textContent = next; }
            if (lastLabel) { lastLabel.textContent = next - 1; }
        }, 400);
    });
})();
