// "Add document": drag & drop files and/or scan with the camera, then fill in what is known and add.
// Falls back to a plain multipart form POST (files only) when JavaScript is off.
(function () {
    "use strict";

    var form = document.getElementById("upload-form");
    if (!form) { return; }

    var ajaxUrl = form.getAttribute("data-ajax-url");
    var scanUrl = form.getAttribute("data-scan-url");
    var inboxUrl = form.getAttribute("data-inbox-url") || "/Inbox";
    function t(name, fallback) { return form.getAttribute("data-t-" + name) || fallback; }
    function fill(template, value) { return template.replace("{0}", value); }

    var input = document.getElementById("Files");
    var dropzone = document.getElementById("uploader-dropzone");
    var browse = document.getElementById("uploader-browse");
    var list = document.getElementById("upload-list");
    var submit = document.getElementById("upload-submit");
    var details = document.getElementById("step-details");
    var titleRow = document.getElementById("add-title-row");
    var cameraBtn = document.getElementById("add-camera");
    var scanBox = document.getElementById("add-scan");

    if (!ajaxUrl || !input || !window.FormData || !window.XMLHttpRequest) { return; }

    var queue = [];
    var nextId = 1;
    var uploading = false;
    var scan = null; // { files: [File], thumbs: [canvas], status, row }
    var uploader = document.getElementById("uploader");
    if (uploader) { uploader.classList.add("uploader--enhanced"); }

    function token() {
        var el = form.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : "";
    }

    function formatSize(bytes) {
        if (bytes < 1024) { return bytes + " B"; }
        if (bytes < 1024 * 1024) { return num(bytes / 1024, 0) + " KB"; }
        return num(bytes / (1024 * 1024), 1) + " MB";
    }
    function num(value, digits) {
        try { return value.toLocaleString(document.documentElement.lang || undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits }); }
        catch (e) { return value.toFixed(digits); }
    }

    // ---- what the user has chosen so far -------------------------------------

    function total() { return queue.filter(function (i) { return i.status === "queued"; }).length + (scan && scan.status === "queued" ? 1 : 0); }

    function refresh() {
        var n = total();
        details.hidden = n === 0 && !uploading;
        if (!uploading) { submit.disabled = n === 0; }
        titleRow.hidden = n !== 1;
        list.hidden = queue.length === 0;
    }

    function addFiles(fileList) {
        clearSummary();
        for (var i = 0; i < fileList.length; i++) {
            var item = { id: nextId++, file: fileList[i], status: "queued", row: null };
            queue.push(item);
            renderRow(item);
        }
        refresh();
    }

    function renderRow(item) {
        var row = document.createElement("li");
        row.className = "upload-item upload-item--queued";
        row.innerHTML =
            '<div class="upload-item__head">' +
            '<span class="upload-item__name"></span>' +
            '<span class="upload-item__size"></span>' +
            '<button type="button" class="icon-btn upload-item__remove"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18"/></svg></button>' +
            '</div>' +
            '<div class="upload-item__bar"><span class="upload-item__fill"></span></div>' +
            '<div class="upload-item__status"></div>';
        row.querySelector(".upload-item__name").textContent = item.file.name;
        row.querySelector(".upload-item__size").textContent = formatSize(item.file.size);
        row.querySelector(".upload-item__status").textContent = t("queued", "Queued");
        var remove = row.querySelector(".upload-item__remove");
        remove.setAttribute("aria-label", t("remove", "Remove"));
        remove.addEventListener("click", function () {
            queue.splice(queue.indexOf(item), 1);
            row.remove();
            refresh();
        });
        list.appendChild(row);
        item.row = row;
    }

    function setProgress(row, pct) {
        var fillBar = row.querySelector(".upload-item__fill");
        if (fillBar) { fillBar.style.width = pct + "%"; }
    }

    function setStatus(holder, status, text, href, linkText) {
        holder.status = status;
        holder.row.className = "upload-item upload-item--" + status;
        var cell = holder.row.querySelector(".upload-item__status");
        cell.textContent = text;
        var remove = holder.row.querySelector(".upload-item__remove");
        if (remove && status !== "queued") { remove.hidden = true; }
        if (href) {
            cell.appendChild(document.createTextNode(" "));
            var link = document.createElement("a");
            link.href = href; link.target = "_blank"; link.rel = "noopener";
            link.textContent = linkText || t("show-existing", "Show existing document");
            cell.appendChild(link);
        }
    }

    // ---- camera scan --------------------------------------------------------

    function showScan() {
        if (!scan) { scanBox.hidden = true; return; }
        scanBox.hidden = false;
        scanBox.querySelector("[data-scan-count]").textContent = fill(t("scan-pages", "Scan: {0} page(s)"), scan.files.length);
        var strip = scanBox.querySelector("[data-scan-strip]");
        strip.innerHTML = "";
        scan.thumbs.forEach(function (th) {
            var c = document.createElement("canvas");
            c.className = "add-doc__scan-thumb";
            c.width = th.width; c.height = th.height;
            c.getContext("2d").drawImage(th, 0, 0);
            strip.appendChild(c);
        });
    }

    function onScanned(files, thumbs) {
        if (!files.length) {
            if (!scan) { scanBox.hidden = true; }
            refresh();
            return;
        }
        clearSummary();
        scan = { files: files, thumbs: thumbs, status: "queued", row: null };
        showScan();
        refresh();
    }

    if (cameraBtn && window.MatPaperScanner) {
        cameraBtn.addEventListener("click", function () { window.MatPaperScanner.open({ onDone: onScanned }); });
        document.getElementById("add-scan-edit").addEventListener("click", function () { window.MatPaperScanner.openPages({ onDone: onScanned }); });
        document.getElementById("add-scan-remove").addEventListener("click", function () {
            window.MatPaperScanner.reset();
            scan = null;
            showScan();
            refresh();
        });
        if (form.getAttribute("data-camera") === "1") { window.MatPaperScanner.open({ onDone: onScanned }); }
    } else if (cameraBtn) {
        cameraBtn.hidden = true;
    }

    // ---- sending ------------------------------------------------------------

    function meta(single) {
        var data = new FormData(form);
        var out = { correspondentId: data.get("CorrespondentId"), documentTypeId: data.get("DocumentTypeId"), projectId: data.get("ProjectId") };
        out.tagIds = data.getAll("TagIds").filter(Boolean).join(",");
        out.title = single ? (data.get("Title") || "") : "";
        return out;
    }

    function send(url, build, holder) {
        return new Promise(function (resolve) {
            var data = new FormData();
            build(data);
            var xhr = new XMLHttpRequest();
            xhr.open("POST", url, true);
            xhr.setRequestHeader("RequestVerificationToken", token());
            setStatus(holder, "uploading", t("uploading", "Uploading…"));

            xhr.upload.onprogress = function (e) {
                if (e.lengthComputable) { setProgress(holder.row, Math.round((e.loaded / e.total) * 100)); }
            };
            xhr.onload = function () {
                setProgress(holder.row, 100);
                var res = null;
                try { res = JSON.parse(xhr.responseText); } catch (err) { res = null; }
                if (xhr.status >= 200 && xhr.status < 300 && res) {
                    if (res.status === "created") {
                        setStatus(holder, "created", t("added", "Added"));
                    } else if (res.status === "duplicate") {
                        setStatus(holder, "duplicate", t("duplicate", "Duplicate — skipped"),
                            res.id ? "/Documents/Edit?id=" + encodeURIComponent(res.id) : null);
                    } else {
                        setStatus(holder, "failed", res.message || t("failed", "Failed"));
                    }
                } else {
                    setStatus(holder, "failed", t("failed", "Failed") + " (" + xhr.status + ")");
                }
                resolve();
            };
            xhr.onerror = function () { setStatus(holder, "failed", t("network-error", "Network error")); resolve(); };
            xhr.send(data);
        });
    }

    function addMeta(data, m) {
        data.append("correspondentId", m.correspondentId || "");
        data.append("documentTypeId", m.documentTypeId || "");
        data.append("projectId", m.projectId || "");
        data.append("tagIds", m.tagIds || "");
        data.append("title", m.title || "");
    }

    function clearSummary() {
        var summary = document.getElementById("upload-summary");
        if (summary) { summary.remove(); }
    }

    function summarize(results) {
        var created = results.filter(function (i) { return i.status === "created"; }).length;
        var dupes = results.filter(function (i) { return i.status === "duplicate"; }).length;
        var failed = results.filter(function (i) { return i.status === "failed"; }).length;
        var summary = document.getElementById("upload-summary");
        if (!summary) {
            summary = document.createElement("div");
            summary.id = "upload-summary";
            summary.className = "form-summary";
            document.querySelector(".add-doc__actions").parentNode.insertBefore(summary, document.querySelector(".add-doc__actions"));
        }
        var parts = [];
        if (created > 0) { parts.push(fill(t("summary-added", "{0} added"), created)); }
        if (dupes > 0) { parts.push(fill(t("summary-duplicates", "{0} duplicate(s) skipped"), dupes)); }
        if (failed > 0) { parts.push(fill(t("summary-failed", "{0} failed"), failed)); }
        summary.innerHTML = parts.join(", ") +
            (created > 0 ? '. <a href="' + inboxUrl + '">' + t("review-link", "Review them in your inbox →") + "</a>" : ".");
    }

    async function runUploads() {
        if (uploading) { return; }
        var pending = queue.filter(function (i) { return i.status === "queued"; });
        var hasScan = scan && scan.status === "queued";
        if (pending.length === 0 && !hasScan) { return; }

        var single = pending.length + (hasScan ? 1 : 0) === 1;
        var m = meta(single);
        uploading = true;
        submit.disabled = true;
        var results = [];

        for (var i = 0; i < pending.length; i++) {
            (function (item) {
                item._m = m;
            })(pending[i]);
            await send(ajaxUrl, function (data) { data.append("file", pending[i].file); addMeta(data, m); }, pending[i]);
            results.push(pending[i]);
        }

        if (hasScan) {
            // the scan gets a row of its own once it is being sent
            var row = document.createElement("li");
            row.className = "upload-item upload-item--queued";
            row.innerHTML = '<div class="upload-item__head"><span class="upload-item__name"></span><span class="upload-item__size"></span></div>' +
                '<div class="upload-item__bar"><span class="upload-item__fill"></span></div><div class="upload-item__status"></div>';
            row.querySelector(".upload-item__name").textContent = fill(t("scan-pages", "Scan: {0} page(s)"), scan.files.length);
            list.hidden = false;
            list.appendChild(row);
            scan.row = row;
            await send(scanUrl, function (data) {
                scan.files.forEach(function (f) { data.append("images", f); });
                addMeta(data, m);
            }, scan);
            results.push(scan);
            if (scan.status === "created") {
                window.MatPaperScanner.reset();
                scanBox.hidden = true;
            }
        }

        uploading = false;
        summarize(results);
        refresh();
    }

    // ---- drag & drop + file picker ------------------------------------------

    if (browse) { browse.addEventListener("click", function () { input.click(); }); }
    if (dropzone) {
        dropzone.addEventListener("click", function (e) { if (e.target === browse) { return; } input.click(); });
        ["dragenter", "dragover"].forEach(function (evt) {
            dropzone.addEventListener(evt, function (e) { e.preventDefault(); dropzone.classList.add("uploader__dropzone--active"); });
        });
        ["dragleave", "drop"].forEach(function (evt) {
            dropzone.addEventListener(evt, function (e) { e.preventDefault(); dropzone.classList.remove("uploader__dropzone--active"); });
        });
        dropzone.addEventListener("drop", function (e) {
            if (e.dataTransfer && e.dataTransfer.files) { addFiles(e.dataTransfer.files); }
        });
    }

    input.addEventListener("change", function () {
        addFiles(input.files);
        input.value = ""; // allow re-selecting the same file
    });

    form.addEventListener("submit", function (e) {
        e.preventDefault();
        runUploads();
    });

    refresh();
})();
