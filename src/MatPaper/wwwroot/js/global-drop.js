// Drop files anywhere: dragging files over any page of the app shows a drop target for the whole window; letting go
// sends them to the inbox in the background (PDF, images, XML, or a ZIP archive with them - the server opens it).
// A small panel in the corner shows the progress of each file and what became of it. A folder is read, too.
//
// On the "Add document" page the files join the list there instead (window.MatPaperUpload, see upload.js): that is
// where the details are filled in. A drop that another script has already handled (the drop zone of that page) is
// left alone.
(function () {
    "use strict";

    var config = document.getElementById("drop-i18n");
    if (!config || !window.FormData || !window.XMLHttpRequest) { return; }

    var url = config.getAttribute("data-url") || "/Documents/Upload?handler=Ajax";
    var inboxUrl = config.getAttribute("data-inbox") || "/Inbox";
    var MAX_FILES = 500;
    var usePopover = typeof HTMLElement !== "undefined" && "showPopover" in HTMLElement.prototype;

    function t(name, fallback) { return config.getAttribute("data-t-" + name) || fallback; }
    function fill(template, value) { return template.replace("{0}", value); }

    function token() {
        var el = document.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : "";
    }

    function formatSize(bytes) {
        if (bytes < 1024) { return bytes + " B"; }
        if (bytes < 1024 * 1024) { return Math.round(bytes / 1024) + " KB"; }
        return (bytes / (1024 * 1024)).toFixed(1) + " MB";
    }

    // ---- the drop target ------------------------------------------------------------------------------------------

    var overlay = null;
    var tray = null;

    function element(tag, className, text) {
        var el = document.createElement(tag);
        if (className) { el.className = className; }
        if (text) { el.textContent = text; }
        return el;
    }

    // A popover sits in the top layer, so the target and the panel are seen above an open dialog, too.
    function present(el, visible) {
        if (usePopover) {
            try {
                if (visible) { if (!el.matches(":popover-open")) { el.showPopover(); } }
                else if (el.matches(":popover-open")) { el.hidePopover(); }
                return;
            } catch (e) { /* fall through to the plain way */ }
        }
        el.hidden = !visible;
    }

    function buildOverlay() {
        overlay = element("div", "drop-overlay");
        overlay.setAttribute("aria-hidden", "true");
        if (usePopover) { overlay.setAttribute("popover", "manual"); } else { overlay.hidden = true; }
        overlay.innerHTML =
            '<div class="drop-overlay__card">' +
            '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><path d="M7 9l5-5 5 5" /><path d="M12 4v12" /></svg>' +
            '<strong class="drop-overlay__title"></strong>' +
            '<span class="drop-overlay__hint"></span>' +
            '</div>';
        overlay.querySelector(".drop-overlay__title").textContent = t("title", "Drop files to add them");
        overlay.querySelector(".drop-overlay__hint").textContent = t("hint", "PDF, images, XML or ZIP - they land in your inbox");
        document.body.appendChild(overlay);
    }

    function hasFiles(event) {
        var types = event.dataTransfer && event.dataTransfer.types;
        return !!types && Array.prototype.indexOf.call(types, "Files") >= 0;
    }

    var depth = 0;

    window.addEventListener("dragenter", function (event) {
        if (!hasFiles(event)) { return; }
        event.preventDefault();
        depth++;
        if (!overlay) { buildOverlay(); }
        present(overlay, true);
    });

    window.addEventListener("dragover", function (event) {
        if (!hasFiles(event)) { return; }
        event.preventDefault(); // the page accepts the drop
        if (event.dataTransfer) { event.dataTransfer.dropEffect = "copy"; }
    });

    window.addEventListener("dragleave", function (event) {
        if (!hasFiles(event) || !overlay) { return; }
        depth = Math.max(0, depth - 1);
        // leaving the window altogether: the pointer is gone (some browsers report 0,0 and no target)
        if (depth === 0 || (!event.relatedTarget && event.clientX === 0 && event.clientY === 0)) {
            depth = 0;
            present(overlay, false);
        }
    });

    window.addEventListener("drop", function (event) {
        if (!hasFiles(event)) { return; }
        depth = 0;
        if (overlay) { present(overlay, false); }
        // a script of the page has handled it (a drop zone): it is theirs
        if (event.defaultPrevented) { return; }
        event.preventDefault();

        // The list of dropped items is only valid while this event runs: take what is in it now, read folders after.
        var loose = [];
        var folders = [];
        var items = event.dataTransfer.items;
        if (items && items.length && typeof items[0].webkitGetAsEntry === "function") {
            for (var i = 0; i < items.length; i++) {
                if (items[i].kind !== "file") { continue; }
                var entry = items[i].webkitGetAsEntry();
                if (entry && entry.isDirectory) { folders.push(entry); }
                else {
                    var file = items[i].getAsFile();
                    if (file) { loose.push(file); }
                }
            }
        } else {
            loose = Array.prototype.slice.call(event.dataTransfer.files);
        }

        readFolders(folders).then(function (inFolders) {
            var files = loose.concat(inFolders);
            if (files.length === 0) { return; }
            var tooMany = files.length > MAX_FILES;
            if (tooMany) { files = files.slice(0, MAX_FILES); }
            handOver(files, tooMany);
        });
    });

    // ---- folders --------------------------------------------------------------------------------------------------

    function isJunk(name) {
        return name.charAt(0) === "." || name.indexOf("~$") === 0 || /^(thumbs\.db|desktop\.ini)$/i.test(name);
    }

    function readAll(reader) {
        return new Promise(function (resolve) {
            var all = [];
            (function more() {
                reader.readEntries(function (batch) {
                    if (!batch.length) { resolve(all); return; }
                    all = all.concat(Array.prototype.slice.call(batch));
                    more();
                }, function () { resolve(all); });
            })();
        });
    }

    function readEntry(entry, out) {
        if (out.length > MAX_FILES || isJunk(entry.name)) { return Promise.resolve(); }
        if (entry.isFile) {
            return new Promise(function (resolve) {
                entry.file(function (file) { out.push(file); resolve(); }, function () { resolve(); });
            });
        }
        return readAll(entry.createReader()).then(function (children) {
            return children.reduce(function (chain, child) {
                return chain.then(function () { return readEntry(child, out); });
            }, Promise.resolve());
        });
    }

    function readFolders(folders) {
        var out = [];
        return folders.reduce(function (chain, folder) {
            return chain.then(function () { return readEntry(folder, out); });
        }, Promise.resolve()).then(function () { return out; });
    }

    // ---- where the files go ---------------------------------------------------------------------------------------

    function handOver(files, tooMany) {
        if (window.MatPaperUpload && typeof window.MatPaperUpload.addFiles === "function") {
            window.MatPaperUpload.addFiles(files);
            return;
        }
        enqueue(files, tooMany);
    }

    // ---- the panel with the progress ------------------------------------------------------------------------------

    var queue = [];
    var running = false;

    function buildTray() {
        tray = element("div", "drop-tray");
        tray.setAttribute("role", "status");
        tray.setAttribute("aria-live", "polite");
        if (usePopover) { tray.setAttribute("popover", "manual"); } else { tray.hidden = true; }
        tray.innerHTML =
            '<div class="drop-tray__head"><strong class="drop-tray__title"></strong><span class="drop-tray__count"></span>' +
            '<button type="button" class="icon-btn drop-tray__close"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18" /></svg></button></div>' +
            '<ul class="drop-tray__list"></ul>' +
            '<div class="drop-tray__foot" hidden></div>';
        tray.querySelector(".drop-tray__close").setAttribute("aria-label", t("close", "Close"));
        tray.querySelector(".drop-tray__close").addEventListener("click", function () { present(tray, false); });
        document.body.appendChild(tray);
    }

    function rowFor(item) {
        var row = element("li", "drop-tray__item drop-tray__item--queued");
        row.innerHTML = '<div class="drop-tray__line"><span class="drop-tray__name"></span><span class="drop-tray__size"></span></div>' +
            '<div class="drop-tray__bar"><span class="drop-tray__fill"></span></div><div class="drop-tray__status"></div>';
        row.querySelector(".drop-tray__name").textContent = item.file.name;
        row.querySelector(".drop-tray__name").title = item.file.name;
        row.querySelector(".drop-tray__size").textContent = formatSize(item.file.size);
        row.querySelector(".drop-tray__status").textContent = t("queued", "Queued");
        item.row = row;
        return row;
    }

    function setStatus(item, status, text, href) {
        item.status = status;
        item.row.className = "drop-tray__item drop-tray__item--" + status;
        var cell = item.row.querySelector(".drop-tray__status");
        cell.textContent = text;
        if (href) {
            var link = element("a", "", t("show-existing", "Show existing document"));
            link.href = href; link.target = "_blank"; link.rel = "noopener";
            cell.appendChild(document.createTextNode(" "));
            cell.appendChild(link);
        }
    }

    function updateHead() {
        var done = queue.filter(function (i) { return i.status !== "queued" && i.status !== "uploading"; }).length;
        tray.querySelector(".drop-tray__title").textContent = running || done < queue.length ? t("adding", "Adding documents …") : t("done", "Done");
        tray.querySelector(".drop-tray__count").textContent = done + " / " + queue.length;
    }

    function enqueue(files, tooMany) {
        if (!tray) { buildTray(); }
        // a new drop after a finished one starts a fresh list
        if (!running && queue.every(function (i) { return i.status !== "queued" && i.status !== "uploading"; })) {
            queue = [];
            tray.querySelector(".drop-tray__list").innerHTML = "";
            tray.querySelector(".drop-tray__foot").hidden = true;
        }

        var list = tray.querySelector(".drop-tray__list");
        files.forEach(function (file) {
            var item = { file: file, status: "queued", row: null, docs: { created: 0, duplicates: 0, failed: 0 } };
            queue.push(item);
            list.appendChild(rowFor(item));
        });
        if (tooMany) {
            var note = element("li", "drop-tray__note", fill(t("too-many", "Only the first {0} files are added."), MAX_FILES));
            list.appendChild(note);
        }
        present(tray, true);
        updateHead();
        next();
    }

    function next() {
        if (running) { return; }
        var item = queue.filter(function (i) { return i.status === "queued"; })[0];
        if (!item) { finish(); return; }
        running = true;
        send(item).then(function () { running = false; updateHead(); next(); });
    }

    function send(item) {
        return new Promise(function (resolve) {
            var data = new FormData();
            data.append("file", item.file);
            var xhr = new XMLHttpRequest();
            xhr.open("POST", url, true);
            xhr.setRequestHeader("RequestVerificationToken", token());
            setStatus(item, "uploading", t("uploading", "Uploading…"));
            updateHead();

            var fillBar = item.row.querySelector(".drop-tray__fill");
            xhr.upload.onprogress = function (e) {
                if (e.lengthComputable) { fillBar.style.width = Math.round((e.loaded / e.total) * 100) + "%"; }
            };
            xhr.upload.onload = function () {
                fillBar.style.width = "100%";
                item.row.querySelector(".drop-tray__status").textContent = t("processing", "Processing …");
            };
            xhr.onload = function () {
                fillBar.style.width = "100%";
                var res = null;
                try { res = JSON.parse(xhr.responseText); } catch (err) { res = null; }
                if (xhr.status >= 200 && xhr.status < 300 && res) {
                    if (res.archive) {
                        item.docs = { created: res.created || 0, duplicates: res.duplicates || 0, failed: res.failed || 0 };
                        var parts = [];
                        if (res.created > 0) { parts.push(fill(t("summary-added", "{0} added"), res.created)); }
                        if (res.duplicates > 0) { parts.push(fill(t("summary-duplicates", "{0} duplicate(s) skipped"), res.duplicates)); }
                        if (res.skipped > 0) { parts.push(fill(t("archive-skipped", "{0} file(s) are no documents"), res.skipped)); }
                        if (res.failed > 0) { parts.push(fill(t("summary-failed", "{0} failed"), res.failed)); }
                        var text = parts.join(", ");
                        if (res.status === "created" || res.status === "duplicate") { setStatus(item, res.status, text); }
                        else { setStatus(item, "failed", res.message || text || t("failed", "Failed")); }
                    } else if (res.status === "created") {
                        item.docs.created = 1;
                        setStatus(item, "created", t("added", "Added"));
                    } else if (res.status === "duplicate") {
                        item.docs.duplicates = 1;
                        setStatus(item, "duplicate", t("duplicate", "Duplicate — skipped"), res.id ? "/Documents/Edit?id=" + encodeURIComponent(res.id) : null);
                    } else {
                        item.docs.failed = 1;
                        setStatus(item, "failed", res.message || t("failed", "Failed"));
                    }
                } else {
                    item.docs.failed = 1;
                    setStatus(item, "failed", t("failed", "Failed") + " (" + xhr.status + ")");
                }
                resolve();
            };
            xhr.onerror = function () {
                item.docs.failed = 1;
                setStatus(item, "failed", t("network-error", "Network error"));
                resolve();
            };
            xhr.send(data);
        });
    }

    function finish() {
        updateHead();
        function sum(key) { return queue.reduce(function (n, i) { return n + (i.docs[key] || 0); }, 0); }
        var created = sum("created"), dupes = sum("duplicates"), failed = sum("failed");
        var parts = [];
        if (created > 0) { parts.push(fill(t("summary-added", "{0} added"), created)); }
        if (dupes > 0) { parts.push(fill(t("summary-duplicates", "{0} duplicate(s) skipped"), dupes)); }
        if (failed > 0) { parts.push(fill(t("summary-failed", "{0} failed"), failed)); }

        var foot = tray.querySelector(".drop-tray__foot");
        foot.innerHTML = "";
        foot.appendChild(element("span", "drop-tray__summary", parts.join(", ")));
        if (created > 0) {
            // on the inbox page the new documents are not in the list yet
            if (location.pathname.toLowerCase().indexOf("/inbox") === 0) {
                var reload = element("button", "btn btn--primary", t("reload", "Reload"));
                reload.type = "button";
                reload.addEventListener("click", function () { location.reload(); });
                foot.appendChild(reload);
            } else {
                var link = element("a", "btn btn--primary", t("to-inbox", "Review them in your inbox →"));
                link.href = inboxUrl;
                foot.appendChild(link);
            }
        }
        foot.hidden = false;
    }
})();
