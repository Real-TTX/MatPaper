// "Add document": drag & drop files and/or scan with the camera, then fill in what is known and add.
// Falls back to a plain multipart form POST (files only) when JavaScript is off.
// The drop zone is the toolkit's (mat-dropzone: the file input covers it, so a click, a drop and the keyboard all end in a change of the input).
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
        list.hidden = queue.length === 0 && !(scan && scan.row);
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

    function rowHtml(removable) {
        return '<div class="mp-upload-item__head">' +
            '<span class="mp-upload-item__name"></span>' +
            '<span class="mp-upload-item__size"></span>' +
            (removable ? '<button type="button" class="mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm mp-upload-item__remove">' + window.MatPaper.icon("x") + '</button>' : '') +
            '</div>' +
            '<div class="mat-meter mat-meter--sm"><div class="mat-meter__fill"></div></div>' +
            '<div class="mp-upload-item__status"></div>';
    }

    function renderRow(item) {
        var row = document.createElement("li");
        row.className = "mp-upload-item mp-upload-item--queued";
        row.innerHTML = rowHtml(true);
        row.querySelector(".mp-upload-item__name").textContent = item.file.name;
        row.querySelector(".mp-upload-item__size").textContent = formatSize(item.file.size);
        row.querySelector(".mp-upload-item__status").textContent = t("queued", "Queued");
        var remove = row.querySelector(".mp-upload-item__remove");
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
        var bar = row.querySelector(".mat-meter__fill");
        if (bar) { bar.style.setProperty("--mat-meter", pct + "%"); }
    }

    function setStatus(holder, status, text, href, linkText, note) {
        holder.status = status;
        holder.row.className = "mp-upload-item mp-upload-item--" + status;
        var bar = holder.row.querySelector(".mat-meter__fill");
        if (bar) {
            if (status === "created" || status === "duplicate") { bar.setAttribute("data-tone", "ok"); }
            else if (status === "failed") { bar.setAttribute("data-tone", "error"); }
        }
        var cell = holder.row.querySelector(".mp-upload-item__status");
        cell.textContent = text;
        var remove = holder.row.querySelector(".mp-upload-item__remove");
        if (remove && status !== "queued") { remove.hidden = true; }
        if (href) {
            cell.appendChild(document.createTextNode(" "));
            var link = document.createElement("a");
            link.href = href; link.target = "_blank"; link.rel = "noopener";
            link.textContent = linkText || t("show-existing", "Show existing document");
            cell.appendChild(link);
        }
        // what the server has to say about the archive numbers (read from a sticker on the paper)
        if (note) {
            var line = document.createElement("span");
            line.className = "mp-upload-note";
            line.textContent = note;
            cell.appendChild(line);
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
            c.className = "mp-scan__thumb";
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
            xhr.upload.onload = function () {
                setProgress(holder.row, 100);
                var cell = holder.row.querySelector(".mp-upload-item__status");
                if (cell) { cell.textContent = t("processing", "Processing …"); }
            };
            xhr.onload = function () {
                setProgress(holder.row, 100);
                var res = null;
                try { res = JSON.parse(xhr.responseText); } catch (err) { res = null; }
                // how many documents this file stands for: one, or what an archive held
                holder.docs = { created: 0, duplicates: 0, failed: 0 };
                if (xhr.status >= 200 && xhr.status < 300 && res) {
                    if (res.archive) {
                        holder.docs = { created: res.created || 0, duplicates: res.duplicates || 0, failed: res.failed || 0 };
                        var parts = [];
                        if (res.created > 0) { parts.push(fill(t("summary-added", "{0} added"), res.created)); }
                        if (res.duplicates > 0) { parts.push(fill(t("summary-duplicates", "{0} duplicate(s) skipped"), res.duplicates)); }
                        if (res.skipped > 0) { parts.push(fill(t("archive-skipped", "{0} file(s) are no documents"), res.skipped)); }
                        if (res.failed > 0) { parts.push(fill(t("summary-failed", "{0} failed"), res.failed)); }
                        var text = parts.join(", ");
                        if (res.status === "created" || res.status === "duplicate") {
                            setStatus(holder, res.status, text, res.status === "duplicate" && res.id ? "/Documents/Edit?id=" + encodeURIComponent(res.id) : null, null, res.note);
                        } else {
                            setStatus(holder, "failed", res.message || text || t("failed", "Failed"));
                        }
                    } else if (res.status === "created") {
                        holder.docs.created = 1;
                        setStatus(holder, "created", t("added", "Added"), null, null, res.note);
                    } else if (res.status === "duplicate") {
                        holder.docs.duplicates = 1;
                        setStatus(holder, "duplicate", t("duplicate", "Duplicate — skipped"),
                            res.id ? "/Documents/Edit?id=" + encodeURIComponent(res.id) : null);
                    } else {
                        holder.docs.failed = 1;
                        setStatus(holder, "failed", res.message || t("failed", "Failed"));
                    }
                } else {
                    holder.docs.failed = 1;
                    setStatus(holder, "failed", t("failed", "Failed") + " (" + xhr.status + ")");
                }
                resolve();
            };
            xhr.onerror = function () { holder.docs = { created: 0, duplicates: 0, failed: 1 }; setStatus(holder, "failed", t("network-error", "Network error")); resolve(); };
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

    // What came of it: an alert above the buttons, with the way on to the inbox.
    function summarize(results) {
        function sum(key) { return results.reduce(function (n, i) { return n + ((i.docs && i.docs[key]) || 0); }, 0); }
        var created = sum("created");
        var dupes = sum("duplicates");
        var failed = sum("failed");
        clearSummary();
        var summary = document.createElement("div");
        summary.id = "upload-summary";
        summary.className = "mat-alert mat-alert--" + (failed > 0 ? "warning" : "success");
        summary.setAttribute("role", "status");
        var parts = [];
        if (created > 0) { parts.push(fill(t("summary-added", "{0} added"), created)); }
        if (dupes > 0) { parts.push(fill(t("summary-duplicates", "{0} duplicate(s) skipped"), dupes)); }
        if (failed > 0) { parts.push(fill(t("summary-failed", "{0} failed"), failed)); }
        summary.innerHTML = window.MatPaper.icon(failed > 0 ? "alert" : "check-circle") + '<div class="mat-alert__body"></div>';
        var body = summary.querySelector(".mat-alert__body");
        body.appendChild(document.createTextNode(parts.join(", ") + (created > 0 ? ". " : ".")));
        if (created > 0) {
            var link = document.createElement("a");
            link.href = inboxUrl;
            link.textContent = t("review-link", "Review them in your inbox →");
            body.appendChild(link);
        }
        var actions = form.querySelector(".mp-add__actions");
        actions.parentNode.insertBefore(summary, actions);
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
            await send(ajaxUrl, function (data) { data.append("file", pending[i].file); addMeta(data, m); }, pending[i]);
            results.push(pending[i]);
        }

        if (hasScan) {
            // the scan gets a row of its own once it is being sent
            var row = document.createElement("li");
            row.className = "mp-upload-item mp-upload-item--queued";
            row.innerHTML = rowHtml(false);
            row.querySelector(".mp-upload-item__name").textContent = fill(t("scan-pages", "Scan: {0} page(s)"), scan.files.length);
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

    // ---- file picker (the toolkit's zone passes a drop on to it) -------------

    input.addEventListener("change", function () {
        addFiles(input.files);
        input.value = ""; // allow re-selecting the same file
    });

    // Files dropped anywhere on this page (global-drop.js) join the list here instead of being sent at once:
    // this is the page where the details are filled in.
    window.MatPaperUpload = { addFiles: addFiles };

    form.addEventListener("submit", function (e) {
        e.preventDefault();
        runUploads();
    });

    refresh();
})();
