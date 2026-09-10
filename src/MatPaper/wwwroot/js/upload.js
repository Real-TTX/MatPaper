// Progressive-enhancement uploader: drag & drop, multiple files, per-file
// progress. Falls back to a plain multipart form POST when JavaScript is off.
(function () {
    "use strict";

    var form = document.getElementById("upload-form");
    if (!form) {
        return;
    }

    var ajaxUrl = form.getAttribute("data-ajax-url");
    var inboxUrl = form.getAttribute("data-inbox-url") || "/Inbox";
    var input = document.getElementById("Files");
    var dropzone = document.getElementById("uploader-dropzone");
    var browse = document.getElementById("uploader-browse");
    var list = document.getElementById("upload-list");
    var submit = document.getElementById("upload-submit");
    var storageSelect = document.getElementById("StorageLocationId");

    // XHR upload with progress is required; if unavailable, keep the plain form.
    if (!ajaxUrl || !input || !window.FormData || !window.XMLHttpRequest) {
        return;
    }

    var queue = [];
    var nextId = 1;
    var uploading = false;
    var uploader = document.getElementById("uploader");
    if (uploader) {
        uploader.classList.add("uploader--enhanced");
    }

    function token() {
        var el = form.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : "";
    }

    function formatSize(bytes) {
        if (bytes < 1024) { return bytes + " B"; }
        if (bytes < 1024 * 1024) { return (bytes / 1024).toFixed(0) + " KB"; }
        return (bytes / (1024 * 1024)).toFixed(1) + " MB";
    }

    function addFiles(fileList) {
        for (var i = 0; i < fileList.length; i++) {
            var file = fileList[i];
            var item = { id: nextId++, file: file, status: "queued", row: null };
            queue.push(item);
            renderRow(item);
        }
        if (queue.length > 0) {
            list.hidden = false;
        }
    }

    function renderRow(item) {
        var row = document.createElement("li");
        row.className = "upload-item upload-item--queued";
        row.innerHTML =
            '<div class="upload-item__head">' +
            '<span class="upload-item__name"></span>' +
            '<span class="upload-item__size"></span>' +
            '</div>' +
            '<div class="upload-item__bar"><span class="upload-item__fill"></span></div>' +
            '<div class="upload-item__status">Queued</div>';
        row.querySelector(".upload-item__name").textContent = item.file.name;
        row.querySelector(".upload-item__size").textContent = formatSize(item.file.size);
        list.appendChild(row);
        item.row = row;
    }

    function setProgress(item, pct) {
        var fill = item.row.querySelector(".upload-item__fill");
        if (fill) { fill.style.width = pct + "%"; }
    }

    function setStatus(item, status, text) {
        item.status = status;
        item.row.className = "upload-item upload-item--" + status;
        item.row.querySelector(".upload-item__status").textContent = text;
    }

    function uploadOne(item) {
        return new Promise(function (resolve) {
            var data = new FormData();
            data.append("file", item.file);
            data.append("storageLocationId", storageSelect ? storageSelect.value : "");

            var xhr = new XMLHttpRequest();
            xhr.open("POST", ajaxUrl, true);
            xhr.setRequestHeader("RequestVerificationToken", token());
            setStatus(item, "uploading", "Uploading…");

            xhr.upload.onprogress = function (e) {
                if (e.lengthComputable) {
                    setProgress(item, Math.round((e.loaded / e.total) * 100));
                }
            };
            xhr.onload = function () {
                setProgress(item, 100);
                var res = null;
                try { res = JSON.parse(xhr.responseText); } catch (err) { res = null; }
                if (xhr.status >= 200 && xhr.status < 300 && res) {
                    if (res.status === "created") {
                        setStatus(item, "created", "Added");
                    } else if (res.status === "duplicate") {
                        setStatus(item, "duplicate", "Duplicate — skipped");
                    } else {
                        setStatus(item, "failed", res.message || "Failed");
                    }
                } else {
                    setStatus(item, "failed", "Failed (" + xhr.status + ")");
                }
                resolve();
            };
            xhr.onerror = function () {
                setStatus(item, "failed", "Network error");
                resolve();
            };
            xhr.send(data);
        });
    }

    function summarize() {
        var created = queue.filter(function (i) { return i.status === "created"; }).length;
        var dupes = queue.filter(function (i) { return i.status === "duplicate"; }).length;
        var failed = queue.filter(function (i) { return i.status === "failed"; }).length;

        var summary = document.getElementById("upload-summary");
        if (!summary) {
            summary = document.createElement("div");
            summary.id = "upload-summary";
            summary.className = "form-summary";
            list.parentNode.insertBefore(summary, list.nextSibling);
        }
        var parts = [];
        if (created > 0) { parts.push(created + " added"); }
        if (dupes > 0) { parts.push(dupes + " duplicate(s) skipped"); }
        if (failed > 0) { parts.push(failed + " failed"); }
        summary.innerHTML = parts.join(", ") +
            (created > 0 ? '. <a href="' + inboxUrl + '">Review them in your inbox →</a>' : ".");
    }

    async function runUploads() {
        if (uploading) { return; }
        var pending = queue.filter(function (i) { return i.status === "queued"; });
        if (pending.length === 0) { return; }

        uploading = true;
        submit.disabled = true;
        for (var i = 0; i < pending.length; i++) {
            await uploadOne(pending[i]);
        }
        uploading = false;
        submit.disabled = false;
        summarize();
    }

    // Wire up drag & drop + file picker.
    if (browse) {
        browse.addEventListener("click", function () { input.click(); });
    }
    if (dropzone) {
        dropzone.addEventListener("click", function (e) {
            if (e.target === browse) { return; }
            input.click();
        });
        ["dragenter", "dragover"].forEach(function (evt) {
            dropzone.addEventListener(evt, function (e) {
                e.preventDefault();
                dropzone.classList.add("uploader__dropzone--active");
            });
        });
        ["dragleave", "drop"].forEach(function (evt) {
            dropzone.addEventListener(evt, function (e) {
                e.preventDefault();
                dropzone.classList.remove("uploader__dropzone--active");
            });
        });
        dropzone.addEventListener("drop", function (e) {
            if (e.dataTransfer && e.dataTransfer.files) {
                addFiles(e.dataTransfer.files);
            }
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
})();
