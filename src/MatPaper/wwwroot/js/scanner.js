// Document scanner UI (camera -> automatic crop -> filter -> several pages -> PDF).
// Uses scanner-core.js for the image work. Full-screen on a phone, works the same on a desktop with a webcam.
//
// The scan page (Pages/Documents/Scan.cshtml) holds the form; this script fills its "Images" file input with
// the finished pages (JPEG) and sets "Prepared", so the normal form post builds the PDF.
(function () {
    "use strict";

    var core = window.MatPaperScan;
    var launch = document.getElementById("scanner-launch");
    var form = document.getElementById("scan-form");
    if (!core || !launch || !form) { return; }

    var cfg = document.getElementById("scanner-config");
    function t(key, fallback) { return (cfg && cfg.getAttribute("data-t-" + key)) || fallback; }

    var PREVIEW_SIDE = 1100;   // review preview
    var FINAL_SIDE = 2200;     // what ends up in the PDF
    var SOURCE_SIDE = 2600;    // photos are scaled down to this before any work

    var pages = [];            // { src, quad, turns, filter, preview, thumb }
    var current = -1;          // page being reviewed/adjusted
    var root = null, ui = {};
    var stream = null, track = null, detectTimer = null;
    var smoothQuad = null, stableFrames = 0, lastFound = null, autoMode = true, torchOn = false;
    var view = "camera";

    var FILTERS = [
        ["enhanced", "Enhanced"],
        ["color", "Original"],
        ["gray", "Gray"],
        ["bw", "B/W"]
    ];

    // ------------------------------------------------------------------ DOM

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) { e.className = cls; }
        if (text) { e.textContent = text; }
        return e;
    }

    function button(cls, label, action) {
        var b = el("button", cls, label);
        b.type = "button";
        b.addEventListener("click", action);
        return b;
    }

    function build() {
        root = el("div", "scn");
        root.setAttribute("role", "dialog");
        root.setAttribute("aria-label", t("title", "Scanner"));

        // ---- camera view
        var cam = el("div", "scn__view scn__camera");
        ui.video = el("video", "scn__video");
        ui.video.setAttribute("playsinline", "");
        ui.video.muted = true;
        ui.overlay = el("canvas", "scn__overlay");
        ui.hint = el("div", "scn__hint", t("hint", "Hold the camera over the page"));
        ui.cameraBar = el("div", "scn__bar scn__bar--top");
        ui.closeBtn = button("scn__btn", "✕", function () { if (pages.length) { finish(); } else { closeScanner(); } });
        ui.closeBtn.setAttribute("aria-label", t("close", "Close"));
        ui.torchBtn = button("scn__btn", "☀", toggleTorch);
        ui.torchBtn.setAttribute("aria-label", t("torch", "Light"));
        ui.torchBtn.hidden = true;
        ui.autoBtn = button("scn__btn scn__btn--text", t("auto", "Auto") + " ✓", toggleAuto);
        ui.cameraBar.append(ui.closeBtn, el("span", "scn__spacer"), ui.torchBtn, ui.autoBtn);
        ui.cameraControls = el("div", "scn__bar scn__bar--bottom");
        ui.galleryBtn = button("scn__btn scn__btn--text", t("gallery", "Gallery"), function () { ui.file.click(); });
        ui.shutter = button("scn__shutter", "", function () { capture(false); });
        ui.shutter.setAttribute("aria-label", t("shoot", "Take photo"));
        ui.pagesBtn = button("scn__btn scn__btn--text", "", showPages);
        ui.cameraControls.append(ui.galleryBtn, ui.shutter, ui.pagesBtn);
        ui.message = el("div", "scn__message");
        ui.message.hidden = true;
        cam.append(ui.video, ui.overlay, ui.hint, ui.cameraBar, ui.cameraControls, ui.message);
        ui.cameraView = cam;

        ui.file = el("input");
        ui.file.type = "file";
        ui.file.accept = "image/*";
        ui.file.multiple = true;
        ui.file.hidden = true;
        ui.file.addEventListener("change", function () { addFiles(ui.file.files); ui.file.value = ""; });

        // ---- adjust view (drag the four corners)
        var adj = el("div", "scn__view scn__adjust");
        ui.adjustStage = el("div", "scn__stage");
        ui.adjustCanvas = el("canvas", "scn__adjust-canvas");
        ui.adjustSvg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
        ui.adjustSvg.setAttribute("class", "scn__adjust-svg");
        ui.adjustStage.append(ui.adjustCanvas, ui.adjustSvg);
        var adjBar = el("div", "scn__bar scn__bar--bottom");
        adjBar.append(
            button("scn__btn scn__btn--text", t("auto-corners", "Detect again"), redetect),
            button("scn__btn scn__btn--text", "⟲", function () { turnSource(-1); }),
            button("scn__btn scn__btn--text", "⟳", function () { turnSource(1); }),
            button("scn__btn scn__btn--primary", t("apply", "Done"), applyAdjust));
        adj.append(el("div", "scn__title", t("adjust", "Drag the corners onto the page")), ui.adjustStage, adjBar);
        ui.adjustView = adj;

        // ---- review view
        var rev = el("div", "scn__view scn__review");
        ui.reviewStage = el("div", "scn__stage");
        ui.reviewCanvas = el("canvas", "scn__review-canvas");
        ui.reviewStage.append(ui.reviewCanvas);
        ui.filterBar = el("div", "scn__filters");
        FILTERS.forEach(function (f) {
            var b = button("scn__chip", t("f-" + f[0], f[1]), function () { setFilter(f[0]); });
            b.setAttribute("data-filter", f[0]);
            ui.filterBar.appendChild(b);
        });
        var revBar = el("div", "scn__bar scn__bar--bottom scn__bar--wrap");
        ui.retakeBtn = button("scn__btn scn__btn--text", t("retake", "Retake"), retake);
        ui.cropBtn = button("scn__btn scn__btn--text", t("crop", "Crop"), function () { showAdjust(current); });
        ui.turnBtn = button("scn__btn scn__btn--text", "⟳", function () { turnPage(1); });
        ui.nextBtn = button("scn__btn scn__btn--text", t("add-page", "+ Page"), nextPage);
        ui.doneBtn = button("scn__btn scn__btn--primary", t("finish", "Done"), finish);
        revBar.append(ui.retakeBtn, ui.cropBtn, ui.turnBtn, ui.nextBtn, ui.doneBtn);
        rev.append(ui.reviewStage, ui.filterBar, revBar);
        ui.reviewView = rev;

        // ---- pages view
        var pg = el("div", "scn__view scn__pages");
        pg.append(el("div", "scn__title", t("pages", "Pages")));
        ui.grid = el("div", "scn__grid");
        var pgBar = el("div", "scn__bar scn__bar--bottom");
        pgBar.append(
            button("scn__btn scn__btn--text", t("add-page", "+ Page"), showCamera),
            button("scn__btn scn__btn--primary", t("finish", "Done"), finish));
        pg.append(ui.grid, pgBar);
        ui.pagesView = pg;

        ui.busy = el("div", "scn__busy", t("working", "Working …"));
        ui.busy.hidden = true;

        root.append(cam, adj, rev, pg, ui.file, ui.busy);
        document.body.appendChild(root);
        window.addEventListener("resize", function () { if (root && !root.hidden) { redrawCurrent(); } });
    }

    function setView(name) {
        view = name;
        ui.cameraView.hidden = name !== "camera";
        ui.adjustView.hidden = name !== "adjust";
        ui.reviewView.hidden = name !== "review";
        ui.pagesView.hidden = name !== "pages";
    }

    function busy(on) { ui.busy.hidden = !on; }

    // ------------------------------------------------------------------ open / close

    function openScanner(files) {
        if (!root) { build(); }
        root.hidden = false;
        document.documentElement.classList.add("has-scanner");
        updatePagesBtn();
        if (files && files.length) {
            showCamera(true); // sets the view; camera stays off until needed
            stopCamera();
            addFiles(files);
        } else {
            showCamera();
        }
    }

    function closeScanner() {
        stopCamera();
        if (root) { root.hidden = true; }
        document.documentElement.classList.remove("has-scanner");
        summarize();
    }

    // ------------------------------------------------------------------ camera

    function showCamera(skipStart) {
        setView("camera");
        updatePagesBtn();
        if (!skipStart) { startCamera(); }
    }

    function updatePagesBtn() {
        ui.pagesBtn.textContent = pages.length ? "▦ " + pages.length : "";
        ui.pagesBtn.hidden = pages.length === 0;
    }

    function startCamera() {
        if (stream) { return; }
        ui.message.hidden = true;
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            return cameraUnavailable(t("no-camera", "The camera is not available here (it needs HTTPS). Use the gallery instead."));
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
            cameraUnavailable(t("camera-denied", "No access to the camera. Allow it in the browser, or use the gallery."));
        });
    }

    function stopCamera() {
        if (detectTimer) { clearInterval(detectTimer); detectTimer = null; }
        if (stream) { stream.getTracks().forEach(function (tr) { tr.stop(); }); }
        stream = null; track = null;
        torchOn = false;
        ui.video.srcObject = null;
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
        ui.autoBtn.textContent = t("auto", "Auto") + (autoMode ? " ✓" : "");
        stableFrames = 0;
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
        try { found = core.detect(ui.video); } catch (e) { found = null; }

        var cw = ui.overlay.clientWidth, ch = ui.overlay.clientHeight;
        if (ui.overlay.width !== cw * 2) { ui.overlay.width = cw * 2; ui.overlay.height = ch * 2; }
        var g = ui.overlay.getContext("2d");
        g.setTransform(2, 0, 0, 2, 0, 0);
        g.clearRect(0, 0, cw, ch);

        if (!found) {
            smoothQuad = null; stableFrames = 0; lastFound = null;
            ui.hint.textContent = t("hint", "Hold the camera over the page");
            return;
        }

        // light smoothing so the outline does not jitter
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

        var box = frameBox();
        g.lineWidth = 3;
        g.strokeStyle = stableFrames > 2 ? "#3ddc84" : "#ffd54a";
        g.fillStyle = stableFrames > 2 ? "rgba(61,220,132,0.18)" : "rgba(255,213,74,0.14)";
        g.beginPath();
        smoothQuad.forEach(function (p, i) {
            var x = box.x + p[0] * box.w, y = box.y + p[1] * box.h;
            if (i) { g.lineTo(x, y); } else { g.moveTo(x, y); }
        });
        g.closePath(); g.fill(); g.stroke();

        ui.hint.textContent = stableFrames > 2 && autoMode ? t("hold", "Hold still …") : t("found", "Page found");
        if (autoMode && stableFrames >= 7) { stableFrames = 0; capture(true); }
    }

    function capture(automatic) {
        if (ui.video.readyState < 2 || !ui.video.videoWidth) { return; }
        var c = core.makeCanvas(ui.video.videoWidth, ui.video.videoHeight);
        c.getContext("2d", { willReadFrequently: true }).drawImage(ui.video, 0, 0);
        var quad = lastFound || defaultQuad();
        if (automatic && navigator.vibrate) { navigator.vibrate(30); }
        addPage(limit(c), quad);
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
        updatePagesBtn();
        stopCamera();
        showReview(current);
    }

    function addFiles(fileList) {
        var files = Array.prototype.slice.call(fileList || []).filter(function (f) { return /^image\//.test(f.type); });
        if (!files.length) { return; }
        busy(true);
        var chain = Promise.resolve();
        files.forEach(function (file) {
            chain = chain.then(function () { return loadImage(file).then(function (canvas) {
                var det = null;
                try { det = core.detect(canvas); } catch (e) { det = null; }
                pages.push({ src: canvas, quad: det ? det.quad : defaultQuad(), turns: 0, filter: "enhanced", preview: null, thumb: null });
            }); });
        });
        chain.then(function () {
            busy(false);
            current = pages.length - 1;
            updatePagesBtn();
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

    function render(page, side) {
        var warped = core.warp(page.src, page.quad, side);
        var turned = core.rotate(warped, page.turns);
        return core.filter(turned, page.filter);
    }

    function fit(canvas, stage, target) {
        var sw = stage.clientWidth, sh = stage.clientHeight;
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
            b.classList.toggle("is-active", page && b.getAttribute("data-filter") === page.filter);
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
        updatePagesBtn();
        showCamera();
    }

    function nextPage() { showCamera(); }

    // ------------------------------------------------------------------ adjust (corners)

    var adjust = { scale: 1, handles: [] };

    function showAdjust(index) {
        current = index;
        setView("adjust");
        var page = pages[index];
        adjust.scale = fit(page.src, ui.adjustStage, ui.adjustCanvas);
        var w = ui.adjustCanvas.width, h = ui.adjustCanvas.height;
        ui.adjustSvg.setAttribute("viewBox", "0 0 " + w + " " + h);
        ui.adjustSvg.setAttribute("width", w);
        ui.adjustSvg.setAttribute("height", h);
        drawAdjust();
    }

    function drawAdjust() {
        var page = pages[current], svg = ui.adjustSvg;
        var w = ui.adjustCanvas.width, h = ui.adjustCanvas.height;
        while (svg.firstChild) { svg.removeChild(svg.firstChild); }
        var ns = "http://www.w3.org/2000/svg";
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
            var poly = svg.querySelector("polygon"), circles = svg.querySelectorAll("circle");
            var w = ui.adjustCanvas.width, h = ui.adjustCanvas.height;
            poly.setAttribute("points", page.quad.map(function (p) { return (p[0] * w) + "," + (p[1] * h); }).join(" "));
            circles[index].setAttribute("cx", x * w); circles[index].setAttribute("cy", y * h);
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
        try { det = core.detect(page.src); } catch (e) { det = null; }
        page.quad = det ? det.quad : defaultQuad();
        drawAdjust();
    }

    // Turns the photo itself (when the page was shot sideways); the corners turn with it.
    function turnSource(delta) {
        var page = pages[current];
        var rotated = core.rotate(page.src, delta);
        page.quad = page.quad.map(function (p) { return delta > 0 ? [1 - p[1], p[0]] : [p[1], 1 - p[0]]; });
        page.src = rotated;
        showAdjust(current);
    }

    function applyAdjust() { showReview(current); }

    // ------------------------------------------------------------------ pages overview

    function showPages() {
        stopCamera();
        setView("pages");
        ui.grid.innerHTML = "";
        pages.forEach(function (page, i) {
            var cell = el("div", "scn__cell");
            var thumb = core.makeCanvas(1, 1);
            if (!page.thumb) { page.thumb = core.filter(core.rotate(core.warp(page.src, page.quad, 360), page.turns), page.filter); }
            thumb = page.thumb;
            var img = el("canvas", "scn__thumb");
            img.width = thumb.width; img.height = thumb.height;
            img.getContext("2d").drawImage(thumb, 0, 0);
            img.addEventListener("click", function () { showReview(i); });
            var tools = el("div", "scn__cell-tools");
            tools.append(
                button("scn__mini", "◀", function () { moveBy(i, -1); }),
                button("scn__mini", "✕", function () { pages.splice(i, 1); updatePagesBtn(); pages.length ? showPages() : showCamera(); }),
                button("scn__mini", "▶", function () { moveBy(i, 1); }));
            tools.children[0].disabled = i === 0;
            tools.children[2].disabled = i === pages.length - 1;
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
        if (!pages.length) { closeScanner(); return; }
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
            var dt = new DataTransfer();
            files.forEach(function (f) { dt.items.add(f); });
            var input = document.getElementById("Images");
            input.files = dt.files;
            document.getElementById("Prepared").value = "1";
            busy(false);
            closeScanner();
        });
    }

    // The form shows how many pages are ready.
    function summarize() {
        var box = document.getElementById("scanner-summary");
        if (!box) { return; }
        var input = document.getElementById("Images");
        var ready = pages.length && input && input.files && input.files.length === pages.length;
        box.hidden = !pages.length;
        box.querySelector("[data-count]").textContent = pages.length ? t("pages-ready", "{0} page(s) ready").replace("{0}", pages.length) : "";
        var strip = box.querySelector("[data-strip]");
        strip.innerHTML = "";
        pages.forEach(function (page) {
            if (!page.thumb) { page.thumb = core.filter(core.rotate(core.warp(page.src, page.quad, 240), page.turns), page.filter); }
            var c = el("canvas", "scn-summary__thumb");
            c.width = page.thumb.width; c.height = page.thumb.height;
            c.getContext("2d").drawImage(page.thumb, 0, 0);
            strip.appendChild(c);
        });
        var submit = document.getElementById("scan-submit");
        if (submit) { submit.disabled = !ready && pages.length > 0; }
    }

    // ------------------------------------------------------------------ wire up

    launch.addEventListener("click", function () { openScanner(); });
    var galleryLaunch = document.getElementById("scanner-gallery");
    var galleryInput = document.getElementById("scanner-gallery-input");
    if (galleryLaunch && galleryInput) {
        galleryLaunch.addEventListener("click", function () { galleryInput.click(); });
        galleryInput.addEventListener("change", function () { openScanner(galleryInput.files); galleryInput.value = ""; });
    }
    var reopen = document.getElementById("scanner-reopen");
    if (reopen) { reopen.addEventListener("click", function () { openScanner(); showPages(); }); }

    // The scanner replaces the plain photo field; without it the field stays as the fallback.
    document.getElementById("scan-fallback").hidden = true;
    launch.closest(".scn-launch").hidden = false;

    window.MatPaperScanner = { open: openScanner };
})();
