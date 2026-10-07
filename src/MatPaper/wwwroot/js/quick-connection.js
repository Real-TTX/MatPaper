// "New connection ..." inside a form: the connection selects named by the dialog (data-smb /
// data-mail / data-cloud) get one extra entry. Choosing it opens a small dialog; saving creates the
// connection through /System/Connections/Edit?handler=Quick and selects it - in this select and in
// every other select of the same kind on the page, so the user never leaves the form.
(function () {
    "use strict";

    var dialog = document.getElementById("quick-connection");
    if (!dialog || typeof dialog.showModal !== "function") { return; }

    var NEW = "__new__";
    var current = null; // { select, kind, previous }

    function names(attr) {
        return (dialog.getAttribute(attr) || "").split(",").filter(Boolean);
    }
    function t(key, fallback) { return dialog.getAttribute("data-t-" + key) || fallback; }
    function field(sel) { return dialog.querySelector(sel); }

    var KINDS = [
        { kind: "smb", names: names("data-smb") },
        { kind: "mail", names: names("data-mail") },
        { kind: "cloud", names: names("data-cloud") }
    ];

    function selectsOf(kind) {
        var list = [];
        KINDS.forEach(function (k) {
            if (k.kind !== kind) { return; }
            k.names.forEach(function (n) {
                var el = document.querySelector('select[name="' + n + '"]');
                if (el) { list.push(el); }
            });
        });
        return list;
    }

    function addNewOption(select) {
        var option = document.createElement("option");
        option.value = NEW;
        option.textContent = "＋ " + t("new", "New connection …");
        select.appendChild(option);
    }

    function showFor(kind) {
        dialog.querySelectorAll("[data-qc-only]").forEach(function (el) {
            el.hidden = el.getAttribute("data-qc-only") !== kind;
        });
        var cloud = kind === "cloud";
        field('[data-qc="fields"]').hidden = cloud;
        field('[data-qc="cloud"]').hidden = !cloud;
        field('[data-qc="save"]').hidden = cloud;
        field('[data-qc="errors"]').hidden = true;
    }

    function openFor(select, kind) {
        current = { select: select, kind: kind, previous: select.getAttribute("data-qc-previous") || "" };
        dialog.querySelectorAll("input:not([type=checkbox]), select").forEach(function (el) {
            if (el.name === "Input.Port") { el.value = "993"; }
            else if (el.name === "Input.Kind") { el.value = "1"; }
            else { el.value = ""; }
        });
        field("#qc-ssl").checked = true;
        showFor(kind);
        dialog.showModal();
        var first = dialog.querySelector(kind === "cloud" ? '[data-qc="cloud"] a' : "#qc-name");
        if (first) { first.focus(); }
    }

    function cancel() {
        if (current) { current.select.value = current.previous; }
        current = null;
        if (dialog.open) { dialog.close(); }
    }

    // The mailbox type and TLS switch change the usual port.
    function syncPort() {
        var imap = field("#qc-kind").value === "1";
        var ssl = field("#qc-ssl").checked;
        var port = field("#qc-port");
        var known = ["993", "143", "995", "110", ""];
        if (known.indexOf(port.value) !== -1) { port.value = imap ? (ssl ? "993" : "143") : (ssl ? "995" : "110"); }
    }
    field("#qc-kind").addEventListener("change", syncPort);
    field("#qc-ssl").addEventListener("change", syncPort);

    function save() {
        if (!current) { return; }
        var kind = current.kind;
        var token = document.querySelector('input[name="__RequestVerificationToken"]');
        var body = new FormData();
        body.append("__RequestVerificationToken", token ? token.value : "");
        body.append("Input.AuthMode", "0");
        body.append("Input.Kind", kind === "smb" ? "0" : field("#qc-kind").value);
        ["Input.Name", "Input.Host", "Input.Username", "Input.Password"].forEach(function (n) {
            body.append(n, dialog.querySelector('[name="' + n + '"]').value);
        });
        if (kind === "smb") {
            ["Input.Share", "Input.SmbPath", "Input.Domain"].forEach(function (n) {
                body.append(n, dialog.querySelector('[name="' + n + '"]').value);
            });
        } else {
            body.append("Input.Port", field("#qc-port").value || "993");
            body.append("Input.UseSsl", field("#qc-ssl").checked ? "true" : "false");
        }

        var saveBtn = field('[data-qc="save"]');
        var label = saveBtn.textContent;
        saveBtn.disabled = true;
        saveBtn.textContent = t("saving", "Saving …");
        var errors = field('[data-qc="errors"]');
        errors.hidden = true;

        fetch("/System/Connections/Edit?handler=Quick", { method: "POST", body: body, credentials: "same-origin" })
            .then(function (r) { return r.json(); })
            .then(function (result) {
                if (!result || !result.ok) {
                    errors.textContent = (result && result.errors && result.errors.join(" ")) || t("failed", "The connection could not be saved.");
                    errors.hidden = false;
                    return;
                }
                // Every select of this kind learns the new connection; the one that asked selects it.
                selectsOf(kind).forEach(function (select) {
                    var option = document.createElement("option");
                    option.value = String(result.id);
                    option.textContent = result.name;
                    var last = select.querySelector('option[value="' + NEW + '"]');
                    select.insertBefore(option, last);
                });
                current.select.value = String(result.id);
                current.select.setAttribute("data-qc-previous", String(result.id));
                current.select.dispatchEvent(new Event("change", { bubbles: true }));
                current = null;
                dialog.close();
            })
            .catch(function () {
                errors.textContent = t("failed", "The connection could not be saved.");
                errors.hidden = false;
            })
            .then(function () {
                saveBtn.disabled = false;
                saveBtn.textContent = label;
            });
    }

    dialog.addEventListener("click", function (event) {
        var act = event.target && event.target.closest ? event.target.closest("[data-qc]") : null;
        if (!act) { return; }
        var name = act.getAttribute("data-qc");
        if (name === "close") { event.preventDefault(); cancel(); }
        else if (name === "save") { event.preventDefault(); save(); }
    });
    dialog.addEventListener("cancel", function () { cancel(); });
    // Enter in a field saves instead of submitting the page's own form.
    dialog.querySelector("form").addEventListener("submit", function (event) { event.preventDefault(); });
    dialog.addEventListener("keydown", function (event) {
        if (event.key === "Enter" && event.target && event.target.tagName === "INPUT" && event.target.type !== "checkbox") {
            event.preventDefault();
            if (!field('[data-qc="save"]').hidden) { save(); }
        }
    });

    KINDS.forEach(function (k) {
        selectsOf(k.kind).forEach(function (select) {
            addNewOption(select);
            select.setAttribute("data-qc-previous", select.value);
            select.addEventListener("change", function () {
                if (select.value === NEW) {
                    openFor(select, k.kind);
                } else {
                    select.setAttribute("data-qc-previous", select.value);
                }
            });
        });
    });
})();
