// Document scanner UI: camera -> automatic crop -> filter -> several pages.
// Full-screen on a phone, the same on a desktop with a webcam. The image work lives in scanner-core.js.
//
//   MatPaperScanner.open({ onDone: function (files, thumbs) { ... } })
//        files  = the finished pages as JPEG Files, in order
//        thumbs = small canvases of the same pages (for a summary)
//   MatPaperScanner.openPages()   open on the page overview (to edit what was scanned)
//   MatPaperScanner.reset()       forget all pages
//   MatPaperScanner.count()       how many pages there are
(function () {
    "use strict";

    var core = window.MatPaperScan;
    if (!core) { return; }

    var cfg = document.getElementById("scanner-config");
    function t(key, fallback) { return (cfg && cfg.getAttribute("data-t-" + key)) || fallback; }

    var PREVIEW_SIDE = 1100;   // review preview
    var FINAL_SIDE = 2200;     // what ends up in the PDF
    var SOURCE_SIDE = 2600;    // photos are scaled down to this before any work
    var SENS_KEY = "matpaper.scan.sensitivity";

    var pages = [];            // { src, quad, turns, filter, preview, thumb }
    var current = -1;
    var root = null, ui = {}, view = "camera", onDone = null;
    var stream = null, track = null, detectTimer = null;
    var smoothQuad = null, stableFrames = 0, lastFound = null, autoMode = true, torchOn = false;
    var sensitivity = 1;
    var MODE_KEY = "matpaper.scan.mode";
    var mode = "live"; // "live" = the in-page camera, "native" = the phone's own camera app (better optics, autofocus)
    try { if (localStorage.getItem(MODE_KEY) === "native") { mode = "native"; } } catch (e) { }
    try { var saved = parseInt(localStorage.getItem(SENS_KEY), 10); if (saved >= 0 && saved <= 2) { sensitivity = saved; } } catch (e) { }

    var FILTERS = [["enhanced", "Enhanced"], ["color", "Original"], ["gray", "Gray"], ["bw", "B/W"]];

    // ------------------------------------------------------------------ icons (stroke, currentColor)

    var ICONS = {
        close: '<path d="M6 6l12 12M18 6L6 18"/>',
        back: '<path d="M15 5l-7 7 7 7"/>',
        torch: '<path d="M13 2L4 14h7l-1 8 9-12h-7z"/>',
        sliders: '<path d="M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M20 18h0"/><circle cx="16" cy="6" r="2"/><circle cx="10" cy="12" r="2"/><circle cx="18" cy="18" r="2"/>',
        auto: '<path d="M12 3l2.2 5.3L20 9l-4.3 3.7L17 18l-5-3-5 3 1.3-5.3L4 9l5.8-.7z"/>',
        rotateCw: '<path d="M20 11a8 8 0 1 0-2.3 5.7"/><path d="M20 4v7h-7"/>',
        rotateCcw: '<path d="M4 11a8 8 0 1 1 2.3 5.7"/><path d="M4 4v7h7"/>',
        crop: '<path d="M6 2v14a2 2 0 0 0 2 2h14"/><path d="M2 6h14a2 2 0 0 1 2 2v14"/>',
        camera: '<path d="M4 8a2 2 0 0 1 2-2h2l1.5-2h5L16 6h2a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z"/><circle cx="12" cy="12.5" r="3.5"/>',
        plus: '<path d="M12 5v14M5 12h14"/>',
        check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
        trash: '<path d="M4 7h16M10 11v6M14 11v6M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12M9 7V4h6v3"/>',
        left: '<path d="M14 6l-6 6 6 6"/>',
        right: '<path d="M10 6l6 6-6 6"/>',
        live: '<rect x="3" y="6" width="13" height="12" rx="2"/><path d="M16 10l5-3v10l-5-3z"/>',
        native: '<rect x="7" y="2.5" width="10" height="19" rx="2"/><circle cx="12" cy="9.5" r="3"/><path d="M10.5 18h3"/>',
        image: '<rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="10" r="1.7"/><path d="M4 18l5-5 4 4 3-3 4 4"/>',
        detect: '<path d="M4 8V5a1 1 0 0 1 1-1h3M16 4h3a1 1 0 0 1 1 1v3M20 16v3a1 1 0 0 1-1 1h-3M8 20H5a1 1 0 0 1-1-1v-3"/><path d="M9 12h6"/>'
    };

    function icon(name) {
        var s = document.createElementNS("http://www.w3.org/2000/svg", "svg");
        s.setAttribute("viewBox", "0 0 24 24");
        s.setAttribute("fill", "none");
        s.setAttribute("stroke", "currentColor");
        s.setAttribute("stroke-width", "1.9");
        s.setAttribute("stroke-linecap", "round");
        s.setAttribute("stroke-linejoin", "round");
        s.setAttribute("aria-hidden", "true");
        s.setAttribute("class", "scn__icon");
        s.innerHTML = ICONS[name] || "";
        return s;
    }

    // ------------------------------------------------------------------ DOM helpers

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) { e.className = cls; }
        if (text) { e.textContent = text; }
        return e;
    }

    function btn(cls, iconName, label, action, aria) {
        var b = el("button", cls);
        b.type = "button";
        if (iconName) { b.appendChild(icon(iconName)); }
        if (label) { b.appendChild(el("span", "scn__label", label)); }
        if (aria || label) { b.setAttribute("aria-label", aria || label); }
        b.addEventListener("click", action);
        return b;
    }

    function build() {
        root = el("div", "scn");
        root.setAttribute("role", "dialog");
        root.setAttribute("aria-label", t("title", "Scanner"));

        // ---------------- camera
        var cam = el("div", "scn__view");
        var camTop = el("div", "scn__top");
        ui.closeBtn = btn("scn__tool", "close", "", close, t("close", "Close"));
        ui.camTitle = el("div", "scn__heading", t("title", "Scanner"));
        ui.modeBtn = btn("scn__tool", "live", "", toggleMode, t("mode", "Camera mode"));
        ui.sensBtn = btn("scn__tool scn__tool--text", "sliders", "", cycleSensitivity, t("sensitivity", "Sensitivity"));
        ui.sensLabel = el("span", "scn__label");
        ui.sensBtn.appendChild(ui.sensLabel);
        ui.torchBtn = btn("scn__tool", "torch", "", toggleTorch, t("torch", "Light"));
        ui.torchBtn.hidden = true;
        ui.autoBtn = btn("scn__tool scn__tool--text", "auto", t("auto", "Auto"), toggleAuto);
        camTop.append(ui.closeBtn, ui.camTitle, ui.modeBtn, ui.sensBtn, ui.torchBtn, ui.autoBtn);

        ui.stage = el("div", "scn__stage scn__stage--camera");
        ui.video = el("video", "scn__video");
        ui.video.setAttribute("playsinline", "");
        ui.video.muted = true;
        ui.overlay = el("canvas", "scn__overlay");
        ui.hint = el("div", "scn__hint", t("hint", "Hold the camera over the page"));
        ui.message = el("div", "scn__message");
        ui.message.hidden = true;
        ui.nativePanel = el("div", "scn__native");
        ui.nativePanel.append(
            icon("native"),
            el("p", "scn__native-text", t("native-text", "The photo is taken with the camera app of your device - usually sharper, with autofocus.")),
            btn("scn__action scn__action--primary scn__native-btn", "camera", t("native-shoot", "Take photo"), function () { ui.nativeInput.click(); }),
            btn("scn__action scn__native-btn", "image", t("native-pick", "Choose photos"), function () { ui.pickInput.click(); }));
        ui.nativeInput = el("input");
        ui.nativeInput.type = "file"; ui.nativeInput.accept = "image/*"; ui.nativeInput.setAttribute("capture", "environment"); ui.nativeInput.hidden = true;
        ui.pickInput = el("input");
        ui.pickInput.type = "file"; ui.pickInput.accept = "image/*"; ui.pickInput.multiple = true; ui.pickInput.hidden = true;
        [ui.nativeInput, ui.pickInput].forEach(function (inp) {
            inp.addEventListener("change", function () { addFiles(inp.files); inp.value = ""; });
        });
        ui.stage.append(ui.video, ui.overlay, ui.nativePanel, ui.hint, ui.message, ui.nativeInput, ui.pickInput);

        var camBottom = el("div", "scn__bottom scn__bottom--camera");
        ui.pagesBtn = btn("scn__pagesbtn", "", "", showPages, t("pages", "Pages"));
        ui.pagesThumb = el("canvas", "scn__pagesthumb");
        ui.pagesCount = el("span", "scn__badge");
        ui.pagesBtn.append(ui.pagesThumb, ui.pagesCount);
        ui.shutter = btn("scn__shutter", "", "", function () { if (mode === "native") { ui.nativeInput.click(); } else { capture(false); } }, t("shoot", "Take photo"));
        ui.shutter.appendChild(el("span", "scn__shutter-ring"));
        ui.finishBtn = btn("scn__action scn__action--primary", "check", t("finish", "Done"), finish);
        camBottom.append(ui.pagesBtn, ui.shutter, ui.finishBtn);
        cam.append(camTop, ui.stage, camBottom);
        ui.cameraView = cam;

        // ---------------- review
        var rev = el("div", "scn__view");
        var revTop = el("div", "scn__top");
        ui.revBack = btn("scn__tool", "back", "", showCamera, t("camera", "Camera"));
        ui.revTitle = el("div", "scn__heading");
        revTop.append(ui.revBack, ui.revTitle, btn("scn__tool", "rotateCw", "", function () { turnPage(1); }, t("rotate", "Rotate")));
        ui.reviewStage = el("div", "scn__stage");
        ui.reviewCanvas = el("canvas", "scn__page");
        ui.reviewStage.appendChild(ui.reviewCanvas);
        ui.filterBar = el("div", "scn__filters");
        FILTERS.forEach(function (f) {
            var b = el("button", "scn__chip", t("f-" + f[0], f[1]));
            b.type = "button";
            b.setAttribute("data-filter", f[0]);
            b.addEventListener("click", function () { setFilter(f[0]); });
            ui.filterBar.appendChild(b);
        });
        var revBottom = el("div", "scn__bottom scn__bottom--actions");
        revBottom.append(
            btn("scn__action", "camera", t("retake", "Retake"), retake),
            btn("scn__action", "crop", t("crop", "Crop"), function () { showAdjust(current); }),
            btn("scn__action", "plus", t("add-page", "Page"), showCamera),
            btn("scn__action scn__action--primary", "check", t("finish", "Done"), finish));
        rev.append(revTop, ui.reviewStage, ui.filterBar, revBottom);
        ui.reviewView = rev;

        // ---------------- adjust corners
        var adj = el("div", "scn__view");
        var adjTop = el("div", "scn__top");
        adjTop.append(btn("scn__tool", "back", "", function () { showReview(current); }, t("back", "Back")),
            el("div", "scn__heading", t("adjust", "Drag the corners onto the page")));
        ui.adjustStage = el("div", "scn__stage");
        ui.adjustWrap = el("div", "scn__adjust-wrap");
        ui.adjustCanvas = el("canvas", "scn__page");
        ui.adjustSvg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
        ui.adjustSvg.setAttribute("class", "scn__adjust-svg");
        ui.adjustWrap.append(ui.adjustCanvas, ui.adjustSvg);
        ui.adjustStage.appendChild(ui.adjustWrap);
        var adjBottom = el("div", "scn__bottom scn__bottom--actions");
        adjBottom.append(
            btn("scn__action", "detect", t("auto-corners", "Detect again"), redetect),
            btn("scn__action", "rotateCcw", t("turn-left", "Left"), function () { turnSource(-1); }),
            btn("scn__action", "rotateCw", t("turn-right", "Right"), function () { turnSource(1); }),
            btn("scn__action scn__action--primary", "check", t("apply", "Apply"), function () { showReview(current); }));
        adj.append(adjTop, ui.adjustStage, adjBottom);
        ui.adjustView = adj;

        // ---------------- pages overview
        var pg = el("div", "scn__view");
        var pgTop = el("div", "scn__top");
        pgTop.append(btn("scn__tool", "back", "", showCamera, t("camera", "Camera")), el("div", "scn__heading", t("pages", "Pages")));
        ui.grid = el("div", "scn__grid");
        var pgBottom = el("div", "scn__bottom scn__bottom--actions");
        pgBottom.append(
            btn("scn__action", "plus", t("add-page", "Page"), showCamera),
            btn("scn__action scn__action--primary", "check", t("finish", "Done"), finish));
        pg.append(pgTop, ui.grid, pgBottom);
        ui.pagesView = pg;

        ui.busy = el("div", "scn__busy", t("working", "Working …"));
        ui.busy.hidden = true;

        root.append(cam, rev, adj, pg, ui.busy);
        document.body.appendChild(root);
        window.addEventListener("resize", function () { if (root && !root.hidden) { redrawCurrent(); } });
        updateSens();
    }

    function setView(name) {
        view = name;
        ui.cameraView.hidden = name !== "camera";
        ui.reviewView.hidden = name !== "review";
        ui.adjustView.hidden = name !== "adjust";
        ui.pagesView.hidden = name !== "pages";
    }

    function busy(on) { ui.busy.hidden = !on; }

    // ------------------------------------------------------------------ open / close

    // The area behind the status bar belongs to the scanner while it is open (black, like its bars) and is given
    // back to the app when it closes: the browser colour is set explicitly both ways and the root is repainted,
    // so nothing dark is left behind the status bar afterwards.
    function barColor(scanning) {
        var html = document.documentElement;
        var meta = document.querySelector('meta[name="theme-color"]');
        if (meta) {
            var surface = getComputedStyle(html).getPropertyValue("--color-surface").trim();
            meta.setAttribute("content", scanning ? "#000000" : (surface || "#ffffff"));
        }
        if (!scanning) {
            html.style.backgroundColor = "var(--color-surface)";
            void html.offsetHeight;
            html.style.removeProperty("background-color");
        }
    }

    function open(options) {
        onDone = (options && options.onDone) || onDone;
        if (!root) { build(); }
        root.hidden = false;
        barColor(true);
        document.documentElement.classList.add("has-scanner");
        if (options && options.pages && pages.length) { showPages(); } else { showCamera(); }
    }

    function hide() {
        stopCamera();
        if (root) { root.hidden = true; }
        document.documentElement.classList.remove("has-scanner");
        barColor(false);
    }

    // Closing keeps what was scanned; the page that opened the scanner decides what to do with it.
    function close() {
        if (pages.length) { finish(); } else { hide(); if (onDone) { onDone([], []); } }
    }

    // ------------------------------------------------------------------ camera

    function updateCameraBar() {
        var has = pages.length > 0;
        ui.pagesBtn.style.visibility = has ? "visible" : "hidden";
        ui.finishBtn.style.visibility = has ? "visible" : "hidden";
        ui.camTitle.textContent = has ? t("page-n", "Page {0}").replace("{0}", pages.length + 1) : t("title", "Scanner");
        if (has) {
            ui.pagesCount.textContent = String(pages.length);
            var th = thumbOf(pages[pages.length - 1], 120);
            ui.pagesThumb.width = th.width; ui.pagesThumb.height = th.height;
            ui.pagesThumb.getContext("2d").drawImage(th, 0, 0);
        }
    }

    function showCamera() {
        setView("camera");
        updateCameraBar();
        applyMode();
    }

    // The two ways to get a photo: the live view (outline while you hold the camera, automatic release)
    // or the device's camera app (full sensor quality, autofocus), judged afterwards.
    function applyMode() {
        var native = mode === "native";
        ui.nativePanel.hidden = !native;
        ui.video.style.visibility = native ? "hidden" : "visible";
        ui.overlay.style.visibility = native ? "hidden" : "visible";
        ui.autoBtn.hidden = native;
        if (native) { ui.torchBtn.hidden = true; }
        ui.modeBtn.replaceChildren(icon(native ? "native" : "live"));
        ui.modeBtn.setAttribute("aria-label", t("mode", "Camera mode") + ": " + (native ? t("mode-native", "Camera app") : t("mode-live", "Live camera")));
        ui.hint.hidden = native;
        if (native) { stopCamera(); ui.message.hidden = true; } else { startCamera(); }
    }

    function toggleMode() {
        mode = mode === "live" ? "native" : "live";
        try { localStorage.setItem(MODE_KEY, mode); } catch (e) { }
        applyMode();
        flashHint(mode === "native" ? t("mode-native-hint", "Camera app: take the photo, it is judged afterwards") : t("mode-live-hint", "Live camera"));
    }

    // Photos from the camera app or the gallery: find the page in each, then show the last one.
    function addFiles(fileList) {
        var files = Array.prototype.slice.call(fileList || []).filter(function (f) { return /^image\//.test(f.type); });
        if (!files.length) { return; }
        busy(true);
        var chain = Promise.resolve();
        files.forEach(function (file) {
            chain = chain.then(function () {
                return loadImage(file).then(function (canvas) {
                    var det = null;
                    try { det = core.detect(canvas, { sensitivity: sensitivity }); } catch (e) { det = null; }
                    pages.push({ src: canvas, quad: det ? det.quad : defaultQuad(), turns: 0, filter: "enhanced", preview: null, thumb: null });
                });
            });
        });
        chain.then(function () {
            busy(false);
            current = pages.length - 1;
            stopCamera();
            showReview(current);
        }).catch(function () { busy(false); });
    }

    function loadImage(file) {
        return new Promise(function (resolve, reject) {
            var url = URL.createObjectURL(file), img = new Image();
            img.onload = function () {
                var c = core.makeCanvas(img.naturalWidth, img.naturalHeight);
                c.getContext("2d", { willReadFrequently: true }).drawImage(img, 0, 0);
                URL.revokeObjectURL(url);
                resolve(limit(c));
            };
            img.onerror = function () { URL.revokeObjectURL(url); reject(); };
            img.src = url;
        });
    }

    function startCamera() {
        if (stream) { return; }
        ui.message.hidden = true;
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            return cameraUnavailable(t("no-camera", "The camera is not available here (it needs HTTPS)."));
        }
        navigator.mediaDevices.getUserMedia({
            video: { facingMode: { ideal: "environment" }, width: { ideal: 1920 }, height: { ideal: 1080 } },
            audio: false
        }).then(function (s) {
            stream = s;
            track = s.getVideoTracks()[0];
            ui.video.srcObject = s;
            ui.video.play().catch(function () { });
            var caps = track.getCapabilities ? track.getCapabilities() : {};
            ui.torchBtn.hidden = !caps.torch;
            smoothQuad = null; stableFrames = 0; lastFound = null;
            detectTimer = setInterval(detectLoop, 230);
        }).catch(function () {
            cameraUnavailable(t("camera-denied", "No access to the camera. Allow it in the browser."));
        });
    }

    function stopCamera() {
        if (detectTimer) { clearInterval(detectTimer); detectTimer = null; }
        if (stream) { stream.getTracks().forEach(function (tr) { tr.stop(); }); }
        stream = null; track = null; torchOn = false;
        if (ui.video) { ui.video.srcObject = null; }
        if (ui.torchBtn) { ui.torchBtn.classList.remove("is-on"); }
    }

    function cameraUnavailable(text) {
        ui.message.textContent = text;
        ui.message.hidden = false;
    }

    function toggleTorch() {
        if (!track) { return; }
        torchOn = !torchOn;
        track.applyConstraints({ advanced: [{ torch: torchOn }] }).catch(function () { });
        ui.torchBtn.classList.toggle("is-on", torchOn);
    }

    function toggleAuto() {
        autoMode = !autoMode;
        ui.autoBtn.classList.toggle("is-off", !autoMode);
        stableFrames = 0;
        flashHint(autoMode ? t("auto-on", "Auto: takes the photo when the page lies still") : t("auto-off", "Auto off: tap the shutter"));
    }

    function updateSens() {
        var names = [t("sens-low", "Low"), t("sens-mid", "Medium"), t("sens-high", "High")];
        ui.sensLabel.textContent = names[sensitivity];
        ui.sensBtn.setAttribute("aria-label", t("sensitivity", "Sensitivity") + ": " + names[sensitivity]);
    }

    // Low = only a clear sheet counts; high = finds more, but may take a reflection for a page.
    function cycleSensitivity() {
        sensitivity = (sensitivity + 1) % 3;
        try { localStorage.setItem(SENS_KEY, String(sensitivity)); } catch (e) { }
        updateSens();
        smoothQuad = null; stableFrames = 0; lastFound = null;
        flashHint([t("sens-low-hint", "Low: only a clearly visible page"), t("sens-mid-hint", "Medium"),
            t("sens-high-hint", "High: finds more, but may mistake reflections for the page")][sensitivity]);
    }

    var hintTimer = null, hintLocked = false;
    function flashHint(text) {
        ui.hint.textContent = text;
        hintLocked = true;
        clearTimeout(hintTimer);
        hintTimer = setTimeout(function () { hintLocked = false; }, 2200);
    }

    // Maps a point of the video frame (0..1) to overlay pixels; the video is letterboxed (object-fit: contain).
    function frameBox() {
        var cw = ui.overlay.clientWidth, ch = ui.overlay.clientHeight;
        var vw = ui.video.videoWidth || 16, vh = ui.video.videoHeight || 9;
        var scale = Math.min(cw / vw, ch / vh), w = vw * scale, h = vh * scale;
        return { x: (cw - w) / 2, y: (ch - h) / 2, w: w, h: h };
    }

    function detectLoop() {
        if (view !== "camera" || ui.video.readyState < 2 || !ui.video.videoWidth) { return; }
        var found = null;
        try { found = core.detect(ui.video, { sensitivity: sensitivity }); } catch (e) { found = null; }

        var cw = ui.overlay.clientWidth, ch = ui.overlay.clientHeight;
        if (ui.overlay.width !== cw * 2) { ui.overlay.width = cw * 2; ui.overlay.height = ch * 2; }
        var g = ui.overlay.getContext("2d");
        g.setTransform(2, 0, 0, 2, 0, 0);
        g.clearRect(0, 0, cw, ch);

        if (!found) {
            smoothQuad = null; stableFrames = 0; lastFound = null;
            if (!hintLocked) { ui.hint.textContent = t("hint", "Hold the camera over the page"); }
            return;
        }

        if (smoothQuad) {
            var moved = 0;
            smoothQuad = smoothQuad.map(function (p, i) {
                var q = found.quad[i];
                moved = Math.max(moved, Math.hypot(q[0] - p[0], q[1] - p[1]));
                return [p[0] * 0.5 + q[0] * 0.5, p[1] * 0.5 + q[1] * 0.5];
            });
            stableFrames = moved < 0.025 ? stableFrames + 1 : 0;
        } else {
            smoothQuad = found.quad; stableFrames = 0;
        }
        lastFound = smoothQuad;

        var box = frameBox(), steady = stableFrames > 2;
        g.lineWidth = 3;
        g.strokeStyle = steady ? "#3ddc84" : "#ffd54a";
        g.fillStyle = steady ? "rgba(61,220,132,0.18)" : "rgba(255,213,74,0.14)";
        g.beginPath();
        smoothQuad.forEach(function (p, i) {
            var x = box.x + p[0] * box.w, y = box.y + p[1] * box.h;
            if (i) { g.lineTo(x, y); } else { g.moveTo(x, y); }
        });
        g.closePath(); g.fill(); g.stroke();

        if (!hintLocked) { ui.hint.textContent = steady && autoMode ? t("hold", "Hold still …") : t("found", "Page found"); }
        if (autoMode && stableFrames >= 7) { stableFrames = 0; capture(true); }
    }

    function capture(automatic) {
        if (ui.video.readyState < 2 || !ui.video.videoWidth) { return; }
        var c = core.makeCanvas(ui.video.videoWidth, ui.video.videoHeight);
        c.getContext("2d", { willReadFrequently: true }).drawImage(ui.video, 0, 0);
        if (automatic && navigator.vibrate) { navigator.vibrate(30); }
        addPage(limit(c), lastFound || defaultQuad());
    }

    // ------------------------------------------------------------------ pages

    function defaultQuad() { return [[0.08, 0.08], [0.92, 0.08], [0.92, 0.92], [0.08, 0.92]]; }

    function limit(canvas) {
        var longest = Math.max(canvas.width, canvas.height);
        if (longest <= SOURCE_SIDE) { return canvas; }
        var s = SOURCE_SIDE / longest, c = core.makeCanvas(canvas.width * s, canvas.height * s);
        c.getContext("2d").drawImage(canvas, 0, 0, c.width, c.height);
        return c;
    }

    function addPage(sourceCanvas, quad) {
        pages.push({ src: sourceCanvas, quad: quad, turns: 0, filter: "enhanced", preview: null, thumb: null });
        current = pages.length - 1;
        stopCamera();
        showReview(current);
    }

    function render(page, side) {
        return core.filter(core.rotate(core.warp(page.src, page.quad, side), page.turns), page.filter);
    }

    function thumbOf(page, side) {
        if (!page.thumb || page.thumbSide !== side) {
            page.thumb = render(page, side);
            page.thumbSide = side;
        }
        return page.thumb;
    }

    function fit(canvas, stage, target) {
        var sw = stage.clientWidth - 16, sh = stage.clientHeight - 16;
        var scale = Math.min(sw / canvas.width, sh / canvas.height);
        target.width = Math.max(1, Math.round(canvas.width * scale));
        target.height = Math.max(1, Math.round(canvas.height * scale));
        target.getContext("2d").drawImage(canvas, 0, 0, target.width, target.height);
        return scale;
    }

    // ------------------------------------------------------------------ review

    function showReview(index) {
        current = index;
        setView("review");
        var page = pages[index];
        ui.revTitle.textContent = t("page-n", "Page {0}").replace("{0}", index + 1) + " / " + pages.length;
        busy(true);
        setTimeout(function () {
            page.preview = render(page, PREVIEW_SIDE);
            page.thumb = null;
            fit(page.preview, ui.reviewStage, ui.reviewCanvas);
            markFilter();
            busy(false);
        }, 20);
    }

    function redrawCurrent() {
        var page = pages[current];
        if (!page) { return; }
        if (view === "review" && page.preview) { fit(page.preview, ui.reviewStage, ui.reviewCanvas); }
        if (view === "adjust") { showAdjust(current); }
    }

    function markFilter() {
        var page = pages[current];
        ui.filterBar.querySelectorAll("[data-filter]").forEach(function (b) {
            b.classList.toggle("is-active", !!page && b.getAttribute("data-filter") === page.filter);
        });
    }

    function setFilter(name) {
        var page = pages[current];
        if (!page) { return; }
        page.filter = name;
        markFilter();
        busy(true);
        setTimeout(function () {
            page.preview = render(page, PREVIEW_SIDE);
            page.thumb = null;
            fit(page.preview, ui.reviewStage, ui.reviewCanvas);
            busy(false);
        }, 10);
    }

    function turnPage(delta) {
        var page = pages[current];
        if (!page) { return; }
        page.turns = (page.turns + delta + 4) % 4;
        showReview(current);
    }

    function retake() {
        pages.splice(current, 1);
        current = -1;
        showCamera();
    }

    // ------------------------------------------------------------------ adjust (corners)

    function showAdjust(index) {
        current = index;
        setView("adjust");
        var page = pages[index];
        fit(page.src, ui.adjustStage, ui.adjustCanvas);
        var w = ui.adjustCanvas.width, h = ui.adjustCanvas.height;
        ui.adjustSvg.setAttribute("viewBox", "0 0 " + w + " " + h);
        ui.adjustSvg.setAttribute("width", w);
        ui.adjustSvg.setAttribute("height", h);
        drawAdjust();
    }

    function drawAdjust() {
        var page = pages[current], svg = ui.adjustSvg, ns = "http://www.w3.org/2000/svg";
        var w = ui.adjustCanvas.width, h = ui.adjustCanvas.height;
        while (svg.firstChild) { svg.removeChild(svg.firstChild); }
        var poly = document.createElementNS(ns, "polygon");
        poly.setAttribute("points", page.quad.map(function (p) { return (p[0] * w) + "," + (p[1] * h); }).join(" "));
        poly.setAttribute("class", "scn__poly");
        svg.appendChild(poly);
        page.quad.forEach(function (p, i) {
            var c = document.createElementNS(ns, "circle");
            c.setAttribute("cx", p[0] * w); c.setAttribute("cy", p[1] * h); c.setAttribute("r", 18);
            c.setAttribute("class", "scn__handle");
            c.addEventListener("pointerdown", function (event) { dragHandle(event, i); });
            svg.appendChild(c);
        });
    }

    function dragHandle(event, index) {
        event.preventDefault();
        var svg = ui.adjustSvg, page = pages[current];
        svg.setPointerCapture(event.pointerId);
        function move(e) {
            var r = svg.getBoundingClientRect();
            var x = Math.min(1, Math.max(0, (e.clientX - r.left) / r.width));
            var y = Math.min(1, Math.max(0, (e.clientY - r.top) / r.height));
            page.quad[index] = [x, y];
            var w = ui.adjustCanvas.width, h = ui.adjustCanvas.height;
            svg.querySelector("polygon").setAttribute("points", page.quad.map(function (p) { return (p[0] * w) + "," + (p[1] * h); }).join(" "));
            var c = svg.querySelectorAll("circle")[index];
            c.setAttribute("cx", x * w); c.setAttribute("cy", y * h);
        }
        function up() {
            svg.removeEventListener("pointermove", move);
            svg.removeEventListener("pointerup", up);
            svg.removeEventListener("pointercancel", up);
        }
        svg.addEventListener("pointermove", move);
        svg.addEventListener("pointerup", up);
        svg.addEventListener("pointercancel", up);
    }

    function redetect() {
        var page = pages[current], det = null;
        try { det = core.detect(page.src, { sensitivity: sensitivity }); } catch (e) { det = null; }
        page.quad = det ? det.quad : defaultQuad();
        drawAdjust();
    }

    // Turns the photo itself (page shot sideways); the corners turn with it.
    function turnSource(delta) {
        var page = pages[current];
        page.src = core.rotate(page.src, delta);
        page.quad = page.quad.map(function (p) { return delta > 0 ? [1 - p[1], p[0]] : [p[1], 1 - p[0]]; });
        showAdjust(current);
    }

    // ------------------------------------------------------------------ overview

    function showPages() {
        stopCamera();
        if (!pages.length) { showCamera(); return; }
        setView("pages");
        ui.grid.innerHTML = "";
        pages.forEach(function (page, i) {
            var cell = el("div", "scn__cell");
            var th = thumbOf(page, 360);
            var img = el("canvas", "scn__thumb");
            img.width = th.width; img.height = th.height;
            img.getContext("2d").drawImage(th, 0, 0);
            img.addEventListener("click", function () { showReview(i); });
            var tools = el("div", "scn__cell-tools");
            var left = btn("scn__mini", "left", "", function () { moveBy(i, -1); }, t("move-left", "Move left"));
            var del = btn("scn__mini", "trash", "", function () { pages.splice(i, 1); if (pages.length) { showPages(); } else { showCamera(); } }, t("delete", "Delete"));
            var right = btn("scn__mini", "right", "", function () { moveBy(i, 1); }, t("move-right", "Move right"));
            left.disabled = i === 0; right.disabled = i === pages.length - 1;
            tools.append(left, del, right);
            cell.append(img, el("span", "scn__num", String(i + 1)), tools);
            ui.grid.appendChild(cell);
        });
    }

    function moveBy(i, delta) {
        var j = i + delta;
        if (j < 0 || j >= pages.length) { return; }
        var tmp = pages[i]; pages[i] = pages[j]; pages[j] = tmp;
        showPages();
    }

    // ------------------------------------------------------------------ finish

    function canvasToFile(canvas, name) {
        return new Promise(function (resolve) {
            canvas.toBlob(function (blob) { resolve(new File([blob], name, { type: "image/jpeg" })); }, "image/jpeg", 0.88);
        });
    }

    function finish() {
        stopCamera();
        if (!pages.length) { hide(); if (onDone) { onDone([], []); } return; }
        busy(true);
        var files = [], chain = Promise.resolve();
        pages.forEach(function (page, i) {
            chain = chain.then(function () {
                return new Promise(function (resolve) {
                    setTimeout(function () {
                        canvasToFile(render(page, FINAL_SIDE), "scan-" + (i + 1) + ".jpg").then(function (f) { files.push(f); resolve(); });
                    }, 10);
                });
            });
        });
        chain.then(function () {
            busy(false);
            hide();
            if (onDone) { onDone(files, pages.map(function (p) { return thumbOf(p, 240); })); }
        });
    }

    function reset() { pages = []; current = -1; }

    window.MatPaperScanner = {
        open: open,
        openPages: function (options) { open({ onDone: options && options.onDone, pages: true }); },
        reset: reset,
        count: function () { return pages.length; }
    };
})();
