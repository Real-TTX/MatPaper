// Phone page viewer: a full-screen dialog that shows a document page by page.
//
// A browser's own PDF viewer inside an <iframe> is unusable on a phone (one page, no scrolling, no
// zoom). Instead the server renders each page as an image (/Documents/{token}/page/{n}) and this
// dialog stacks them in a scroll area: swipe to move, pinch or the +/- buttons to zoom, double-tap
// to jump between fit-width and 2x. Pages load lazily as they come into view.
//
//   MatPaperViewer.open({ token, title })       open it
//   MatPaperViewer.isPhone()                    true on narrow screens (where it should be used)
//   <button data-viewer-token="…" data-viewer-title="…">   opens it on click (phones only)
(function () {
    "use strict";

    var MIN_ZOOM = 1, MAX_ZOOM = 4;
    var dialog = null;
    var parts = null;
    var zoom = 1;
    var observer = null;

    function isPhone() {
        return window.matchMedia("(max-width: 800px)").matches;
    }

    // The installed app has no back button: a file link must open the viewer there, not take over the window.
    function isStandalone() {
        return window.navigator.standalone === true
            || (typeof window.matchMedia === "function" && window.matchMedia("(display-mode: standalone)").matches);
    }

    function text(key, fallback) {
        var el = document.getElementById("viewer-i18n");
        return (el && el.getAttribute("data-" + key)) || fallback;
    }

    function build() {
        dialog = document.createElement("dialog");
        dialog.className = "doc-viewer";
        dialog.innerHTML =
            '<div class="doc-viewer__bar">' +
            '<button type="button" class="doc-viewer__btn" data-act="close"></button>' +
            '<span class="doc-viewer__title"></span>' +
            '<button type="button" class="doc-viewer__btn" data-act="out">−</button>' +
            '<button type="button" class="doc-viewer__btn" data-act="in">+</button>' +
            '<button type="button" class="doc-viewer__btn" data-act="share"></button>' +
            '<a class="doc-viewer__btn" data-act="download" data-viewer-download></a>' +
            '</div>' +
            '<div class="doc-viewer__scroll"><div class="doc-viewer__pages"></div></div>' +
            '<div class="doc-viewer__page" aria-live="polite"></div>' +
            '<div class="doc-viewer__toast" role="status" hidden></div>';
        document.body.appendChild(dialog);

        parts = {
            title: dialog.querySelector(".doc-viewer__title"),
            scroll: dialog.querySelector(".doc-viewer__scroll"),
            pages: dialog.querySelector(".doc-viewer__pages"),
            indicator: dialog.querySelector(".doc-viewer__page"),
            download: dialog.querySelector('[data-act="download"]'),
            share: dialog.querySelector('[data-act="share"]'),
            toast: dialog.querySelector(".doc-viewer__toast"),
            close: dialog.querySelector('[data-act="close"]')
        };
        parts.close.textContent = "✕";
        parts.close.setAttribute("aria-label", text("close", "Close"));
        parts.download.textContent = "⤓";
        parts.download.setAttribute("aria-label", text("download", "Download"));
        parts.share.innerHTML = '<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="18" cy="5" r="3"/><circle cx="6" cy="12" r="3"/><circle cx="18" cy="19" r="3"/><path d="M8.6 13.5l6.8 4M15.4 6.5l-6.8 4"/></svg>';
        parts.share.setAttribute("aria-label", text("share", "Share"));
        parts.share.setAttribute("title", text("share", "Share"));

        dialog.addEventListener("click", function (event) {
            var act = event.target && event.target.closest ? event.target.closest("[data-act]") : null;
            if (!act) { return; }
            var name = act.getAttribute("data-act");
            if (name === "close") { dialog.close(); }
            else if (name === "in") { setZoom(zoom + 0.5); }
            else if (name === "out") { setZoom(zoom - 0.5); }
            else if (name === "share") { share(); }
            else if (name === "download" && isStandalone()) {
                // no download in the installed app's window: the share sheet saves the file ("Save to Files")
                event.preventDefault();
                if (current && current.file && canShareFiles() && navigator.canShare({ files: [current.file] })) {
                    navigator.share({ files: [current.file], title: current.title || "" }).catch(function () { });
                } else if (current && window.MatPaperDownload) {
                    window.MatPaperDownload.open("/Documents/" + current.token + "/download");
                }
            }
        });

        // Double-tap: fit width <-> 2x.
        var lastTap = 0;
        parts.scroll.addEventListener("touchend", function (event) {
            if (event.touches.length > 0 || event.changedTouches.length !== 1) { return; }
            var now = Date.now();
            if (now - lastTap < 300) {
                setZoom(zoom > 1.2 ? 1 : 2);
                event.preventDefault();
            }
            lastTap = now;
        });

        dialog.addEventListener("close", function () {
            current = null;
            if (observer) { observer.disconnect(); observer = null; }
            parts.pages.innerHTML = "";
            document.documentElement.classList.remove("has-viewer");
        });

        parts.scroll.addEventListener("scroll", updateIndicator, { passive: true });
        wirePinch();
    }

    // ---- Share ----------------------------------------------------------------
    // The file itself goes to another app (mail, messenger ...) through the phone's share sheet. Not every
    // browser can do that with a file; then a link that works for a few days is created and shared (or copied).
    var current = null;
    var toastTimer = null;

    function toast(message) {
        if (!parts || !parts.toast) { return; }
        parts.toast.textContent = message;
        parts.toast.hidden = false;
        if (toastTimer) { clearTimeout(toastTimer); }
        toastTimer = setTimeout(function () { parts.toast.hidden = true; }, 3500);
    }

    function fileNameOf(response, fallback) {
        var disposition = response.headers.get("Content-Disposition") || "";
        var star = /filename\*=UTF-8''([^;]+)/i.exec(disposition);
        if (star) { try { return decodeURIComponent(star[1]); } catch (e) { /* fall through */ } }
        var plain = /filename="?([^";]+)"?/i.exec(disposition);
        return plain ? plain[1] : fallback;
    }

    function loadFile(token, title) {
        return fetch("/Documents/" + token + "/download", { credentials: "same-origin" }).then(function (response) {
            if (!response.ok) { throw new Error("download"); }
            return response.blob().then(function (blob) {
                return new File([blob], fileNameOf(response, (title || "document") + ".pdf"), { type: blob.type || "application/pdf" });
            });
        });
    }

    function canShareFiles() {
        return typeof navigator.canShare === "function" && typeof navigator.share === "function" && typeof File === "function";
    }

    function copyLink(url, days) {
        var done = function () { toast(text("link-copied", "Link copied - valid for {0} days").replace("{0}", String(days))); };
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(url).then(done, function () { window.prompt(text("link", "Link"), url); });
        } else {
            window.prompt(text("link", "Link"), url);
        }
    }

    function shareLink(shown) {
        var tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
        var body = new FormData();
        body.append("__RequestVerificationToken", tokenEl ? tokenEl.value : "");
        body.append("days", "7");
        return fetch("/Documents/" + shown.token + "/share", { method: "POST", body: body, credentials: "same-origin" })
            .then(function (response) { return response.json().catch(function () { return null; }); })
            .then(function (result) {
                if (!result || !result.ok) {
                    toast(result && result.forbidden ? text("share-forbidden", "Only the owner can share this document.") : text("share-failed", "The document could not be shared."));
                    return;
                }
                if (typeof navigator.share === "function") {
                    return navigator.share({ title: shown.title || "", url: result.url }).catch(function (error) {
                        if (error && error.name === "AbortError") { return; }
                        copyLink(result.url, result.days);
                    });
                }
                copyLink(result.url, result.days);
            })
            .catch(function () { toast(text("share-failed", "The document could not be shared.")); });
    }

    function share() {
        if (!current) { return; }
        var shown = current;
        // The file is fetched while the viewer opens, so the share sheet can open at once (a phone only allows it
        // right after a tap).
        if (shown.file && canShareFiles() && navigator.canShare({ files: [shown.file] })) {
            navigator.share({ files: [shown.file], title: shown.title || "" }).catch(function (error) {
                if (error && error.name === "AbortError") { return; }
                shareLink(shown);
            });
            return;
        }
        shareLink(shown);
    }

    function setZoom(value) {
        zoom = Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, value));
        parts.pages.style.width = (zoom * 100) + "%";
    }

    // Two fingers: the pages grow or shrink around the point between them.
    function wirePinch() {
        var start = null;
        function distance(t) { return Math.hypot(t[0].clientX - t[1].clientX, t[0].clientY - t[1].clientY); }
        parts.scroll.addEventListener("touchstart", function (e) {
            if (e.touches.length === 2) {
                var rect = parts.scroll.getBoundingClientRect();
                start = {
                    d: distance(e.touches), zoom: zoom,
                    fx: (e.touches[0].clientX + e.touches[1].clientX) / 2 - rect.left,
                    fy: (e.touches[0].clientY + e.touches[1].clientY) / 2 - rect.top,
                    sl: parts.scroll.scrollLeft, st: parts.scroll.scrollTop
                };
            }
        }, { passive: true });
        parts.scroll.addEventListener("touchmove", function (e) {
            if (!start || e.touches.length !== 2) { return; }
            e.preventDefault();
            var before = zoom;
            setZoom(start.zoom * distance(e.touches) / start.d);
            var ratio = zoom / start.zoom;
            parts.scroll.scrollLeft = (start.sl + start.fx) * ratio - start.fx;
            parts.scroll.scrollTop = (start.st + start.fy) * ratio - start.fy;
            void before;
        }, { passive: false });
        parts.scroll.addEventListener("touchend", function (e) { if (e.touches.length < 2) { start = null; } }, { passive: true });
    }

    function updateIndicator() {
        var imgs = parts.pages.querySelectorAll("img[data-page]");
        if (imgs.length === 0) { parts.indicator.textContent = ""; return; }
        var mid = parts.scroll.getBoundingClientRect().top + parts.scroll.clientHeight / 2;
        var current = 1;
        for (var i = 0; i < imgs.length; i++) {
            var r = imgs[i].getBoundingClientRect();
            if (r.top <= mid) { current = i + 1; }
        }
        parts.indicator.textContent = imgs.length > 1 ? text("page", "Page") + " " + current + " / " + imgs.length : "";
    }

    function addPage(n, src) {
        var img = document.createElement("img");
        img.className = "doc-viewer__img";
        img.alt = text("page", "Page") + " " + n;
        img.setAttribute("data-page", String(n));
        img.setAttribute("data-src", src);
        // Until the picture is there the slot has roughly the height of an A4 page.
        img.style.aspectRatio = "1 / 1.414";
        img.addEventListener("load", function () { img.style.aspectRatio = ""; });
        parts.pages.appendChild(img);
        if (observer) { observer.observe(img); } else { img.src = src; }
    }

    function showMessage(message, token) {
        parts.pages.innerHTML = "";
        var box = document.createElement("div");
        box.className = "doc-viewer__message";
        var p = document.createElement("p");
        p.textContent = message;
        var a = document.createElement("a");
        a.className = "btn btn--primary";
        a.href = "/Documents/" + token + "/download";
        a.textContent = text("download", "Download");
        box.appendChild(p);
        box.appendChild(a);
        parts.pages.appendChild(box);
    }

    function open(options) {
        if (!options || !options.token) { return; }
        if (!dialog) { build(); }
        var token = options.token;

        parts.title.textContent = options.title || "";
        current = { token: token, title: options.title || "", file: null };
        if (canShareFiles()) {
            loadFile(token, options.title).then(function (file) {
                if (current && current.token === token) { current.file = file; }
            }).catch(function () { /* the link route stays */ });
        }
        parts.download.setAttribute("href", "/Documents/" + token + "/download");
        parts.pages.innerHTML = "";
        parts.indicator.textContent = "";
        setZoom(1);
        parts.scroll.scrollTop = 0;

        if ("IntersectionObserver" in window) {
            observer = new IntersectionObserver(function (entries) {
                entries.forEach(function (entry) {
                    if (!entry.isIntersecting) { return; }
                    var img = entry.target;
                    var src = img.getAttribute("data-src");
                    if (src) { img.src = src; img.removeAttribute("data-src"); }
                    observer.unobserve(img);
                });
            }, { root: parts.scroll, rootMargin: "100% 0px" });
        }

        document.documentElement.classList.add("has-viewer");
        dialog.showModal();

        fetch("/Documents/" + token + "/pages", { credentials: "same-origin" })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (info) {
                if (!info) { showMessage(text("failed", "The document could not be shown."), token); return; }
                if (info.kind === "pdf" && info.pages > 0) {
                    // Pixel width of the screen, doubled for sharpness and for the zoom steps.
                    var width = Math.min(2000, Math.max(800, Math.round(window.innerWidth * (window.devicePixelRatio || 1))));
                    for (var n = 1; n <= info.pages; n++) {
                        addPage(n, "/Documents/" + token + "/page/" + n + "?w=" + width);
                    }
                    updateIndicator();
                } else if (info.kind === "image") {
                    addPage(1, "/Documents/" + token + "/view");
                } else {
                    showMessage(text("no-preview", "There is no preview for this file type."), token);
                }
            })
            .catch(function () { showMessage(text("failed", "The document could not be shown."), token); });
    }

    document.addEventListener("click", function (event) {
        var el = event.target && event.target.closest ? event.target.closest("[data-viewer-token]") : null;
        if (!el || !(isPhone() || isStandalone())) { return; }
        event.preventDefault();
        event.stopImmediatePropagation();
        open({ token: el.getAttribute("data-viewer-token"), title: el.getAttribute("data-viewer-title") || "" });
    }, true);

    window.MatPaperViewer = { open: open, isPhone: isPhone };
})();
