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
    var noteTimer = null, adjustTimer = null;
    var sharpScratch = null, sharpHistory = [], sharpWaited = 0;   // how crisp the page was in the last pictures (auto release waits for a crisp one)
    var missed = 0, lastArea = 0, lastReach = 0, lastMargin = 0, lastCut = false;   // detections in a row without a page / size and confidence of the last one
    var sensitivity = 1;
    var MODE_KEY = "matpaper.scan.mode";
    var mode = "live"; // "live" = the in-page camera, "native" = the phone's own camera app (better optics, autofocus)
    try { if (localStorage.getItem(MODE_KEY) === "native") { mode = "native"; } } catch (e) { }
    try { var saved = parseInt(localStorage.getItem(SENS_KEY), 10); if (saved >= 0 && saved <= 2) { sensitivity = saved; } } catch (e) { }

    var FILTERS = [["enhanced", "Enhanced"], ["color", "Original"], ["gray", "Gray"], ["bw", "B/W"]];

    // ------------------------------------------------------------------ icons (the sprite of the page, MatPaper.icon)

    var SPRITE = {
        close: "x", back: "back", torch: "flashlight", sliders: "sliders", auto: "sparkles", rotateCw: "rotate-cw", rotateCcw: "rotate-ccw",
        crop: "crop", camera: "camera", plus: "plus", check: "check", trash: "trash", left: "chevron-left", right: "chevron-right",
        live: "video", native: "smartphone", image: "image", detect: "scan"
    };

    function icon(name) {
        var holder = document.createElement("span");
        holder.innerHTML = window.MatPaper.icon(SPRITE[name] || name);
        var svg = holder.firstChild;
        svg.classList.add("mp-mp-scn__icon");
        return svg;
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
        if (label) { b.appendChild(el("span", "mp-scn__label", label)); }
        if (aria || label) { b.setAttribute("aria-label", aria || label); }
        b.addEventListener("click", action);
        return b;
    }

    function build() {
        root = el("div", "mp-scn");
        root.setAttribute("role", "dialog");
        root.setAttribute("aria-label", t("title", "Scanner"));

        // ---------------- camera
        var cam = el("div", "mp-scn__view");
        var camTop = el("div", "mp-scn__top");
        ui.closeBtn = btn("mp-scn__tool", "close", "", close, t("close", "Close"));
        ui.camTitle = el("div", "mp-scn__heading", t("title", "Scanner"));
        ui.modeBtn = btn("mp-scn__tool", "live", "", toggleMode, t("mode", "Camera mode"));
        ui.sensBtn = btn("mp-scn__tool mp-scn__tool--text", "sliders", "", cycleSensitivity, t("sensitivity", "Sensitivity"));
        ui.sensLabel = el("span", "mp-scn__label");
        ui.sensBtn.appendChild(ui.sensLabel);
        ui.torchBtn = btn("mp-scn__tool", "torch", "", toggleTorch, t("torch", "Light"));
        ui.torchBtn.hidden = true;
        ui.autoBtn = btn("mp-scn__tool mp-scn__tool--text", "auto", t("auto", "Auto"), toggleAuto);
        camTop.append(ui.closeBtn, ui.camTitle, ui.modeBtn, ui.sensBtn, ui.torchBtn, ui.autoBtn);

        ui.stage = el("div", "mp-scn__stage mp-scn__stage--camera");
        ui.video = el("video", "mp-scn__video");
        ui.video.setAttribute("playsinline", "");
        ui.video.muted = true;
        ui.overlay = el("canvas", "mp-scn__overlay");
        ui.hint = el("div", "mp-scn__hint", t("hint", "Hold the camera over the page"));
        ui.message = el("div", "mp-scn__message");
        ui.message.hidden = true;
        ui.nativePanel = el("div", "mp-scn__native");
        ui.nativePanel.append(
            icon("native"),
            el("p", "mp-scn__native-text", t("native-text", "The photo is taken with the camera app of your device - usually sharper, with autofocus.")),
            btn("mp-scn__action mp-scn__action--primary mp-scn__native-btn", "camera", t("native-shoot", "Take photo"), function () { ui.nativeInput.click(); }),
            btn("mp-scn__action mp-scn__native-btn", "image", t("native-pick", "Choose photos"), function () { ui.pickInput.click(); }));
        ui.nativeInput = el("input");
        ui.nativeInput.type = "file"; ui.nativeInput.accept = "image/*"; ui.nativeInput.setAttribute("capture", "environment"); ui.nativeInput.hidden = true;
        ui.pickInput = el("input");
        ui.pickInput.type = "file"; ui.pickInput.accept = "image/*"; ui.pickInput.multiple = true; ui.pickInput.hidden = true;
        [ui.nativeInput, ui.pickInput].forEach(function (inp) {
            inp.addEventListener("change", function () { addFiles(inp.files); inp.value = ""; });
        });
        ui.stage.append(ui.video, ui.overlay, ui.nativePanel, ui.hint, ui.message, ui.nativeInput, ui.pickInput);

        var camBottom = el("div", "mp-scn__bottom mp-scn__bottom--camera");
        ui.pagesBtn = btn("mp-scn__pagesbtn", "", "", showPages, t("pages", "Pages"));
        ui.pagesThumb = el("canvas", "mp-scn__pagesthumb");
        ui.pagesCount = el("span", "mp-scn__badge");
        ui.pagesBtn.append(ui.pagesThumb, ui.pagesCount);
        ui.shutter = btn("mp-scn__shutter", "", "", function () { if (mode === "native") { ui.nativeInput.click(); } else { capture(false); } }, t("shoot", "Take photo"));
        ui.shutter.appendChild(el("span", "mp-scn__shutter-ring"));
        ui.finishBtn = btn("mp-scn__action mp-scn__action--primary", "check", t("finish", "Done"), finish);
        camBottom.append(ui.pagesBtn, ui.shutter, ui.finishBtn);
        cam.append(camTop, ui.stage, camBottom);
        ui.cameraView = cam;

        // ---------------- review
        var rev = el("div", "mp-scn__view");
        var revTop = el("div", "mp-scn__top");
        ui.revBack = btn("mp-scn__tool", "back", "", showCamera, t("camera", "Camera"));
        ui.revTitle = el("div", "mp-scn__heading");
        revTop.append(ui.revBack, ui.revTitle, btn("mp-scn__tool", "rotateCw", "", function () { turnPage(1); }, t("rotate", "Rotate")));
        ui.reviewStage = el("div", "mp-scn__stage");
        ui.reviewCanvas = el("canvas", "mp-scn__page");
        ui.reviewNote = el("div", "mp-scn__hint mp-scn__hint--note");
        ui.reviewNote.hidden = true;
        ui.reviewStage.append(ui.reviewCanvas, ui.reviewNote);
        ui.filterBar = el("div", "mp-scn__filters");
        FILTERS.forEach(function (f) {
            var b = el("button", "mp-scn__chip", t("f-" + f[0], f[1]));
            b.type = "button";
            b.setAttribute("data-filter", f[0]);
            b.addEventListener("click", function () { setFilter(f[0]); });
            ui.filterBar.appendChild(b);
        });
        var revBottom = el("div", "mp-scn__bottom mp-scn__bottom--actions");
        revBottom.append(
            btn("mp-scn__action", "camera", t("retake", "Retake"), retake),
            btn("mp-scn__action", "crop", t("crop", "Crop"), function () { showAdjust(current); }),
            btn("mp-scn__action", "plus", t("add-page", "Page"), showCamera),
            btn("mp-scn__action mp-scn__action--primary", "check", t("finish", "Done"), finish));
        rev.append(revTop, ui.reviewStage, ui.filterBar, revBottom);
        ui.reviewView = rev;

        // ---------------- adjust corners
        var adj = el("div", "mp-scn__view");
        var adjTop = el("div", "mp-scn__top");
        adjTop.append(btn("mp-scn__tool", "back", "", function () { showReview(current); }, t("back", "Back")),
            ui.adjustTitle = el("div", "mp-scn__heading", t("adjust", "Drag the corners onto the page")));
        ui.adjustStage = el("div", "mp-scn__stage");
        ui.adjustWrap = el("div", "mp-scn__adjust-wrap");
        ui.adjustCanvas = el("canvas", "mp-scn__page");
        ui.adjustSvg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
        ui.adjustSvg.setAttribute("class", "mp-scn__adjust-svg");
        ui.adjustWrap.append(ui.adjustCanvas, ui.adjustSvg);
        ui.adjustStage.appendChild(ui.adjustWrap);
        var adjBottom = el("div", "mp-scn__bottom mp-scn__bottom--actions");
        adjBottom.append(
            btn("mp-scn__action", "detect", t("auto-corners", "Detect again"), redetect),
            btn("mp-scn__action", "rotateCcw", t("turn-left", "Left"), function () { turnSource(-1); }),
            btn("mp-scn__action", "rotateCw", t("turn-right", "Right"), function () { turnSource(1); }),
            btn("mp-scn__action mp-scn__action--primary", "check", t("apply", "Apply"), function () { showReview(current); }));
        adj.append(adjTop, ui.adjustStage, adjBottom);
        ui.adjustView = adj;

        // ---------------- pages overview
        var pg = el("div", "mp-scn__view");
        var pgTop = el("div", "mp-scn__top");
        pgTop.append(btn("mp-scn__tool", "back", "", showCamera, t("camera", "Camera")), el("div", "mp-scn__heading", t("pages", "Pages")));
        ui.grid = el("div", "mp-scn__grid");
        var pgBottom = el("div", "mp-scn__bottom mp-scn__bottom--actions");
        pgBottom.append(
            btn("mp-scn__action", "plus", t("add-page", "Page"), showCamera),
            btn("mp-scn__action mp-scn__action--primary", "check", t("finish", "Done"), finish));
        pg.append(pgTop, ui.grid, pgBottom);
        ui.pagesView = pg;

        ui.busy = el("div", "mp-scn__busy", t("working", "Working …"));
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

    // The status bar belongs to the scanner while it is open and goes back to the app when it closes. A phone
    // paints the status bar from two places: the browser colour (theme-color) and the colour of the root element.
    // Both are black while the scanner is open - the root through html.mp-has-scanner in the style sheet - and both
    // are put back on the way out, with a repaint, so nothing dark is left behind the status bar afterwards.
    var pageThemeColor = null;

    function themeColor(color) {
        // a new tag, not a changed attribute: some browsers only look again when the tag is inserted
        var old = document.querySelector('meta[name="theme-color"]');
        var meta = document.createElement("meta");
        meta.setAttribute("name", "theme-color");
        meta.setAttribute("content", color);
        if (old && old.parentNode) { old.parentNode.replaceChild(meta, old); } else { document.head.appendChild(meta); }
    }

    function barColor(scanning) {
        var html = document.documentElement;
        if (scanning) {
            var current = document.querySelector('meta[name="theme-color"]');
            pageThemeColor = current ? current.getAttribute("content") : null;
            html.classList.add("mp-has-scanner");
            themeColor("#000000");
        } else {
            html.classList.remove("mp-has-scanner");
            if (pageThemeColor) { themeColor(pageThemeColor); }
            html.style.backgroundColor = "var(--mat-bg)";
            void html.offsetHeight;
            html.style.removeProperty("background-color");
        }
        void html.offsetHeight;
    }

    function open(options) {
        onDone = (options && options.onDone) || onDone;
        if (!root) { build(); }
        root.hidden = false;
        barColor(true);
        if (options && options.pages && pages.length) { showPages(); } else { showCamera(); }
    }

    function hide() {
        stopCamera();
        if (root) { root.hidden = true; }
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
                    try { det = core.detect(canvas, { sensitivity: sensitivity, precise: true }); } catch (e) { det = null; }
                    pages.push({ src: canvas, quad: det ? det.quad : defaultQuad(), note: !det ? "guess" : (det.cut ? "cut" : ""), turns: 0, filter: "enhanced", preview: null, thumb: null });
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
            smoothQuad = null; stableFrames = 0; lastFound = null; missed = 0;
            detectTimer = setTimeout(detectLoop, 200);
        }).catch(function () {
            cameraUnavailable(t("camera-denied", "No access to the camera. Allow it in the browser."));
        });
    }

    function stopCamera() {
        if (detectTimer) { clearTimeout(detectTimer); detectTimer = null; }
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

    // One analysis after the other: the next one is planned when the last one is done, so a slow phone does not queue them up.
    function detectLoop() {
        detectTimer = null;
        if (!stream) { return; }
        var started = performance.now();
        try { analyse(); } catch (e) { }
        if (stream) { detectTimer = setTimeout(detectLoop, Math.max(80, 190 - (performance.now() - started))); }
    }

    // The corners come back in a fixed order (top left first), but a sheet turned about 45 degrees can change which corner
    // counts as the first one: line them up with the corners of the previous picture.
    function alignQuad(prev, quad) {
        var best = quad, bestCost = Infinity;
        for (var r = 0; r < 4; r++) {
            var cand = [quad[r], quad[(r + 1) % 4], quad[(r + 2) % 4], quad[(r + 3) % 4]], cost = 0;
            for (var i = 0; i < 4; i++) { cost += Math.hypot(cand[i][0] - prev[i][0], cand[i][1] - prev[i][1]); }
            if (cost < bestCost) { bestCost = cost; best = cand; }
        }
        return best;
    }

    // How crisp the page is right now: the mean strength of the fine detail (Laplacian) in a window in the middle of the page, read at
    // the camera's own resolution. Only a comparison between pictures of the same page means something.
    function sharpness(quad) {
        var vw = ui.video.videoWidth, vh = ui.video.videoHeight, cx = 0, cy = 0, side = Math.min(320, Math.round(Math.min(vw, vh) * 0.45));
        quad.forEach(function (p) { cx += p[0] / 4; cy += p[1] / 4; });
        if (!sharpScratch || sharpScratch.width !== side) { sharpScratch = core.makeCanvas(side, side); }
        var x = sharpScratch.getContext("2d", { willReadFrequently: true });
        x.drawImage(ui.video, Math.max(0, Math.min(vw - side, Math.round(cx * vw - side / 2))), Math.max(0, Math.min(vh - side, Math.round(cy * vh - side / 2))), side, side, 0, 0, side, side);
        var d = x.getImageData(0, 0, side, side).data, g = new Float32Array(side * side), i, j, sum = 0;
        for (i = 0, j = 0; i < g.length; i++, j += 4) { g[i] = 0.299 * d[j] + 0.587 * d[j + 1] + 0.114 * d[j + 2]; }
        for (var yy = 1; yy < side - 1; yy++) {
            for (var xx = 1; xx < side - 1; xx++) { i = yy * side + xx; sum += Math.abs(4 * g[i] - g[i - 1] - g[i + 1] - g[i - side] - g[i + side]); }
        }
        return sum / ((side - 2) * (side - 2));
    }

    function analyse() {
        if (view !== "camera" || ui.video.readyState < 2 || !ui.video.videoWidth) { return; }
        var found = null;
        // the page found a moment ago is the hint: the outline stays on the same sheet instead of jumping between two
        try { found = core.detect(ui.video, { sensitivity: sensitivity, hint: smoothQuad }); } catch (e) { found = null; }

        var cw = ui.overlay.clientWidth, ch = ui.overlay.clientHeight;
        if (ui.overlay.width !== cw * 2) { ui.overlay.width = cw * 2; ui.overlay.height = ch * 2; }
        var g = ui.overlay.getContext("2d");
        g.setTransform(2, 0, 0, 2, 0, 0);
        g.clearRect(0, 0, cw, ch);

        var held = false;
        if (!found) {
            // a page that was there a moment ago stays for two more analyses: a hand or a reflection must not make the outline flicker
            if (smoothQuad && missed < 2) { missed++; held = true; stableFrames = 0; sharpHistory.length = 0; sharpWaited = 0; }
            else {
                smoothQuad = null; stableFrames = 0; lastFound = null; missed = 0; sharpHistory.length = 0; sharpWaited = 0;
                if (!hintLocked) { ui.hint.textContent = t("hint", "Hold the camera over the page"); }
                return;
            }
        } else {
            missed = 0;
            if (smoothQuad) {
                var next = alignQuad(smoothQuad, found.quad), moved = 0;
                next.forEach(function (q, i) { moved = Math.max(moved, Math.hypot(q[0] - smoothQuad[i][0], q[1] - smoothQuad[i][1])); });
                // follow a page that moves quickly, keep one that lies still calm
                var a = moved > 0.08 ? 0.85 : (moved > 0.03 ? 0.6 : 0.35);
                smoothQuad = smoothQuad.map(function (p, i) { return [p[0] + (next[i][0] - p[0]) * a, p[1] + (next[i][1] - p[1]) * a]; });
                stableFrames = moved < 0.02 ? stableFrames + 1 : 0;
                if (!stableFrames) { sharpHistory.length = 0; sharpWaited = 0; }
            } else {
                smoothQuad = found.quad; stableFrames = 0; sharpHistory.length = 0; sharpWaited = 0;
            }
            lastFound = smoothQuad; lastArea = found.area; lastMargin = found.margin; lastCut = !!found.cut;
            // how far the longest side reaches across the picture (a receipt is long and narrow: little area, but close enough)
            var vw = ui.video.videoWidth, vh = ui.video.videoHeight, diag = Math.hypot(vw, vh), longest = 0;
            for (var k = 0; k < 4; k++) { var p0 = smoothQuad[k], p1 = smoothQuad[(k + 1) % 4]; longest = Math.max(longest, Math.hypot((p1[0] - p0[0]) * vw, (p1[1] - p0[1]) * vh)); }
            lastReach = longest / diag;
        }

        var box = frameBox(), steady = stableFrames > 2;
        g.lineWidth = 3;
        if (held) { g.setLineDash([9, 7]); g.strokeStyle = "rgba(255,213,74,0.7)"; g.fillStyle = "rgba(255,213,74,0.06)"; }
        else if (lastCut) { g.strokeStyle = "#ff9f43"; g.fillStyle = "rgba(255,159,67,0.14)"; }
        else { g.strokeStyle = steady ? "#3ddc84" : "#ffd54a"; g.fillStyle = steady ? "rgba(61,220,132,0.18)" : "rgba(255,213,74,0.14)"; }
        g.beginPath();
        smoothQuad.forEach(function (p, i) {
            var x = box.x + p[0] * box.w, y = box.y + p[1] * box.h;
            if (i) { g.lineTo(x, y); } else { g.moveTo(x, y); }
        });
        g.closePath(); g.fill(); g.stroke(); g.setLineDash([]);

        // a page that is far away is not worth a photo: it would be small and soft
        var near = lastArea >= 0.16 || lastReach >= 0.38;
        if (!hintLocked && !held) {
            ui.hint.textContent = lastCut ? t("cut-off", "The page is cut off - move back") : (autoMode && !near ? t("closer", "Move closer") : (steady && autoMode ? t("hold", "Hold still …") : t("found", "Page found")));
        }
        // a page that is cut off by the edge of the picture is not worth a photo either
        if (autoMode && !held && near && !lastCut && lastMargin >= 1 && stableFrames >= 3) {
            // the picture of the moment must be about as crisp as the best of the last ones (a hand shakes, the focus hunts); but not for ever
            sharpHistory.push(sharpness(smoothQuad));
            if (sharpHistory.length > 8) { sharpHistory.shift(); }
            if (stableFrames >= 6) {
                var now = sharpHistory[sharpHistory.length - 1], best = Math.max.apply(null, sharpHistory);
                if (now >= 0.82 * best || sharpWaited >= 5) { stableFrames = 0; sharpHistory.length = 0; sharpWaited = 0; capture(true); } else { sharpWaited++; }
            }
        }
    }

    function capture(automatic) {
        if (ui.video.readyState < 2 || !ui.video.videoWidth) { return; }
        var c = core.makeCanvas(ui.video.videoWidth, ui.video.videoHeight);
        c.getContext("2d", { willReadFrequently: true }).drawImage(ui.video, 0, 0);
        if (automatic && navigator.vibrate) { navigator.vibrate(30); }
        var canvas = limit(c), quad = lastFound;
        // the outline came from a small copy of the picture; the frame in full size places the corners more exactly
        try {
            var again = core.detect(canvas, { sensitivity: sensitivity, hint: quad, precise: true });
            if (again && (!quad || core.overlap(again.quad, quad) > 0.8)) { quad = again.quad; }
        } catch (e) { }
        addPage(canvas, quad || defaultQuad());
    }

    // ------------------------------------------------------------------ pages

    // Nothing found: the whole picture, a hair smaller (it is the safe choice - nothing of the page is cut off)
    function defaultQuad() { return [[0.025, 0.025], [0.975, 0.025], [0.975, 0.975], [0.025, 0.975]]; }

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
        // a photo in which no page was found is used as it is; say so once, the corners can be set under Crop
        clearTimeout(noteTimer);
        ui.reviewNote.hidden = !page.note;
        if (page.note) {
            ui.reviewNote.textContent = page.note === "cut" ? t("cut-note", "The page is cut off at the edge of the photo - retake it with the whole page in view.") : t("no-page-note", "No page was found - the whole photo is used. Under Crop the corners can be set.");
            noteTimer = setTimeout(function () { ui.reviewNote.hidden = true; }, 6500);
            page.note = "";
        }
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
        poly.setAttribute("class", "mp-scn__poly");
        svg.appendChild(poly);
        page.quad.forEach(function (p, i) {
            var c = document.createElementNS(ns, "circle");
            c.setAttribute("cx", p[0] * w); c.setAttribute("cy", p[1] * h); c.setAttribute("r", 18);
            c.setAttribute("class", "mp-scn__handle");
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
        // asked for by hand, so a second, more generous try is welcome
        try { det = core.detect(page.src, { sensitivity: sensitivity, precise: true }) || core.detect(page.src, { sensitivity: 2, precise: true }); } catch (e) { det = null; }
        if (det) { page.quad = det.quad; page.note = ""; drawAdjust(); return; }
        ui.adjustTitle.textContent = t("no-page", "No page found - drag the corners");
        clearTimeout(adjustTimer);
        adjustTimer = setTimeout(function () { ui.adjustTitle.textContent = t("adjust", "Drag the corners onto the page"); }, 2800);
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
            var cell = el("div", "mp-scn__cell");
            var th = thumbOf(page, 360);
            var img = el("canvas", "mp-scn__thumb");
            img.width = th.width; img.height = th.height;
            img.getContext("2d").drawImage(th, 0, 0);
            img.addEventListener("click", function () { showReview(i); });
            var tools = el("div", "mp-scn__cell-tools");
            var left = btn("mp-scn__mini", "left", "", function () { moveBy(i, -1); }, t("move-left", "Move left"));
            var del = btn("mp-scn__mini", "trash", "", function () { pages.splice(i, 1); if (pages.length) { showPages(); } else { showCamera(); } }, t("delete", "Delete"));
            var right = btn("mp-scn__mini", "right", "", function () { moveBy(i, 1); }, t("move-right", "Move right"));
            left.disabled = i === 0; right.disabled = i === pages.length - 1;
            tools.append(left, del, right);
            cell.append(img, el("span", "mp-scn__num", String(i + 1)), tools);
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
