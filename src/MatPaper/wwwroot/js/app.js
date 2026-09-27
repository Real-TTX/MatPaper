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

    // Dependent form fields: a container carrying data-show-when-field /
    // data-show-when-value is shown only while the referenced input's current
    // value matches (comma-separated list allowed; checkboxes report
    // "true"/"false"). Used by the mp-field control for conditional inputs.
    function controllerValue(controls) {
        if (!controls || !controls.length) {
            return "";
        }
        // A radio group reports the checked option, a checkbox "true"/"false".
        if (controls[0].type === "radio") {
            for (var i = 0; i < controls.length; i++) {
                if (controls[i].checked) {
                    return controls[i].value;
                }
            }
            return "";
        }
        if (controls[0].type === "checkbox") {
            return controls[0].checked ? "true" : "false";
        }
        return controls[0].value;
    }

    function wireDependentFields() {
        var rows = document.querySelectorAll("[data-show-when-field]");
        for (var i = 0; i < rows.length; i++) {
            (function (row) {
                var form = row.closest ? row.closest("form") : null;
                var scope = form || document;
                var name = row.getAttribute("data-show-when-field");
                var controls = scope.querySelectorAll('[name="' + name + '"]');
                if (!controls.length) {
                    var byId = document.getElementById(name);
                    if (!byId) {
                        return;
                    }
                    controls = [byId];
                }
                var wanted = (row.getAttribute("data-show-when-value") || "").split(",");
                function evaluate() {
                    row.style.display = wanted.indexOf(controllerValue(controls)) !== -1 ? "" : "none";
                }
                for (var c = 0; c < controls.length; c++) {
                    controls[c].addEventListener("change", evaluate);
                    controls[c].addEventListener("input", evaluate);
                }
                evaluate();
            })(rows[i]);
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
        wireDependentFields();
        wireConfirmActions();
        wireBusyForms();
        wireRowLinks();
        markActiveNav();
        registerServiceWorker();
    }

    // ----- Confirmation dialog ---------------------------------------------

    // Any control carrying data-confirm asks first, in MatPaper's own dialog rather than
    // the browser's. The text lives in an attribute so quotes and apostrophes cannot
    // break out of a JavaScript string and silently disable the question.
    // One pending question at a time, one close listener for the whole page. Attaching a
    // listener per question let unanswered ones pile up and answer each other's promises.
    var confirmPending = null;

    function wireConfirmActions() {
        var dialog = document.getElementById("mp-confirm");
        if (dialog) {
            dialog.addEventListener("close", function () {
                var resolve = confirmPending;
                confirmPending = null;
                var confirmed = dialog.returnValue === "ok";
                dialog.returnValue = "";
                if (resolve) {
                    resolve(confirmed);
                }
            });
        }

        document.addEventListener("click", function (event) {
            var el = event.target && event.target.closest ? event.target.closest("[data-confirm]") : null;
            if (!el || el.getAttribute("data-confirmed") === "1") {
                return;
            }

            var message = el.getAttribute("data-confirm");
            if (!message) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();

            ask(message, el).then(function (confirmed) {
                if (!confirmed) {
                    return;
                }

                // Replay the very same click; the marker keeps us from asking again.
                el.setAttribute("data-confirmed", "1");
                el.click();
                el.removeAttribute("data-confirmed");
            });
        }, true);
    }

    function ask(message, source) {
        var dialog = document.getElementById("mp-confirm");
        if (!dialog || typeof dialog.showModal !== "function") {
            return Promise.resolve(window.confirm(message));
        }

        // A question that is somehow still open counts as declined.
        if (dialog.open) {
            dialog.close("");
        }

        var danger = !!(source && source.closest(".btn--danger, .icon-btn--danger"));
        dialog.querySelector(".confirm__text").textContent = message;
        var ok = dialog.querySelector(".confirm__ok");
        ok.className = "btn confirm__ok " + (danger ? "btn--danger" : "btn--primary");

        return new Promise(function (resolve) {
            confirmPending = resolve;
            dialog.returnValue = "";
            dialog.showModal();
            ok.focus();
        });
    }

    // ----- Clickable rows --------------------------------------------------

    function wireRowLinks() {
        document.addEventListener("click", function (event) {
            var row = event.target && event.target.closest ? event.target.closest("[data-row-href]") : null;
            if (!row) {
                return;
            }

            // Anything interactive inside the row wins, and so does a text selection.
            if (event.target.closest("a, button, input, select, textarea, label, [data-confirm]")) {
                return;
            }
            var selection = window.getSelection();
            if (selection && selection.toString().length > 0) {
                return;
            }

            var href = row.getAttribute("data-row-href");
            if (event.metaKey || event.ctrlKey || event.button === 1) {
                window.open(href, "_blank", "noopener");
            } else {
                window.location.href = href;
            }
        });

        // Keyboard: a row is reachable with Tab and opens with Enter.
        document.addEventListener("keydown", function (event) {
            if (event.key !== "Enter") {
                return;
            }
            var row = event.target && event.target.closest ? event.target.closest("[data-row-href]") : null;
            if (row && event.target === row) {
                window.location.href = row.getAttribute("data-row-href");
            }
        });
    }

    // ----- Busy state ------------------------------------------------------

    // A slow POST used to look like nothing happened, so people clicked again. The
    // button is marked instead of disabled: a disabled submitter is not sent, which
    // would drop the name/value that tells the handler which action was chosen.
    function wireBusyForms() {
        // Bubble phase on purpose: a page script that cancels its own submit (the uploader
        // posts by XHR) runs first, and a cancelled submit is not a request in flight.
        document.addEventListener("submit", function (event) {
            var form = event.target;
            if (!(form instanceof HTMLFormElement) || form.hasAttribute("data-no-busy")) {
                return;
            }

            if (event.defaultPrevented) {
                return;
            }

            if (form.getAttribute("data-busy") === "1") {
                event.preventDefault();
                return;
            }

            form.setAttribute("data-busy", "1");
            var submitter = event.submitter || form.querySelector("[type=submit]");
            if (submitter) {
                submitter.setAttribute("aria-busy", "true");
            }

            // A validation failure or a cancelled navigation must not leave the form
            // stuck, so let go again after a moment.
            window.setTimeout(function () {
                form.removeAttribute("data-busy");
                if (submitter) {
                    submitter.removeAttribute("aria-busy");
                }
            }, 15000);
        });
    }

    // PWA: register the service worker for offline shell + installability.
    function registerServiceWorker() {
        if (!("serviceWorker" in navigator)) {
            return;
        }
        navigator.serviceWorker.register("/service-worker.js").catch(function () {
            /* registration failure is non-fatal */
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
