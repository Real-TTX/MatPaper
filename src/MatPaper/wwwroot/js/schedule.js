// Friendly schedule builder for the task editors. Reads/writes a 5-field cron
// string in a hidden input; times are wall-clock in the app time zone.
(function () {
    "use strict";

    var DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    function pad(n) { return (n < 10 ? "0" : "") + n; }
    function isNum(s) { return /^\d+$/.test(s); }

    function parseCron(cron) {
        cron = (cron || "").trim();
        if (!cron) { return { mode: "manual" }; }
        var f = cron.split(/\s+/);
        if (f.length !== 5) { return { mode: "custom", cron: cron }; }
        var min = f[0], hour = f[1], dom = f[2], mon = f[3], dow = f[4];
        if (mon !== "*") { return { mode: "custom", cron: cron }; }

        if (isNum(min) && hour === "*" && dom === "*" && dow === "*") {
            return { mode: "hourly", minute: +min };
        }
        if (isNum(min) && isNum(hour) && dom === "*" && dow === "*") {
            return { mode: "daily", hour: +hour, minute: +min };
        }
        if (isNum(min) && isNum(hour) && dom === "*" && isNum(dow)) {
            return { mode: "weekly", hour: +hour, minute: +min, dow: +dow };
        }
        if (isNum(min) && isNum(hour) && isNum(dom) && dow === "*") {
            return { mode: "monthly", hour: +hour, minute: +min, dom: +dom };
        }
        return { mode: "custom", cron: cron };
    }

    function init(root) {
        var value = root.querySelector(".mp-schedule__value");
        var mode = root.querySelector(".mp-schedule__mode");
        var minute = root.querySelector(".mp-schedule__minute");
        var time = root.querySelector(".mp-schedule__time");
        var weekday = root.querySelector(".mp-schedule__weekday");
        var dom = root.querySelector(".mp-schedule__dom");
        var cron = root.querySelector(".mp-schedule__cron");
        var summary = root.querySelector(".mp-schedule__summary");
        var parts = Array.prototype.slice.call(root.querySelectorAll(".mp-schedule__part"));

        // Seed controls from the stored expression.
        var p = parseCron(root.getAttribute("data-cron"));
        mode.value = p.mode;
        if (p.mode === "hourly") { minute.value = p.minute; }
        if (p.mode === "daily" || p.mode === "weekly" || p.mode === "monthly") {
            time.value = pad(p.hour) + ":" + pad(p.minute);
        }
        if (p.mode === "weekly") { weekday.value = String(p.dow); }
        if (p.mode === "monthly") { dom.value = p.dom; }
        if (p.mode === "custom") { cron.value = p.cron || ""; }

        function timeParts() {
            var t = (time.value || "03:00").split(":");
            return { h: parseInt(t[0], 10) || 0, m: parseInt(t[1], 10) || 0 };
        }

        function build() {
            switch (mode.value) {
                case "manual": return "";
                case "hourly": return (parseInt(minute.value, 10) || 0) + " * * * *";
                case "daily": { var d = timeParts(); return d.m + " " + d.h + " * * *"; }
                case "weekly": { var w = timeParts(); return w.m + " " + w.h + " * * " + (weekday.value || "1"); }
                case "monthly": { var mo = timeParts(); return mo.m + " " + mo.h + " " + (parseInt(dom.value, 10) || 1) + " * *"; }
                case "custom": return (cron.value || "").trim();
            }
            return "";
        }

        function describe(expr) {
            if (mode.value === "manual" || !expr) { return "Runs only when started manually."; }
            // Times are wall-clock in the app time zone; name it once so nobody guesses.
            var zone = root.getAttribute("data-zone");
            var suffix = zone ? " (" + zone + ")" : "";
            var t = timeParts();
            switch (mode.value) {
                case "hourly": return "Every hour at minute " + (parseInt(minute.value, 10) || 0) + ".";
                case "daily": return "Every day at " + pad(t.h) + ":" + pad(t.m) + suffix + ".";
                case "weekly": return "Every " + DAYS[(parseInt(weekday.value, 10) || 0) % 7] + " at " + pad(t.h) + ":" + pad(t.m) + suffix + ".";
                case "monthly": return "On day " + (parseInt(dom.value, 10) || 1) + " of each month at " + pad(t.h) + ":" + pad(t.m) + suffix + ".";
                case "custom": return "Cron: " + expr;
            }
            return "";
        }

        function refresh() {
            parts.forEach(function (part) {
                var modes = (part.getAttribute("data-modes") || "").split(/\s+/);
                part.style.display = modes.indexOf(mode.value) >= 0 ? "" : "none";
            });
            var expr = build();
            value.value = expr;
            summary.textContent = describe(expr);
        }

        root.addEventListener("change", refresh);
        root.addEventListener("input", refresh);
        refresh();
    }

    document.querySelectorAll(".mp-schedule").forEach(init);
})();
