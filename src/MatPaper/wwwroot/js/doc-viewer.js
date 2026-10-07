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
            '<a class="doc-viewer__btn" data-act="download"></a>' +
            '</div>' +
            '<div class="doc-viewer__scroll"><div class="doc-viewer__pages"></div></div>' +
            '<div class="doc-viewer__page" aria-live="polite"></div>';
        document.body.appendChild(dialog);

        parts = {
            title: dialog.querySelector(".doc-viewer__title"),
            scroll: dialog.querySelector(".doc-viewer__scroll"),
            pages: dialog.querySelector(".doc-viewer__pages"),
            indicator: dialog.querySelector(".doc-viewer__page"),
            download: dialog.querySelector('[data-act="download"]'),
            close: dialog.querySelector('[data-act="close"]')
        };
        parts.close.textContent = "✕";
        parts.close.setAttribute("aria-label", text("close", "Close"));
        parts.download.textContent = "⤓";
        parts.download.setAttribute("aria-label", text("download", "Download"));

        dialog.addEventListener("click", function (event) {
            var act = event.target && event.target.closest ? event.target.closest("[data-act]") : null;
            if (!act) { return; }
            var name = act.getAttribute("data-act");
            if (name === "close") { dialog.close(); }
            else if (name === "in") { setZoom(zoom + 0.5); }
            else if (name === "out") { setZoom(zoom - 0.5); }
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
            if (observer) { observer.disconnect(); observer = null; }
            parts.pages.innerHTML = "";
            document.documentElement.classList.remove("has-viewer");
        });

        parts.scroll.addEventListener("scroll", updateIndicator, { passive: true });
        wirePinch();
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
        if (!el || !isPhone()) { return; }
        event.preventDefault();
        event.stopImmediatePropagation();
        open({ token: el.getAttribute("data-viewer-token"), title: el.getAttribute("data-viewer-title") || "" });
    }, true);

    window.MatPaperViewer = { open: open, isPhone: isPhone };
})();
