// Plain tabs: [data-tabs] holds a .tabs__bar of .tabs__tab[data-tab] and panels [data-tab-panel]
// (the panels may sit outside a form that lies inside the container). The tab is kept in the URL hash.
(function () {
    "use strict";
    document.addEventListener("DOMContentLoaded", function () {
        document.querySelectorAll("[data-tabs]").forEach(function (root) {
            var tabs = Array.prototype.slice.call(root.querySelectorAll(".tabs__tab"));
            var panels = Array.prototype.slice.call(root.querySelectorAll("[data-tab-panel]"));
            function show(name) {
                tabs.forEach(function (t) {
                    var on = t.getAttribute("data-tab") === name;
                    t.classList.toggle("is-active", on);
                    t.setAttribute("aria-selected", on ? "true" : "false");
                });
                panels.forEach(function (p) { p.hidden = p.getAttribute("data-tab-panel") !== name; });
                root.setAttribute("data-active-tab", name);
            }
            tabs.forEach(function (t) {
                t.addEventListener("click", function () {
                    var name = t.getAttribute("data-tab");
                    show(name);
                    if (history.replaceState) { history.replaceState(null, "", "#" + name); }
                });
            });
            // After a postback on the same page (e.g. "Test connection") the tab the user was on opens again.
            var key = "mp-tab:" + location.pathname;
            var remembered = null;
            try {
                var saved = JSON.parse(sessionStorage.getItem(key) || "null");
                if (saved && Date.now() - saved.at < 15000) { remembered = saved.tab; }
                sessionStorage.removeItem(key);
            } catch (err) { /* storage unavailable */ }
            root.addEventListener("submit", function () {
                try { sessionStorage.setItem(key, JSON.stringify({ tab: root.getAttribute("data-active-tab"), at: Date.now() })); } catch (err) { }
            }, true);

            var wanted = (location.hash || "").slice(1) || remembered || "";
            var withError = panels.filter(function (p) { return p.querySelector(".field-error:not(:empty), .input-validation-error, .field-validation-error"); })[0];
            var valid = tabs.some(function (t) { return t.getAttribute("data-tab") === wanted; });
            show(withError ? withError.getAttribute("data-tab-panel") : valid ? wanted : tabs[0].getAttribute("data-tab"));
            root.classList.remove("is-booting");
        });
    });
})();
