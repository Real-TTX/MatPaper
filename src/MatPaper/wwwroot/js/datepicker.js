// Date fields: every <input type="date"> becomes a field that looks like the other pickers and opens a calendar
// in a dialog (a full-screen one on a phone). The native input stays in the form - hidden, holding yyyy-MM-dd -
// so server code, validation and scripts that read it do not change. A pick sets it and fires input/change.
//
//   MatPaperDate.enhance(root)   enhance the date inputs inside root (done for the whole page on load)
(function () {
    "use strict";

    var lang = document.documentElement.lang || navigator.language || "en";
    var shown = new Intl.DateTimeFormat(lang, { day: "2-digit", month: "2-digit", year: "numeric" });
    var monthName = new Intl.DateTimeFormat(lang, { month: "long" });
    var weekdayName = new Intl.DateTimeFormat(lang, { weekday: "short" });

    function text(key, fallback) {
        var el = document.getElementById("viewer-i18n");
        return (el && el.getAttribute("data-" + key)) || fallback;
    }

    // 0 = Sunday ... 6 = Saturday; Monday unless the language is one that starts on Sunday.
    function firstDayOfWeek() {
        try {
            var locale = new Intl.Locale(lang);
            var info = locale.weekInfo || (typeof locale.getWeekInfo === "function" ? locale.getWeekInfo() : null);
            if (info && info.firstDay) { return info.firstDay % 7; }
        } catch (e) { /* fall through */ }
        return lang.indexOf("en") === 0 ? 0 : 1;
    }

    function pad(n) { return (n < 10 ? "0" : "") + n; }
    function toIso(d) { return d.getFullYear() + "-" + pad(d.getMonth() + 1) + "-" + pad(d.getDate()); }
    function fromIso(value) {
        var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value || "");
        return m ? new Date(+m[1], +m[2] - 1, +m[3]) : null;
    }
    function sameDay(a, b) { return a && b && a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate(); }

    var ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3" y="5" width="18" height="16" rx="2"/><path d="M16 3v4M8 3v4M3 10h18"/></svg>';
    var PREV = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m15 6-6 6 6 6"/></svg>';
    var NEXT = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m9 6 6 6-6 6"/></svg>';
    var CLOSE = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18"/></svg>';

    var dialog = null;
    var parts = null;
    var state = null; // { input, view: Date (first of the month shown), focus: Date }

    function build() {
        dialog = document.createElement("dialog");
        dialog.className = "mp-date-dialog";
        dialog.innerHTML =
            '<div class="mp-date-dialog__head">' +
            '<button type="button" class="icon-btn" data-nav="-1"></button>' +
            '<div class="mp-date-dialog__title"><select class="form-control" data-role="month"></select><select class="form-control" data-role="year"></select></div>' +
            '<button type="button" class="icon-btn" data-nav="1"></button>' +
            '<button type="button" class="icon-btn mp-date-dialog__close" data-act="close"></button>' +
            '</div>' +
            '<div class="mp-date-dialog__weekdays"></div>' +
            '<div class="mp-date-dialog__grid" role="grid"></div>' +
            '<div class="mp-date-dialog__foot"><button type="button" class="btn btn--secondary" data-act="clear"></button><button type="button" class="btn btn--primary" data-act="today"></button></div>';
        document.body.appendChild(dialog);

        parts = {
            prev: dialog.querySelector('[data-nav="-1"]'),
            next: dialog.querySelector('[data-nav="1"]'),
            month: dialog.querySelector('[data-role="month"]'),
            year: dialog.querySelector('[data-role="year"]'),
            weekdays: dialog.querySelector(".mp-date-dialog__weekdays"),
            grid: dialog.querySelector(".mp-date-dialog__grid"),
            clear: dialog.querySelector('[data-act="clear"]'),
            today: dialog.querySelector('[data-act="today"]'),
            close: dialog.querySelector('[data-act="close"]')
        };
        parts.prev.innerHTML = PREV; parts.prev.setAttribute("aria-label", text("date-prev", "Previous month"));
        parts.next.innerHTML = NEXT; parts.next.setAttribute("aria-label", text("date-next", "Next month"));
        parts.close.innerHTML = CLOSE; parts.close.setAttribute("aria-label", text("close", "Close"));
        parts.clear.textContent = text("date-clear", "Clear");
        parts.today.textContent = text("date-today", "Today");
        parts.month.setAttribute("aria-label", text("date-month", "Month"));
        parts.year.setAttribute("aria-label", text("date-year", "Year"));

        for (var m = 0; m < 12; m++) {
            var option = document.createElement("option");
            option.value = String(m);
            option.textContent = monthName.format(new Date(2021, m, 1));
            parts.month.appendChild(option);
        }

        var first = firstDayOfWeek();
        for (var i = 0; i < 7; i++) {
            var cell = document.createElement("span");
            // 2021-02-07 was a Sunday
            cell.textContent = weekdayName.format(new Date(2021, 1, 7 + ((first + i) % 7)));
            parts.weekdays.appendChild(cell);
        }

        dialog.addEventListener("click", function (event) {
            if (event.target === dialog) { dialog.close(); return; }
            var nav = event.target.closest && event.target.closest("[data-nav]");
            if (nav) { move(+nav.getAttribute("data-nav")); return; }
            var act = event.target.closest && event.target.closest("[data-act]");
            if (!act) { return; }
            var name = act.getAttribute("data-act");
            if (name === "close") { dialog.close(); }
            else if (name === "clear") { commit(null); }
            else if (name === "today") { commit(new Date()); }
        });
        parts.month.addEventListener("change", function () { state.view = new Date(state.view.getFullYear(), +parts.month.value, 1); render(); });
        parts.year.addEventListener("change", function () { state.view = new Date(+parts.year.value, state.view.getMonth(), 1); render(); });
        parts.grid.addEventListener("click", function (event) {
            var day = event.target.closest && event.target.closest("[data-date]");
            if (day && !day.disabled) { commit(fromIso(day.getAttribute("data-date"))); }
        });
        parts.grid.addEventListener("keydown", function (event) {
            var step = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -7, ArrowDown: 7 }[event.key];
            if (!step || !state) { return; }
            event.preventDefault();
            var next = new Date(state.focus.getFullYear(), state.focus.getMonth(), state.focus.getDate() + step);
            state.focus = next;
            state.view = new Date(next.getFullYear(), next.getMonth(), 1);
            render(true);
        });
    }

    function move(delta) {
        state.view = new Date(state.view.getFullYear(), state.view.getMonth() + delta, 1);
        state.focus = new Date(state.view.getFullYear(), state.view.getMonth(), Math.min(state.focus.getDate(), 28));
        render();
    }

    function bounds(input) {
        return { min: fromIso(input.getAttribute("min")), max: fromIso(input.getAttribute("max")) };
    }

    function render(focusDay) {
        var input = state.input;
        var selected = fromIso(input.value);
        var today = new Date();
        var limit = bounds(input);
        var year = state.view.getFullYear();
        var month = state.view.getMonth();

        // the year list covers the shown year, the picked one and a sensible range around today
        var from = Math.min(today.getFullYear() - 100, year), to = Math.max(today.getFullYear() + 20, year);
        if (selected) { from = Math.min(from, selected.getFullYear()); to = Math.max(to, selected.getFullYear()); }
        if (parts.year.options.length === 0 || +parts.year.options[0].value !== from || +parts.year.options[parts.year.options.length - 1].value !== to) {
            parts.year.innerHTML = "";
            for (var y = from; y <= to; y++) {
                var option = document.createElement("option");
                option.value = String(y);
                option.textContent = String(y);
                parts.year.appendChild(option);
            }
        }
        parts.month.value = String(month);
        parts.year.value = String(year);

        var offset = (new Date(year, month, 1).getDay() - firstDayOfWeek() + 7) % 7;
        parts.grid.innerHTML = "";
        for (var i = 0; i < 42; i++) {
            var date = new Date(year, month, 1 - offset + i);
            var button = document.createElement("button");
            button.type = "button";
            button.className = "mp-date-dialog__day";
            button.textContent = String(date.getDate());
            button.setAttribute("data-date", toIso(date));
            button.setAttribute("aria-label", shown.format(date));
            if (date.getMonth() !== month) { button.classList.add("is-other"); }
            if (sameDay(date, today)) { button.classList.add("is-today"); }
            if (sameDay(date, selected)) { button.classList.add("is-selected"); button.setAttribute("aria-selected", "true"); }
            if ((limit.min && date < limit.min) || (limit.max && date > limit.max)) { button.disabled = true; }
            button.tabIndex = sameDay(date, state.focus) ? 0 : -1;
            parts.grid.appendChild(button);
            if (focusDay && sameDay(date, state.focus)) { button.focus(); }
        }
    }

    function open(input) {
        if (!dialog) { build(); }
        var selected = fromIso(input.value) || new Date();
        state = { input: input, view: new Date(selected.getFullYear(), selected.getMonth(), 1), focus: selected };
        render();
        dialog.showModal();
    }

    function commit(date) {
        var input = state.input;
        input.value = date ? toIso(date) : "";
        refresh(input);
        dialog.close();
        input.dispatchEvent(new Event("input", { bubbles: true }));
        input.dispatchEvent(new Event("change", { bubbles: true }));
    }

    function refresh(input) {
        var trigger = input._mpTrigger;
        if (!trigger) { return; }
        var date = fromIso(input.value);
        var label = trigger.querySelector(".mp-date__label");
        label.textContent = date ? shown.format(date) : (input.getAttribute("placeholder") || text("date-placeholder", "Choose a date"));
        trigger.toggleAttribute("data-empty", !date);
    }

    function enhanceInput(input) {
        if (input._mpTrigger || input.type !== "date") { return; }
        var wrap = document.createElement("div");
        wrap.className = "mp-date";
        var trigger = document.createElement("button");
        trigger.type = "button";
        trigger.className = "mp-date__trigger form-control";
        trigger.innerHTML = '<span class="mp-date__label"></span><span class="mp-date__icon">' + ICON + '</span>';
        input.parentNode.insertBefore(wrap, input);
        wrap.appendChild(input);
        wrap.appendChild(trigger);
        input.setAttribute("tabindex", "-1");
        input.hidden = true;
        input._mpTrigger = trigger;
        refresh(input);
        trigger.addEventListener("click", function () { open(input); });
        // a click on the label of the field opens it too
        if (input.id) {
            trigger.id = input.id + "_date";
            var label = document.querySelector('label[for="' + input.id + '"]');
            if (label) {
                label.setAttribute("for", trigger.id);
            }
        }
    }

    function enhance(root) {
        var inputs = (root || document).querySelectorAll('input[type="date"]');
        for (var i = 0; i < inputs.length; i++) { enhanceInput(inputs[i]); }
    }

    window.MatPaperDate = { enhance: enhance };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", function () { enhance(document); });
    } else {
        enhance(document);
    }
})();
