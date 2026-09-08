// MatPaper — client shell controller: theme switching + mobile navigation.
(function () {
    "use strict";

    var STORAGE_KEY = "matpaper-theme";
    var VALID_THEMES = ["system", "dark", "bright"];
    var root = document.documentElement;
    var media = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;

    function getStoredTheme() {
        var value = null;
        try {
            value = localStorage.getItem(STORAGE_KEY);
        } catch (e) {
            value = null;
        }
        return VALID_THEMES.indexOf(value) !== -1 ? value : "system";
    }

    function storeTheme(theme) {
        try {
            localStorage.setItem(STORAGE_KEY, theme);
        } catch (e) {
            /* storage unavailable — ignore */
        }
    }

    // Apply the chosen theme by setting (or clearing) the data-theme attribute
    // on <html>. "system" removes the attribute so CSS prefers-color-scheme
    // takes over; "bright" maps to the light palette (no attribute needed for
    // light, but we set it explicitly so it wins over the OS preference).
    function applyTheme(theme) {
        if (theme === "system") {
            root.removeAttribute("data-theme");
        } else if (theme === "dark") {
            root.setAttribute("data-theme", "dark");
        } else {
            root.setAttribute("data-theme", "light");
        }
        updateThemeButtons(theme);
    }

    function updateThemeButtons(theme) {
        var buttons = document.querySelectorAll(".theme-switch__btn");
        for (var i = 0; i < buttons.length; i++) {
            var btn = buttons[i];
            var isActive = btn.getAttribute("data-theme-value") === theme;
            btn.classList.toggle("is-active", isActive);
            btn.setAttribute("aria-pressed", isActive ? "true" : "false");
        }
    }

    function wireThemeButtons() {
        var buttons = document.querySelectorAll(".theme-switch__btn");
        for (var i = 0; i < buttons.length; i++) {
            buttons[i].addEventListener("click", function () {
                var theme = this.getAttribute("data-theme-value");
                if (VALID_THEMES.indexOf(theme) === -1) {
                    return;
                }
                storeTheme(theme);
                applyTheme(theme);
            });
        }
    }

    // When following the system preference there is nothing to toggle on
    // <html> (CSS handles it), but we keep this listener so any future
    // preference-dependent JS re-runs on OS theme changes.
    function wireSystemListener() {
        if (!media) {
            return;
        }
        var handler = function () {
            if (getStoredTheme() === "system") {
                applyTheme("system");
            }
        };
        if (typeof media.addEventListener === "function") {
            media.addEventListener("change", handler);
        } else if (typeof media.addListener === "function") {
            media.addListener(handler);
        }
    }

    function wireSidebarToggle() {
        var shell = document.querySelector(".app-shell");
        var toggle = document.getElementById("sidebar-toggle");
        var backdrop = document.getElementById("sidebar-backdrop");
        if (!shell || !toggle) {
            return;
        }

        function setOpen(open) {
            shell.classList.toggle("is-sidebar-open", open);
            toggle.setAttribute("aria-expanded", open ? "true" : "false");
            if (backdrop) {
                backdrop.hidden = !open;
            }
        }

        toggle.addEventListener("click", function () {
            setOpen(!shell.classList.contains("is-sidebar-open"));
        });

        if (backdrop) {
            backdrop.addEventListener("click", function () {
                setOpen(false);
            });
        }

        document.addEventListener("keydown", function (e) {
            if (e.key === "Escape") {
                setOpen(false);
            }
        });

        // Close after navigating on small screens.
        var navLinks = document.querySelectorAll(".sidebar__nav .nav-item");
        for (var i = 0; i < navLinks.length; i++) {
            navLinks[i].addEventListener("click", function () {
                if (window.matchMedia("(max-width: 768px)").matches) {
                    setOpen(false);
                }
            });
        }
    }

    function markActiveNav() {
        var path = window.location.pathname.replace(/\/+$/, "") || "/";
        var links = document.querySelectorAll(".sidebar__nav .nav-item");
        for (var i = 0; i < links.length; i++) {
            var href = links[i].getAttribute("href").replace(/\/+$/, "") || "/";
            var isActive = href === "/"
                ? path === "/"
                : path === href || path.indexOf(href + "/") === 0;
            links[i].classList.toggle("is-active", isActive);
        }
    }

    // Apply stored theme as early as possible.
    applyTheme(getStoredTheme());

    function init() {
        applyTheme(getStoredTheme());
        wireThemeButtons();
        wireSystemListener();
        wireSidebarToggle();
        markActiveNav();
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
