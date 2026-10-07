// MatPaper — client shell controller: theme (mode/scheme/accent) + mobile navigation.
(function () {
    "use strict";

    // ---- Theme --------------------------------------------------------------
    // The server renders data-theme-mode, data-scheme and data-accent on <html>; the inline
    // head script (_ThemeHead) has already turned "system" into data-mode before the first
    // paint. Here: the sidebar mode switch, saving it to the account, following OS changes,
    // and window.MatPaperTheme for the live preview on the appearance page.
    var STORAGE_KEY = "matpaper-theme";
    var MODES = ["system", "light", "dark"];
    var root = document.documentElement;
    var media = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;

    function normalizeMode(value) {
        if (value === "bright") {
            return "light"; // stored by older versions
        }
        return MODES.indexOf(value) !== -1 ? value : null;
    }

    function getStoredMode() {
        try {
            return normalizeMode(localStorage.getItem(STORAGE_KEY));
        } catch (e) {
            return null;
        }
    }

    function storeMode(mode) {
        try {
            localStorage.setItem(STORAGE_KEY, mode);
        } catch (e) {
            /* storage unavailable — ignore */
        }
    }

    function currentMode() {
        return normalizeMode(root.getAttribute("data-theme-mode")) || "system";
    }

    function effectiveMode(mode) {
        return mode === "dark" || (mode === "system" && media && media.matches) ? "dark" : "light";
    }

    // The browser bar (mobile, installed PWA) follows the active accent.
    function syncThemeColor() {
        var meta = document.querySelector('meta[name="theme-color"]');
        // The colour of the header: on a phone it also shows behind the status bar.
        var bar = getComputedStyle(root).getPropertyValue("--color-surface").trim();
        if (meta && bar) {
            meta.setAttribute("content", bar);
        }
    }

    function updateThemeButtons(mode) {
        var buttons = document.querySelectorAll(".theme-switch__btn");
        for (var i = 0; i < buttons.length; i++) {
            var btn = buttons[i];
            var isActive = btn.getAttribute("data-theme-value") === mode;
            btn.classList.toggle("is-active", isActive);
            btn.setAttribute("aria-pressed", isActive ? "true" : "false");
        }
    }

    // Applies any of { mode, scheme, accent } to <html> right away (does not save).
    function applyTheme(parts) {
        if (parts.mode) {
            root.setAttribute("data-theme-mode", parts.mode);
            root.setAttribute("data-mode", effectiveMode(parts.mode));
            updateThemeButtons(parts.mode);
        }
        if (parts.scheme) {
            root.setAttribute("data-scheme", parts.scheme);
        }
        if (parts.accent) {
            root.setAttribute("data-accent", parts.accent);
        }
        syncThemeColor();
    }

    window.MatPaperTheme = {
        apply: applyTheme,
        current: function () {
            return {
                mode: currentMode(),
                scheme: root.getAttribute("data-scheme"),
                accent: root.getAttribute("data-accent")
            };
        }
    };

    function antiforgeryToken() {
        var el = document.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : "";
    }

    // Signed-in pages carry data-theme-save: the mode then follows the account to every device.
    function saveMode(mode) {
        var url = root.getAttribute("data-theme-save");
        if (!url || typeof fetch !== "function") {
            return;
        }
        var data = new FormData();
        data.append("mode", mode);
        data.append("__RequestVerificationToken", antiforgeryToken());
        fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: { "RequestVerificationToken": antiforgeryToken() },
            body: data
        }).then(function (r) {
            if (r.ok) {
                root.setAttribute("data-theme-source", "user");
            }
        }).catch(function () {
            /* offline: the browser keeps the choice in localStorage */
        });
    }

    // Phone footer shows only the active symbol (CSS); tapping it moves on to the next one.
    function isCompactFooter() {
        return window.matchMedia && window.matchMedia("(max-width: 768px)").matches;
    }

    function wireLangSwitch() {
        var buttons = document.querySelectorAll(".sidebar .lang-switch__btn");
        for (var i = 0; i < buttons.length; i++) {
            buttons[i].addEventListener("click", function (ev) {
                if (!isCompactFooter() || !this.classList.contains("is-active")) {
                    return;
                }
                var other = this.parentElement.querySelector(".lang-switch__btn:not(.is-active)");
                if (other) {
                    ev.preventDefault();
                    other.click();
                }
            });
        }
    }

    function wireThemeButtons() {
        var buttons = document.querySelectorAll(".theme-switch__btn");
        for (var i = 0; i < buttons.length; i++) {
            buttons[i].addEventListener("click", function () {
                var mode = normalizeMode(this.getAttribute("data-theme-value"));
                if (isCompactFooter() && mode === currentMode()) {
                    mode = MODES[(MODES.indexOf(mode) + 1) % MODES.length];
                }
                if (!mode) {
                    return;
                }
                storeMode(mode);
                applyTheme({ mode: mode });
                saveMode(mode);
            });
        }
    }

    function wireSystemListener() {
        if (!media) {
            return;
        }
        var handler = function () {
            if (currentMode() === "system") {
                applyTheme({ mode: "system" });
            }
        };
        if (typeof media.addEventListener === "function") {
            media.addEventListener("change", handler);
        } else if (typeof media.addListener === "function") {
            media.addListener(handler);
        }
    }

    function initTheme() {
        updateThemeButtons(currentMode());
        syncThemeColor();
        // One-time move: a mode picked before modes were saved per account goes to the account.
        var stored = getStoredMode();
        if (stored && root.getAttribute("data-theme-source") !== "user" && root.getAttribute("data-theme-save")) {
            saveMode(stored);
        }
        wireThemeButtons();
        wireLangSwitch();
        wireSystemListener();
    }

    // Filter bars: on phones only the search stays visible plus a "Filter (n)" toggle that
    // unfolds the remaining fields. n counts the filters that currently narrow the list.
    // A list toolbar keeps search and sort in view; every other filter lives in a dialog behind one
    // "Filter (n)" button, on phones and desktops alike. The fields stay inside the toolbar's own GET
    // form (the <dialog> is a child of it), so they submit exactly as before.
    function wireToolbars() {
        var bars = document.querySelectorAll("form.toolbar");
        for (var i = 0; i < bars.length; i++) {
            (function (bar) {
                var kids = Array.prototype.slice.call(bar.children);
                var apply = kids[kids.length - 1];
                var search = null;
                var inDialog = [];
                kids.forEach(function (el) {
                    if (el === apply) { return; }
                    if (el.classList.contains("toolbar__search")) {
                        // the first search stays; further text filters (folder ...) go into the dialog
                        if (!search) { search = el; } else { inDialog.push(el); }
                    } else if (el.classList.contains("toolbar__count") || el.classList.contains("toolbar__spacer")) {
                        return;
                    } else {
                        var field = el.querySelector("select");
                        if (field && field.name === "Sort") { return; } // sorting stays in the bar
                        inDialog.push(el);
                    }
                });
                var button = apply && apply.querySelector("button[type=submit]");
                if (inDialog.length < 2 || !button) { return; }

                function activeCount() {
                    var n = 0;
                    inDialog.forEach(function (el) {
                        var picker = el.querySelector(".mp-picker");
                        var field = el.querySelector("select, input:not([type=hidden])");
                        if (picker) {
                            if ((picker.getAttribute("data-selected") || "") !== "") { n++; }
                        } else if (field) {
                            if (field.tagName === "SELECT" ? field.selectedIndex > 0 : field.value !== "") { n++; }
                        }
                    });
                    return n;
                }

                var t = function (key, fallback) { return bar.getAttribute("data-t-" + key) || fallback; };
                var title = button.textContent.trim() || t("filters", "Filter");

                var dialog = document.createElement("dialog");
                dialog.className = "filter-dialog";
                dialog.innerHTML =
                    '<div class="filter-dialog__head"><h2 class="filter-dialog__title"></h2>' +
                    '<button type="button" class="icon-btn filter-dialog__close" aria-label=""><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18"/></svg></button></div>' +
                    '<div class="filter-dialog__body"></div>' +
                    '<div class="filter-dialog__foot"><a class="btn btn--secondary filter-dialog__reset"></a>' +
                    '<button type="submit" class="btn btn--primary filter-dialog__apply"></button></div>';
                dialog.querySelector(".filter-dialog__title").textContent = title;
                dialog.querySelector(".filter-dialog__close").setAttribute("aria-label", t("close", "Close"));
                var reset = dialog.querySelector(".filter-dialog__reset");
                reset.textContent = t("reset", "Reset");
                reset.setAttribute("href", location.pathname);
                dialog.querySelector(".filter-dialog__apply").textContent = t("apply", "Apply");
                var body = dialog.querySelector(".filter-dialog__body");
                inDialog.forEach(function (el) { body.appendChild(el); });
                bar.appendChild(dialog);

                // The original submit button stays as the form's default button (Enter in the search box)
                // but is no longer shown.
                apply.classList.add("visually-hidden");

                var wrap = document.createElement("div");
                wrap.className = "toolbar__group toolbar__toggle";
                var toggle = document.createElement("button");
                toggle.type = "button";
                toggle.className = "btn btn--secondary toolbar__filters";
                var n = activeCount();
                toggle.textContent = title + (n ? " (" + n + ")" : "");
                toggle.addEventListener("click", function () { dialog.showModal(); });
                wrap.appendChild(toggle);
                if (search) { search.insertAdjacentElement("afterend", wrap); } else { bar.insertBefore(wrap, bar.firstChild); }

                dialog.querySelector(".filter-dialog__close").addEventListener("click", function () { dialog.close(); });
                // a click on the backdrop closes it
                dialog.addEventListener("mousedown", function (event) { if (event.target === dialog) { dialog.close(); } });
            })(bars[i]);
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

    function init() {
        initTheme();
        wireSidebarToggle();
        wireToolbars();
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

// The installed app does not zoom the page by pinching or double-tapping (iOS ignores the viewport hint
// in Safari). Places that need zoom - the page viewer, the scanner - bring their own.
(function () {
    "use strict";
    ["gesturestart", "gesturechange", "gestureend"].forEach(function (name) {
        document.addEventListener(name, function (event) { event.preventDefault(); }, { passive: false });
    });
})();
