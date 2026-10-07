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
            var wanted = (location.hash || "").slice(1);
            var withError = panels.filter(function (p) { return p.querySelector(".field-error:not(:empty), .input-validation-error, .field-validation-error"); })[0];
            var valid = tabs.some(function (t) { return t.getAttribute("data-tab") === wanted; });
            show(withError ? withError.getAttribute("data-tab-panel") : valid ? wanted : tabs[0].getAttribute("data-tab"));
            root.classList.remove("is-booting");
        });
    });
})();
