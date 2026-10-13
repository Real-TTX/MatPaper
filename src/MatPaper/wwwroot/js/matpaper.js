// MatPaper - the few behaviours of the app that the MatUi toolkit (wwwroot/matui) does not bring: the service worker of the PWA, a busy mark on
// a form that is on its way, rows that open their page, the share sheet for files in the installed app, archive numbers, the preview of the
// appearance page. Everything else (menu, theme, dialogs, pickers, validation, tabs) is the toolkit's; texts come from the server as data attributes.
(function () {
    "use strict";

    var doc = document;
    var root = doc.documentElement;

    function closest(el, selector) {
        return el && el.closest ? el.closest(selector) : null;
    }

    // ---- PWA: register the service worker for the offline shell and installability.
    function registerServiceWorker() {
        if (!("serviceWorker" in navigator)) { return; }
        navigator.serviceWorker.register("/service-worker.js").catch(function () { /* registration failure is non-fatal */ });
    }

    // ---- A page answered by a handler button (Test, Preview, ...) keeps "?handler=..." in its address, and the form without an action
    // would post "Save" to that same handler. Drop it from the address.
    function cleanHandlerFromAddress() {
        if (!root.hasAttribute("data-posted")) { return; }
        var url = new URL(location.href);
        if (url.searchParams.has("handler")) {
            url.searchParams.delete("handler");
            history.replaceState(null, "", url.pathname + url.search + url.hash);
        }
    }

    // ---- Busy state: a slow POST used to look like nothing happened, so people clicked again. The button is marked instead of disabled:
    // a disabled submitter is not sent, which would drop the name/value that tells the handler which action was chosen.
    function wireBusyForms() {
        // Bubble phase on purpose: a page script that cancels its own submit (the uploader posts by XHR) runs first, and a cancelled submit
        // is not a request in flight.
        doc.addEventListener("submit", function (event) {
            var form = event.target;
            if (!(form instanceof HTMLFormElement) || form.hasAttribute("data-no-busy") || event.defaultPrevented) { return; }
            if (form.getAttribute("data-busy") === "1") { event.preventDefault(); return; }
            // a form that only asks for a page of a list is no work worth a spinner
            if ((form.getAttribute("method") || "get").toLowerCase() === "get") { return; }

            form.setAttribute("data-busy", "1");
            var submitter = event.submitter || form.querySelector("[type=submit]");
            if (submitter) {
                submitter.setAttribute("aria-busy", "true");
                submitter.classList.add("is-loading");
            }

            // A validation failure or a cancelled navigation must not leave the form stuck, so let go again after a moment.
            window.setTimeout(function () {
                form.removeAttribute("data-busy");
                if (submitter) {
                    submitter.removeAttribute("aria-busy");
                    submitter.classList.remove("is-loading");
                }
            }, 15000);
        });
    }

    // ---- Clickable rows: a row with data-row-href opens its page; anything interactive inside the row wins, and so does a text selection.
    function wireRowLinks() {
        doc.addEventListener("click", function (event) {
            var row = closest(event.target, "[data-row-href]");
            if (!row) { return; }
            if (closest(event.target, "a, button, input, select, textarea, label, [data-confirm]")) { return; }
            var selection = window.getSelection();
            if (selection && selection.toString().length > 0) { return; }

            var href = row.getAttribute("data-row-href");
            if (event.metaKey || event.ctrlKey || event.button === 1) {
                window.open(href, "_blank", "noopener");
            } else {
                window.location.href = href;
            }
        });

        // Keyboard: a row is reachable with Tab and opens with Enter.
        doc.addEventListener("keydown", function (event) {
            if (event.key !== "Enter") { return; }
            var row = closest(event.target, "[data-row-href]");
            if (row && event.target === row) { window.location.href = row.getAttribute("data-row-href"); }
        });
    }

    // ---- The installed app does not zoom the page by pinching or double-tapping (iOS ignores the viewport hint in Safari).
    // Places that need zoom - the page viewer, the scanner - bring their own.
    function noGestureZoom() {
        if (!isStandalone()) { return; }
        ["gesturestart", "gesturechange", "gestureend"].forEach(function (name) {
            doc.addEventListener(name, function (event) { event.preventDefault(); }, { passive: false });
        });
    }

    // ---- Files in the installed app: it has no address bar and no back button. A link to a file - a download, the XML of an e-invoice -
    // would open the file in the app's own window and trap the user there until the app is restarted. Here such a link fetches the file and
    // hands it to the share sheet ("Save to Files", Mail ...) instead; the page stays where it is. In a normal browser tab nothing changes.
    function isStandalone() {
        return window.navigator.standalone === true
            || (typeof window.matchMedia === "function" && window.matchMedia("(display-mode: standalone)").matches);
    }

    function text(key, fallback) {
        var el = doc.getElementById("viewer-i18n");
        return (el && el.getAttribute("data-" + key)) || fallback;
    }

    function fileNameOf(response, fallback) {
        var disposition = response.headers.get("Content-Disposition") || "";
        var star = /filename\*=UTF-8''([^;]+)/i.exec(disposition);
        if (star) { try { return decodeURIComponent(star[1]); } catch (e) { /* fall through */ } }
        var plain = /filename="?([^";]+)"?/i.exec(disposition);
        return plain ? plain[1] : fallback;
    }

    var sheet = null;

    function buildSheet() {
        sheet = doc.createElement("dialog");
        sheet.className = "mat-dialog mat-dialog--sm";
        sheet.setAttribute("aria-label", text("download", "Download"));
        sheet.innerHTML =
            '<div class="mat-dialog__form">' +
            '<div class="mat-dialog__head"><h2 class="mat-dialog__title"></h2>' +
            '<button type="button" class="mat-btn mat-btn--ghost mat-btn--icon mat-dialog__close" data-dialog-close aria-label=""><svg class="mat-icon" aria-hidden="true" focusable="false"><use href="#mat-i-x"></use></svg></button></div>' +
            '<div class="mat-dialog__body"><p class="mp-sheet__text"></p></div>' +
            '<div class="mat-dialog__foot"><div class="mat-btn-row mat-btn-row--plain">' +
            '<button type="button" class="mat-btn mat-btn--primary" data-sheet="save" hidden></button>' +
            '<button type="button" class="mat-btn mat-btn--secondary" data-dialog-close></button></div></div></div>';
        sheet.querySelector(".mat-dialog__title").textContent = text("download", "Download");
        sheet.querySelector(".mat-dialog__close").setAttribute("aria-label", text("close", "Close"));
        sheet.querySelector(".mat-btn--secondary").textContent = text("close", "Close");
        doc.body.appendChild(sheet);
    }

    // Prepares the file, then waits for a tap on "Save / share": the share sheet only opens straight after a tap.
    function openSheet(url) {
        if (!sheet) { buildSheet(); }
        var message = sheet.querySelector(".mp-sheet__text");
        var save = sheet.querySelector('[data-sheet="save"]');
        message.textContent = text("dl-preparing", "Preparing the file …");
        save.hidden = true;
        if (!sheet.open) { window.MatUi.dialog.open(sheet); }

        fetch(url, { credentials: "same-origin" })
            .then(function (response) {
                if (!response.ok) { throw new Error("download"); }
                return response.blob().then(function (blob) {
                    return new File([blob], fileNameOf(response, "document"), { type: blob.type || "application/octet-stream" });
                });
            })
            .then(function (file) {
                var canShare = typeof navigator.canShare === "function" && typeof navigator.share === "function" && navigator.canShare({ files: [file] });
                if (!canShare) {
                    message.textContent = text("dl-unsupported", "This device cannot save files from the app. Open MatPaper in the browser to download.");
                    return;
                }
                message.textContent = file.name;
                save.textContent = text("dl-save", "Save or share");
                save.hidden = false;
                save.onclick = function () {
                    navigator.share({ files: [file], title: file.name })
                        .then(function () { window.MatUi.dialog.close(sheet); })
                        .catch(function (error) { if (!error || error.name !== "AbortError") { message.textContent = text("dl-failed", "The file could not be saved."); } });
                };
            })
            .catch(function () { message.textContent = text("dl-failed", "The file could not be saved."); });
    }

    window.MatPaperDownload = { open: openSheet, isStandalone: isStandalone };

    function wireDownloadLinks() {
        if (!isStandalone()) { return; }
        doc.addEventListener("click", function (event) {
            var link = closest(event.target, "a[href]");
            if (!link || link.hasAttribute("data-viewer-download") || link.hasAttribute("data-viewer-token")) { return; }
            var href = link.getAttribute("href") || "";
            if (!/\/download(\?|$)|\/xml(\?|$)|[?&]download=true/.test(href)) { return; }
            event.preventDefault();
            event.stopPropagation();
            openSheet(link.href);
        }, true);
    }

    // ---- A control with data-autosubmit sends its form as soon as it changes (a switch that applies at once, rule FORM-04 of the toolkit: a switch is not saved with a button).
    function wireAutoSubmit() {
        doc.addEventListener("change", function (event) {
            var el = event.target;
            if (!el.hasAttribute || !el.hasAttribute("data-autosubmit") || !el.form) { return; }
            if (typeof el.form.requestSubmit === "function") { el.form.requestSubmit(); } else { el.form.submit(); }
        });
    }

    // ---- Tabs of the toolkit (data-tabs): the tab is kept in the address (#panel) and across a postback of the page (Test connection ...), and a tab
    // with a problem opens first, so the message is not hidden behind another tab.
    function wireTabs() {
        doc.querySelectorAll("[data-tabs]").forEach(function (group) {
            var tabs = Array.prototype.slice.call(group.querySelectorAll('[role="tab"]'));
            if (tabs.length === 0) { return; }
            function panelOf(tab) { return doc.getElementById(tab.getAttribute("aria-controls")); }

            group.addEventListener("click", function (event) {
                var tab = closest(event.target, '[role="tab"]');
                if (tab && history.replaceState) { history.replaceState(null, "", "#" + tab.getAttribute("aria-controls")); }
            });

            var key = "mp-tab:" + location.pathname;
            var remembered = null;
            try {
                var saved = JSON.parse(sessionStorage.getItem(key) || "null");
                if (saved && Date.now() - saved.at < 15000) { remembered = saved.panel; }
                sessionStorage.removeItem(key);
            } catch (err) { /* storage unavailable */ }
            group.addEventListener("submit", function () {
                var selected = group.querySelector('[role="tab"][aria-selected="true"]');
                try { sessionStorage.setItem(key, JSON.stringify({ panel: selected && selected.getAttribute("aria-controls"), at: Date.now() })); } catch (err) { /* storage unavailable */ }
            }, true);

            var wanted = (location.hash || "").slice(1) || remembered;
            var withError = tabs.filter(function (tab) { var panel = panelOf(tab); return panel && panel.querySelector(".mat-field.is-invalid, .mat-error:not(:empty)"); })[0];
            var target = withError || tabs.filter(function (tab) { return tab.getAttribute("aria-controls") === wanted; })[0];
            if (target && target.getAttribute("aria-selected") !== "true") { target.click(); }
        });
    }

    // ---- A run in progress: ask for the page in the background every few seconds and swap in the parts marked data-live, so the result shows
    // up without a reload. Stops when the run ends.
    function wireLivePanels() {
        if (!doc.querySelector("[data-live][data-running]")) { return; }
        var tries = 0;
        var timer = window.setInterval(function () {
            if (++tries > 200) { window.clearInterval(timer); return; }
            fetch(location.href, { credentials: "same-origin", headers: { "X-Requested-With": "fetch" } })
                .then(function (r) { return r.ok ? r.text() : null; })
                .then(function (html) {
                    if (!html) { return; }
                    var fresh = new DOMParser().parseFromString(html, "text/html");
                    doc.querySelectorAll("[data-live]").forEach(function (el) {
                        var next = fresh.querySelector("[data-live=\"" + el.getAttribute("data-live") + "\"]");
                        if (next) { el.replaceWith(next); if (window.MatUi) { window.MatUi.refresh(next); } }
                    });
                    if (!doc.querySelector("[data-live][data-running]")) { window.clearInterval(timer); }
                })
                .catch(function () { /* the next try */ });
        }, 3000);
    }

    // ---- Archive numbers: "Next free number" fills the field beside it; printing a copy with a number that was just given (the box
    // "give it the next free number") makes the page show it afterwards.
    function wireArchiveNumbers() {
        doc.addEventListener("click", function (event) {
            var button = closest(event.target, "[data-asn-next]");
            if (!button) { return; }
            var field = closest(button, ".mat-field");
            var input = field && field.querySelector("input");
            if (!input) { return; }
            input.value = button.getAttribute("data-asn-next");
            input.dispatchEvent(new Event("input", { bubbles: true }));
            input.focus();
        });

        doc.addEventListener("submit", function (event) {
            var form = event.target;
            if (!form.matches || !form.matches("[data-stamp-form]")) { return; }
            var assign = form.querySelector("input[name=assign]");
            if (assign && assign.checked) {
                // the PDF opens in a new tab; this page learns about the new number by loading again
                window.setTimeout(function () { window.location.reload(); }, 1500);
            }
        });
    }

    // ---- Appearance page: every choice is applied to the page at once (not saved; Save stores it on the account). A choice of "instance
    // default" previews the default. The attributes are those of the toolkit: data-theme, data-scheme, data-accent.
    function wireAppearancePreview() {
        var form = doc.getElementById("appearance-form");
        if (!form) { return; }

        form.addEventListener("change", function (event) {
            var input = event.target;
            var part = input.getAttribute && input.getAttribute("data-theme-part");
            if (!part) { return; }
            var value = input.hasAttribute("data-preview") ? input.getAttribute("data-preview") : "";
            if (part === "mode") {
                if (value === "light" || value === "dark") { root.setAttribute("data-theme", value); } else { root.removeAttribute("data-theme"); }
            } else if (part === "scheme") {
                if (value) { root.setAttribute("data-scheme", value); } else { root.removeAttribute("data-scheme"); }
            } else if (part === "accent") {
                if (value) { root.setAttribute("data-accent", value); } else { root.removeAttribute("data-accent"); }
            }
        });
    }

    // ---- User colours (tags, projects): data-colour="#rrggbb" becomes the custom property --mat-swatch, the toolkit's way of showing a colour that is data
    // (no inline style in the markup, rule CODE-02). SHIM: MatUi gets data-colour itself (feature C of the matpaper-needs branch of MatDesign); remove this with the next update.
    function fillColours(scope) {
        (scope || doc).querySelectorAll("[data-colour]").forEach(function (el) {
            var value = el.getAttribute("data-colour");
            if (/^#([0-9a-f]{3}|[0-9a-f]{6})$/i.test(value)) { el.style.setProperty("--mat-swatch", value); }
        });
    }
    window.MatPaperColours = { fill: fillColours };

    function init() {
        fillColours();
        cleanHandlerFromAddress();
        wireBusyForms();
        wireRowLinks();
        wireAutoSubmit();
        wireTabs();
        wireLivePanels();
        noGestureZoom();
        wireDownloadLinks();
        wireArchiveNumbers();
        wireAppearancePreview();
        registerServiceWorker();
    }

    if (doc.readyState === "loading") {
        doc.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
