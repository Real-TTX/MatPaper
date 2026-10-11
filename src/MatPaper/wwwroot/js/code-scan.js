// Scan a label with the camera: the code on the sticker of a paper original opens the document that has the number
// (or fills the field of an archive number). The frames of the live camera go to the server, where the same reader that
// reads uploaded files looks at them (QR code and bar codes): that works on every phone - Safari has no BarcodeDetector -
// and finds exactly what a scan of the paper would find. Where there is no live camera (no HTTPS, no permission) a photo
// taken with the camera app does the same.
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
    var INTERVAL = 450;      // pause between two frames
    var MAX_SIDE = 1400;     // a frame is cut down to this before it is sent
    var AREA = { w: 0.7, h: 0.5 }; // the part of the picture inside the frame on the screen

    var ICONS = {
        close: '<path d="M6 6l12 12M18 6L6 18"/>',
        torch: '<path d="M13 2L4 14h7l-1 8 9-12h-7z"/>',
        camera: '<path d="M4 8a2 2 0 0 1 2-2h2l1.5-2h5L16 6h2a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z"/><circle cx="12" cy="12.5" r="3.5"/>'
    };

    var root = null, ui = {}, mode = "open", targetInput = null;
    var stream = null, track = null, timer = null, busy = false, torchOn = false, finished = false;

    // ------------------------------------------------------------------ DOM

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

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) { e.className = cls; }
        if (text) { e.textContent = text; }
        return e;
    }

    function tool(iconName, label, onClick) {
        var b = el("button", "scn__tool");
        b.type = "button";
        b.appendChild(icon(iconName));
        if (label) { b.appendChild(el("span", "", label)); b.setAttribute("aria-label", label); }
        b.addEventListener("click", onClick);
        return b;
    }

    function build() {
        root = el("div", "scn scn--code");
        root.hidden = true;
        root.setAttribute("role", "dialog");
        root.setAttribute("aria-modal", "true");
        root.setAttribute("aria-label", t("title", "Scan a label"));

        var view = el("div", "scn__view");
        var top = el("div", "scn__top");
        var close = tool("close", "", closeScanner);
        close.setAttribute("aria-label", t("close", "Close"));
        top.appendChild(close);
        top.appendChild(el("div", "scn__heading", t("title", "Scan a label")));
        ui.torch = tool("torch", "", toggleTorch);
        ui.torch.setAttribute("aria-label", t("light", "Light"));
        ui.torch.hidden = true;
        top.appendChild(ui.torch);

        ui.stage = el("div", "code-scan__stage");
        ui.frame = el("div", "code-scan__frame");
        ui.video = el("video");
        ui.video.setAttribute("playsinline", "");
        ui.video.setAttribute("muted", "");
        ui.video.muted = true;
        ui.video.autoplay = true;
        ui.frame.appendChild(ui.video);
        ui.frame.appendChild(el("div", "code-scan__reticle"));
        ui.stage.appendChild(ui.frame);
        ui.status = el("div", "code-scan__status");
        ui.status.setAttribute("role", "status");
        ui.stage.appendChild(ui.status);

        var bottom = el("div", "scn__bottom");
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

        view.appendChild(top);
        view.appendChild(ui.stage);
        view.appendChild(bottom);
        root.appendChild(view);
        document.body.appendChild(root);

        ui.video.addEventListener("loadedmetadata", layout);
        window.addEventListener("resize", layout);
        window.addEventListener("orientationchange", function () { window.setTimeout(layout, 250); });
        document.addEventListener("keydown", function (e) { if (e.key === "Escape" && !root.hidden) { closeScanner(); } });
        document.addEventListener("visibilitychange", function () {
            if (root.hidden) { return; }
            if (document.hidden) { stopCamera(); } else if (!finished) { startCamera(); }
        });
    }

    // The status bar of a phone belongs to the scanner while it is open (the same as the document scanner does).
    function themeColor(color) {
        var old = document.querySelector('meta[name="theme-color"]');
        var meta = document.createElement("meta");
        meta.setAttribute("name", "theme-color");
        meta.setAttribute("content", color);
        if (old && old.parentNode) { old.parentNode.replaceChild(meta, old); } else { document.head.appendChild(meta); }
    }

    function barColor(scanning) {
        var html = document.documentElement;
        var surface = getComputedStyle(html).getPropertyValue("--color-surface").trim() || "#ffffff";
        if (scanning) {
            html.classList.add("has-scanner");
            themeColor("#000000");
        } else {
            html.classList.remove("has-scanner");
            themeColor(surface);
            html.style.backgroundColor = "var(--color-surface)";
            void html.offsetHeight;
            html.style.removeProperty("background-color");
        }
        void html.offsetHeight;
    }

    // The frame on the screen has the shape of the camera picture, as big as the stage allows.
    function layout() {
        var vw = ui.video.videoWidth, vh = ui.video.videoHeight;
        if (!vw || !vh) { return; }
        var scale = Math.min(ui.stage.clientWidth / vw, ui.stage.clientHeight / vh);
        ui.frame.style.width = Math.round(vw * scale) + "px";
        ui.frame.style.height = Math.round(vh * scale) + "px";
    }

    function status(text, kind) {
        ui.status.textContent = text || "";
        ui.status.className = "code-scan__status" + (kind ? " is-" + kind : "");
    }

    // ------------------------------------------------------------------ camera

    function startCamera() {
        stopCamera();
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) { noCamera(); return; }
        navigator.mediaDevices.getUserMedia({
            video: { facingMode: { ideal: "environment" }, width: { ideal: 1920 }, height: { ideal: 1080 } },
            audio: false
        }).then(function (s) {
            if (root.hidden || finished) { s.getTracks().forEach(function (tr) { tr.stop(); }); return; }
            stream = s;
            track = s.getVideoTracks()[0];
            ui.video.srcObject = s;
            var played = ui.video.play();
            if (played && played.catch) { played.catch(function () { }); }
            try { ui.torch.hidden = !(track.getCapabilities && track.getCapabilities().torch); } catch (e) { ui.torch.hidden = true; }
            layout();
            status(t("hint", "Hold the label in the frame"));
            schedule();
        }).catch(function () { noCamera(); });
    }

    function noCamera() {
        ui.torch.hidden = true;
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

    // ------------------------------------------------------------------ looking at frames

    function schedule(delay) {
        window.clearTimeout(timer);
        if (root.hidden || !stream || finished) { return; }
        timer = window.setTimeout(frame, delay || INTERVAL);
    }

    // One frame: the middle of the picture (what the frame on the screen shows) at full detail, cut down when it is big.
    function frame() {
        var video = ui.video;
        if (busy || root.hidden || !stream || !video.videoWidth || video.readyState < 2) { schedule(); return; }
        var vw = video.videoWidth, vh = video.videoHeight;
        var sw = Math.round(vw * AREA.w), sh = Math.round(vh * AREA.h);
        var sx = Math.round((vw - sw) / 2), sy = Math.round((vh - sh) / 2);
        var scale = Math.min(1, MAX_SIDE / Math.max(sw, sh));
        ui.canvas.width = Math.max(1, Math.round(sw * scale));
        ui.canvas.height = Math.max(1, Math.round(sh * scale));
        ui.canvas.getContext("2d").drawImage(video, sx, sy, sw, sh, 0, 0, ui.canvas.width, ui.canvas.height);
        busy = true;
        send(ui.canvas).then(function (res) {
            busy = false;
            if (handle(res, false)) { return; }
            schedule();
        }, function () {
            busy = false;
            status(t("error", "The code could not be read. Please try again."), "warn");
            schedule(2000);
        });
    }

    function token() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : "";
    }

    function send(canvas) {
        return new Promise(function (resolve, reject) {
            canvas.toBlob(function (blob) {
                if (!blob) { reject(new Error("no picture")); return; }
                var data = new FormData();
                data.append("image", blob, "label.jpg");
                fetch(URL_SCAN, { method: "POST", body: data, headers: { RequestVerificationToken: token() }, credentials: "same-origin" })
                    .then(function (response) {
                        if (!response.ok) { throw new Error("http " + response.status); }
                        return response.json();
                    }).then(resolve, reject);
            }, "image/jpeg", 0.72);
        });
    }

    // A photo from the camera app: cut down (a phone photo is big) and read the same way.
    function readPhoto(file) {
        status(t("reading", "Reading …"));
        var url = URL.createObjectURL(file);
        var img = new Image();
        img.onload = function () {
            URL.revokeObjectURL(url);
            var scale = Math.min(1, 2200 / Math.max(img.naturalWidth, img.naturalHeight));
            ui.canvas.width = Math.max(1, Math.round(img.naturalWidth * scale));
            ui.canvas.height = Math.max(1, Math.round(img.naturalHeight * scale));
            ui.canvas.getContext("2d").drawImage(img, 0, 0, ui.canvas.width, ui.canvas.height);
            send(ui.canvas).then(function (res) {
                if (!handle(res, true)) { status(t("photo-none", "No code found in the photo. Try again, closer to the label."), "warn"); }
            }, function () { status(t("error", "The code could not be read. Please try again."), "warn"); });
        };
        img.onerror = function () { URL.revokeObjectURL(url); status(t("error", "The code could not be read. Please try again."), "warn"); };
        img.src = url;
    }

    // What the server found. Returns true when the scanner is done (or has said what it has to say).
    function handle(res, fromPhoto) {
        if (!res) { return false; }
        if (res.status === "asn") {
            if (mode === "fill" && targetInput) {
                targetInput.value = res.asn;
                targetInput.dispatchEvent(new Event("input", { bubbles: true }));
                targetInput.dispatchEvent(new Event("change", { bubbles: true }));
                finish(res.text, true);
                return true;
            }
            if (res.found && res.url) {
                finish(res.text + (res.title ? " · " + res.title : ""), true);
                window.location.href = res.url;
                return true;
            }
            status(fill(t("not-found", "No document has the number {0}."), res.text), "warn");
            return fromPhoto;
        }
        if (res.status === "other") {
            status(fill(t("other", "Read \"{0}\" - that is no archive number."), res.text), "warn");
            return fromPhoto;
        }
        return false;
    }

    function finish(text, ok) {
        finished = true;
        window.clearTimeout(timer);
        status(text, ok ? "ok" : "");
        try { if (navigator.vibrate) { navigator.vibrate(60); } } catch (e) { }
        if (mode === "fill") { window.setTimeout(closeScanner, 450); } else { stopCamera(); }
    }

    // ------------------------------------------------------------------ open / close

    function open(options) {
        mode = (options && options.mode) || "open";
        targetInput = (options && options.target) || null;
        if (!root) { build(); }
        finished = false;
        busy = false;
        status("");
        root.hidden = false;
        barColor(true);
        startCamera();
    }

    function closeScanner() {
        finished = true;
        stopCamera();
        if (root) { root.hidden = true; }
        barColor(false);
    }

    document.addEventListener("click", function (event) {
        var button = event.target && event.target.closest ? event.target.closest("[data-code-scan]") : null;
        if (!button) { return; }
        event.preventDefault();
        var id = button.getAttribute("data-code-target");
        open({ mode: button.getAttribute("data-code-scan") === "fill" ? "fill" : "open", target: id ? document.getElementById(id) : null });
    });

    window.MatPaperCodeScan = { open: open, close: closeScanner };
})();
