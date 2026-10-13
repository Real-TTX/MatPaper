// Scan a label with the camera: the code on the sticker of a paper original opens the document that has the number
// (or fills the field of an archive number). The frames of the live camera go to the server, where the same reader that
// reads uploaded files looks at them (QR code and bar codes): that works on every phone - Safari has no BarcodeDetector -
// and finds exactly what a scan of the paper would find. Where there is no live camera (no HTTPS, no permission) a photo
// taken with the camera app does the same.
//
// A label that was read is not acted on at once: the scanner stops, shows what it read (the number and the document)
// and waits - "Open" / "Take over" goes on, "Scan again" is for the label that was read by mistake. The label that was
// just turned down is not offered again as long as it stays in view.
//
//   <button data-code-scan="open">                         opens the document that has the number
//   <button data-code-scan="fill" data-code-target="id">   puts the number into the input with that id
//   MatPaperCodeScan.open({ mode: "open" | "fill", target: input })
(function () {
    "use strict";

    var cfg = document.getElementById("code-scan-config");
    if (!cfg) { return; }
    function t(key, fallback) { return cfg.getAttribute("data-t-" + key) || fallback; }
    function fill(text, value) { return text.replace("{0}", value); }

    var URL_SCAN = cfg.getAttribute("data-url") || "/Documents/ScanCode";
    var TICK = 110;           // how often a frame of the camera is looked at
    var MAX_IN_FLIGHT = 2;    // pictures that wait for the answer of the server at the same time
    var FORCE_EVERY = 700;    // a frame goes to the server at the latest this often, however sharp it is
    var CROP_W = 1600;        // the part of the picture inside the frame is sent at its own size, up to this
    var CROP_H = 1000;
    var PHOTO_SIDE = 3200;    // a photo from the camera app is cut down to this
    var IGNORE_GONE = 1200;   // a turned-down label has to be out of sight this long before it counts again

    var SPRITE = { close: "x", torch: "flashlight", camera: "camera" };

    var root = null, ui = {}, mode = "open", targetInput = null;
    var stream = null, track = null, timer = null, statusTimer = null, inFlight = 0, torchOn = false;
    var state = "closed";      // closed | scanning | result
    var last = null, lastSource = "live", ignore = null, best = 0, lastSent = 0;
    var zoomLevels = [], zoomIndex = 0;

    // ------------------------------------------------------------------ DOM

    function icon(name) {
        var holder = document.createElement("span");
        holder.innerHTML = window.MatPaper.icon(SPRITE[name] || name);
        var svg = holder.firstChild;
        svg.classList.add("mp-mp-scn__icon");
        return svg;
    }

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) { e.className = cls; }
        if (text) { e.textContent = text; }
        return e;
    }

    function tool(iconName, label, onClick) {
        var b = el("button", "mp-scn__tool");
        b.type = "button";
        if (iconName) { b.appendChild(icon(iconName)); }
        if (label) { b.appendChild(el("span", "", label)); b.setAttribute("aria-label", label); }
        b.addEventListener("click", onClick);
        return b;
    }

    function button(cls, label, onClick) {
        var b = el("button", "mat-btn " + cls, label);
        b.type = "button";
        b.addEventListener("click", onClick);
        return b;
    }

    function build() {
        root = el("div", "mp-scn mp-scn--code");
        root.hidden = true;
        root.setAttribute("role", "dialog");
        root.setAttribute("aria-modal", "true");
        root.setAttribute("aria-label", t("title", "Scan a label"));

        var view = el("div", "mp-scn__view");
        var top = el("div", "mp-scn__top");
        var close = tool("close", "", closeScanner);
        close.setAttribute("aria-label", t("close", "Close"));
        top.appendChild(close);
        top.appendChild(el("div", "mp-scn__heading", t("title", "Scan a label")));
        ui.zoom = tool("", "1×", cycleZoom);
        ui.zoom.setAttribute("aria-label", t("zoom", "Zoom"));
        ui.zoom.hidden = true;
        top.appendChild(ui.zoom);
        ui.torch = tool("torch", "", toggleTorch);
        ui.torch.setAttribute("aria-label", t("light", "Light"));
        ui.torch.hidden = true;
        top.appendChild(ui.torch);

        ui.stage = el("div", "mp-code__stage");
        ui.frame = el("div", "mp-code__frame");
        ui.video = el("video");
        ui.video.setAttribute("playsinline", "");
        ui.video.setAttribute("muted", "");
        ui.video.muted = true;
        ui.video.autoplay = true;
        ui.frame.appendChild(ui.video);
        ui.reticle = el("div", "mp-code__reticle");
        ui.frame.appendChild(ui.reticle);
        ui.stage.appendChild(ui.frame);
        ui.status = el("div", "mp-code__status");
        ui.status.setAttribute("role", "status");
        ui.stage.appendChild(ui.status);

        // what was read: the number, the document, and the two ways on
        ui.card = el("div", "mp-code__result");
        ui.card.hidden = true;
        ui.card.setAttribute("role", "alertdialog");
        ui.asn = el("div", "mp-code__asn");
        ui.doc = el("div", "mp-code__doc");
        ui.sub = el("div", "mp-code__sub");
        var actions = el("div", "mp-code__actions");
        ui.again = button("mat-btn--secondary", t("again", "Scan again"), again);
        ui.primary = button("mat-btn--primary", t("open", "Open"), proceed);
        actions.appendChild(ui.again);
        actions.appendChild(ui.primary);
        ui.card.appendChild(ui.asn);
        ui.card.appendChild(ui.doc);
        ui.card.appendChild(ui.sub);
        ui.card.appendChild(actions);
        ui.stage.appendChild(ui.card);

        var bottom = el("div", "mp-scn__bottom");
        ui.photo = tool("camera", t("photo", "Take a photo"), function () { ui.file.click(); });
        bottom.appendChild(ui.photo);
        ui.file = el("input");
        ui.file.type = "file";
        ui.file.accept = "image/*";
        ui.file.setAttribute("capture", "environment");
        ui.file.hidden = true;
        ui.file.addEventListener("change", function () {
            var file = ui.file.files && ui.file.files[0];
            ui.file.value = "";
            if (file) { readPhoto(file); }
        });
        bottom.appendChild(ui.file);

        ui.canvas = document.createElement("canvas");
        ui.small = document.createElement("canvas");

        view.appendChild(top);
        view.appendChild(ui.stage);
        view.appendChild(bottom);
        root.appendChild(view);
        document.body.appendChild(root);

        ui.video.addEventListener("loadedmetadata", layout);
        window.addEventListener("resize", layout);
        window.addEventListener("orientationchange", function () { window.setTimeout(layout, 250); });
        document.addEventListener("keydown", function (e) { if (e.key === "Escape" && state !== "closed") { closeScanner(); } });
        document.addEventListener("visibilitychange", function () {
            if (state === "closed") { return; }
            if (document.hidden) { stopCamera(); } else { startCamera(); }
        });
    }

    // The status bar of a phone belongs to the scanner while it is open (the same as the document scanner does).
    var pageThemeColor = null;

    function themeColor(color) {
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

    // The part of the camera picture that is looked at: the middle of it, at its own size (up to CROP_W x CROP_H), as
    // wide as a label needs - wider on a phone held upright. The frame on the screen shows exactly that part.
    function aim() {
        var vw = ui.video.videoWidth, vh = ui.video.videoHeight;
        var portrait = vh > vw;
        var cw = Math.min(Math.round(vw * (portrait ? 0.84 : 0.56)), CROP_W);
        var ch = Math.min(Math.round(vh * (portrait ? 0.34 : 0.5)), CROP_H);
        return { vw: vw, vh: vh, w: cw, h: ch, x: Math.round((vw - cw) / 2), y: Math.round((vh - ch) / 2) };
    }

    // The frame on the screen has the shape of the camera picture, as big as the stage allows.
    function layout() {
        var vw = ui.video.videoWidth, vh = ui.video.videoHeight;
        if (!vw || !vh) { return; }
        var scale = Math.min(ui.stage.clientWidth / vw, ui.stage.clientHeight / vh);
        ui.frame.style.width = Math.round(vw * scale) + "px";
        ui.frame.style.height = Math.round(vh * scale) + "px";
        var a = aim();
        ui.reticle.style.left = (a.x / vw * 100) + "%";
        ui.reticle.style.top = (a.y / vh * 100) + "%";
        ui.reticle.style.width = (a.w / vw * 100) + "%";
        ui.reticle.style.height = (a.h / vh * 100) + "%";
    }

    function status(text, kind, forMs) {
        window.clearTimeout(statusTimer);
        ui.status.textContent = text || "";
        ui.status.className = "mp-code__status" + (kind ? " is-" + kind : "");
        if (text && forMs) { statusTimer = window.setTimeout(function () { if (state === "scanning") { status(t("hint", "Hold the label in the frame")); } }, forMs); }
    }

    // ------------------------------------------------------------------ camera

    function startCamera() {
        stopCamera();
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) { noCamera(); return; }
        // as sharp as the camera gives: more pixels per module reach smaller labels from further away
        navigator.mediaDevices.getUserMedia({
            video: { facingMode: { ideal: "environment" }, width: { ideal: 3840 }, height: { ideal: 2160 } },
            audio: false
        }).then(function (s) {
            if (state === "closed") { s.getTracks().forEach(function (tr) { tr.stop(); }); return; }
            stream = s;
            track = s.getVideoTracks()[0];
            ui.video.srcObject = s;
            var played = ui.video.play();
            if (played && played.catch) { played.catch(function () { }); }
            abilities();
            layout();
            if (state === "scanning") { status(t("hint", "Hold the label in the frame")); schedule(); }
        }).catch(function () { noCamera(); });
    }

    // What this camera can do beyond the picture: light, zoom, steady focus.
    function abilities() {
        var caps = {};
        try { caps = track.getCapabilities ? track.getCapabilities() : {}; } catch (e) { caps = {}; }
        ui.torch.hidden = !caps.torch;
        zoomLevels = [];
        zoomIndex = 0;
        if (caps.zoom && caps.zoom.max > caps.zoom.min) {
            var min = caps.zoom.min;
            [1, 2, 3].forEach(function (f) {
                var level = Math.min(caps.zoom.max, min * f);
                if (zoomLevels.indexOf(level) === -1) { zoomLevels.push(level); }
            });
        }
        ui.zoom.hidden = zoomLevels.length < 2;
        ui.zoom.querySelector("span").textContent = "1×";
        if (caps.focusMode && caps.focusMode.indexOf && caps.focusMode.indexOf("continuous") !== -1) {
            track.applyConstraints({ advanced: [{ focusMode: "continuous" }] }).catch(function () { });
        }
    }

    function noCamera() {
        ui.torch.hidden = true;
        ui.zoom.hidden = true;
        status(t("no-camera", "The camera is not available here (it needs HTTPS). Take a photo of the label instead."), "warn");
    }

    function stopCamera() {
        window.clearTimeout(timer);
        timer = null;
        if (stream) { stream.getTracks().forEach(function (tr) { tr.stop(); }); }
        stream = null;
        track = null;
        torchOn = false;
        if (ui.torch) { ui.torch.classList.remove("is-on"); }
        if (ui.video) { ui.video.srcObject = null; }
    }

    function toggleTorch() {
        if (!track) { return; }
        torchOn = !torchOn;
        track.applyConstraints({ advanced: [{ torch: torchOn }] }).then(function () {
            ui.torch.classList.toggle("is-on", torchOn);
        }).catch(function () { torchOn = false; });
    }

    function cycleZoom() {
        if (!track || zoomLevels.length < 2) { return; }
        zoomIndex = (zoomIndex + 1) % zoomLevels.length;
        track.applyConstraints({ advanced: [{ zoom: zoomLevels[zoomIndex] }] }).then(function () {
            ui.zoom.querySelector("span").textContent = Math.round(zoomLevels[zoomIndex] / zoomLevels[0]) + "×";
        }).catch(function () { });
    }

    // ------------------------------------------------------------------ looking at frames

    function schedule(delay) {
        window.clearTimeout(timer);
        if (state !== "scanning" || !stream) { return; }
        timer = window.setTimeout(tick, delay || TICK);
    }

    // How sharp the frame is: the strength of the edges in a small copy of it. A camera that is still looking for its
    // focus, or a hand that moves, gives soft frames - they are not worth the trip to the server.
    function sharpness(canvas) {
        var w = 320, h = Math.max(8, Math.round(canvas.height * w / canvas.width));
        ui.small.width = w;
        ui.small.height = h;
        var ctx = ui.small.getContext("2d", { willReadFrequently: true });
        ctx.drawImage(canvas, 0, 0, w, h);
        var d = ctx.getImageData(0, 0, w, h).data, sum = 0, n = 0;
        for (var y = 0; y < h - 1; y++) {
            for (var x = 0; x < w - 1; x++) {
                var i = (y * w + x) * 4;
                var g = d[i] * 0.3 + d[i + 1] * 0.59 + d[i + 2] * 0.11;
                var gx = d[i + 4] * 0.3 + d[i + 5] * 0.59 + d[i + 6] * 0.11;
                var gy = d[i + w * 4] * 0.3 + d[i + w * 4 + 1] * 0.59 + d[i + w * 4 + 2] * 0.11;
                sum += Math.abs(g - gx) + Math.abs(g - gy);
                n++;
            }
        }
        return sum / Math.max(1, n);
    }

    function tick() {
        timer = null;
        if (state !== "scanning" || !stream) { return; }
        var video = ui.video;
        if (video.videoWidth && video.readyState >= 2) {
            var a = aim();
            ui.canvas.width = a.w;
            ui.canvas.height = a.h;
            ui.canvas.getContext("2d").drawImage(video, a.x, a.y, a.w, a.h, 0, 0, a.w, a.h);
            var s = sharpness(ui.canvas);
            best = Math.max(best * 0.985, s);
            var now = Date.now();
            if (inFlight < MAX_IN_FLIGHT && (s >= best * 0.7 || now - lastSent > FORCE_EVERY)) {
                lastSent = now;
                inFlight++;
                send(ui.canvas, false).then(function (res) { inFlight--; handle(res, "live"); },
                    function () { inFlight--; status(t("error", "The code could not be read. Please try again."), "warn", 2500); });
            }
        }
        schedule();
    }

    function token() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : "";
    }

    function send(canvas, photo) {
        return new Promise(function (resolve, reject) {
            canvas.toBlob(function (blob) {
                if (!blob) { reject(new Error("no picture")); return; }
                var data = new FormData();
                data.append("image", blob, "label.jpg");
                if (photo) { data.append("photo", "true"); }
                fetch(URL_SCAN, { method: "POST", body: data, headers: { RequestVerificationToken: token() }, credentials: "same-origin" })
                    .then(function (response) {
                        if (!response.ok) { throw new Error("http " + response.status); }
                        return response.json();
                    }).then(resolve, reject);
            }, "image/jpeg", photo ? 0.85 : 0.78);
        });
    }

    // A photo from the camera app: cut down (a phone photo is big) and read the same way, with more care.
    function readPhoto(file) {
        if (state === "result") { hideCard(); state = "scanning"; }
        status(t("reading", "Reading …"));
        var url = URL.createObjectURL(file);
        var img = new Image();
        img.onload = function () {
            URL.revokeObjectURL(url);
            var scale = Math.min(1, PHOTO_SIDE / Math.max(img.naturalWidth, img.naturalHeight));
            ui.canvas.width = Math.max(1, Math.round(img.naturalWidth * scale));
            ui.canvas.height = Math.max(1, Math.round(img.naturalHeight * scale));
            ui.canvas.getContext("2d").drawImage(img, 0, 0, ui.canvas.width, ui.canvas.height);
            send(ui.canvas, true).then(function (res) {
                if (!handle(res, "photo")) { status(t("photo-none", "No code found in the photo. Try again, closer to the label."), "warn"); }
            }, function () { status(t("error", "The code could not be read. Please try again."), "warn"); });
        };
        img.onerror = function () { URL.revokeObjectURL(url); status(t("error", "The code could not be read. Please try again."), "warn"); };
        img.src = url;
    }

    // What the server found. Returns true when it was something to act on (a label of the archive).
    function handle(res, source) {
        if (!res || state === "closed") { return false; }
        if (source === "live" && state !== "scanning") { return false; }   // an answer that came too late
        var now = Date.now();
        if (res.status === "asn") {
            // the label that was just turned down is still in front of the camera: not again
            if (source === "live" && ignore && ignore.asn === res.asn) { ignore.seen = now; return true; }
            showResult(res, source);
            return true;
        }
        if (source === "live" && ignore && now - ignore.seen > IGNORE_GONE) { ignore = null; }
        if (res.status === "other") {
            status(fill(t("other", "Read \"{0}\" - that is no archive number."), res.text), "warn", source === "live" ? 3500 : 0);
            return source === "photo";
        }
        return false;
    }

    // ------------------------------------------------------------------ the answer and the ways on

    function showResult(res, source) {
        window.clearTimeout(timer);
        timer = null;
        state = "result";
        last = res;
        lastSource = source;
        ui.asn.textContent = res.text;
        ui.doc.className = "mp-code__doc";
        ui.sub.textContent = "";
        ui.primary.hidden = false;
        if (mode === "fill") {
            if (res.found) {
                ui.doc.textContent = fill(t("belongs-to", "Belongs to \"{0}\" already"), res.title || "");
                ui.doc.classList.add("is-warn");
                ui.primary.textContent = t("take-over-anyway", "Take over anyway");
            } else {
                ui.doc.textContent = t("free-number", "Free: no document has this number yet.");
                ui.primary.textContent = t("take-over", "Take over");
            }
        } else if (res.found) {
            ui.doc.textContent = res.title || "";
            ui.sub.textContent = res.subtitle || "";
            ui.primary.textContent = t("open", "Open");
        } else {
            ui.doc.textContent = t("no-document", "No document has this number.");
            ui.doc.classList.add("is-warn");
            ui.primary.hidden = true;
        }

        status("");
        ui.card.hidden = false;
        try { if (navigator.vibrate) { navigator.vibrate(60); } } catch (e) { }
        (ui.primary.hidden ? ui.again : ui.primary).focus();
    }

    function hideCard() {
        ui.card.hidden = true;
    }

    function proceed() {
        if (!last) { return; }
        if (mode === "fill" && targetInput) {
            targetInput.value = last.asn;
            targetInput.dispatchEvent(new Event("input", { bubbles: true }));
            targetInput.dispatchEvent(new Event("change", { bubbles: true }));
            closeScanner();
            targetInput.focus();
            return;
        }
        if (last.url) {
            stopCamera();
            ui.primary.disabled = true;
            window.location.href = last.url;
        }
    }

    // The wrong label: back to looking. The one that was just turned down is not offered again while it stays in view.
    function again() {
        if (last) { ignore = { asn: last.asn, seen: Date.now() }; }
        hideCard();
        state = "scanning";
        best = 0;
        lastSent = 0;
        status(t("again-hint", "Hold another label in the frame"));
        if (lastSource === "photo") { ui.file.click(); }
        if (!stream) { startCamera(); } else { schedule(); }
    }

    // ------------------------------------------------------------------ open / close

    function open(options) {
        mode = (options && options.mode) || "open";
        targetInput = (options && options.target) || null;
        if (!root) { build(); }
        state = "scanning";
        last = null;
        ignore = null;
        inFlight = 0;
        best = 0;
        lastSent = 0;
        hideCard();
        ui.primary.disabled = false;
        status("");
        root.hidden = false;
        barColor(true);
        startCamera();
    }

    function closeScanner() {
        state = "closed";
        window.clearTimeout(statusTimer);
        stopCamera();
        if (root) { root.hidden = true; hideCard(); }
        barColor(false);
    }

    document.addEventListener("click", function (event) {
        var trigger = event.target && event.target.closest ? event.target.closest("[data-code-scan]") : null;
        if (!trigger) { return; }
        event.preventDefault();
        var id = trigger.getAttribute("data-code-target");
        open({ mode: trigger.getAttribute("data-code-scan") === "fill" ? "fill" : "open", target: id ? document.getElementById(id) : null });
    });

    window.MatPaperCodeScan = { open: open, close: closeScanner };
})();
