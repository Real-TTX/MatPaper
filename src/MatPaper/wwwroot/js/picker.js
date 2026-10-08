// mp-picker: searchable single/multi select backed by a shared native <dialog>.
// Binding is done entirely through hidden inputs rendered server-side by PickerTagHelper,
// so this script only needs to keep those inputs (and the visible chips/label) in sync.
// No external dependencies. If <dialog>.showModal is unavailable the widgets are left
// untouched and their hidden inputs still post their current values.
(function () {
    "use strict";

    if (typeof document === "undefined") {
        return;
    }

    function ready(fn) {
        if (document.readyState === "loading") {
            document.addEventListener("DOMContentLoaded", fn);
        } else {
            fn();
        }
    }

    ready(function () {
        var supportsDialog = typeof HTMLDialogElement !== "undefined" &&
            typeof HTMLDialogElement.prototype.showModal === "function";
        if (!supportsDialog) {
            return; // Hidden inputs already carry the current values; nothing to enhance.
        }

        var dialog = buildDialog();
        document.body.appendChild(dialog);

        var widgets = document.querySelectorAll(".mp-picker");
        for (var i = 0; i < widgets.length; i++) {
            wireWidget(widgets[i], dialog);
        }
    });

    // --- Shared dialog -----------------------------------------------------

    function buildDialog() {
        var dialog = document.createElement("dialog");
        dialog.className = "mp-picker-dialog";

        var header = document.createElement("div");
        header.className = "mp-picker-dialog__header";
        var search = document.createElement("input");
        search.type = "text";
        search.className = "mp-picker-dialog__search";
        // Labels come from the first widget on the page (server-side translated).
        var labelSource = document.querySelector(".mp-picker");
        search.setAttribute("placeholder",
            (labelSource && labelSource.getAttribute("data-search-label")) || "Search…");
        search.setAttribute("autocomplete", "off");
        header.appendChild(search);

        // A way out that does not depend on a keyboard: full-screen on a phone there is no backdrop to tap.
        var closeBtn = document.createElement("button");
        closeBtn.type = "button";
        closeBtn.className = "icon-btn mp-picker-dialog__close";
        closeBtn.setAttribute("aria-label", (labelSource && labelSource.getAttribute("data-close-label")) || "Close");
        closeBtn.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18"/></svg>';
        closeBtn.addEventListener("click", function () { dialog._mp.active = null; dialog.close(); });
        header.appendChild(closeBtn);

        var list = document.createElement("ul");
        list.className = "mp-picker-dialog__list";

        var footer = document.createElement("div");
        footer.className = "mp-picker-dialog__footer";
        var done = document.createElement("button");
        done.type = "button";
        done.className = "btn btn--primary mp-picker-dialog__done";
        done.textContent = (labelSource && labelSource.getAttribute("data-done-label")) || "Done";
        footer.appendChild(done);

        dialog.appendChild(header);
        dialog.appendChild(list);
        dialog.appendChild(footer);

        dialog._mp = { search: search, list: list, footer: footer, done: done, active: null };

        // Filter as the user types.
        search.addEventListener("input", function () {
            renderList(dialog, search.value);
        });

        // Backdrop click closes without applying.
        dialog.addEventListener("click", function (ev) {
            if (ev.target === dialog) {
                dialog.close();
            }
        });

        // Escape closes without applying (native "cancel").
        dialog.addEventListener("cancel", function () {
            dialog._mp.active = null;
        });

        done.addEventListener("click", function () {
            applyMultiple(dialog);
            dialog.close();
        });

        return dialog;
    }

    // --- Widget wiring -----------------------------------------------------

    function wireWidget(widget, dialog) {
        var isMultiple = widget.getAttribute("data-multiple") === "true";
        var triggers = widget.querySelectorAll(".mp-picker__trigger");
        for (var i = 0; i < triggers.length; i++) {
            triggers[i].addEventListener("click", function () {
                openFor(widget, dialog);
            });
        }

        if (isMultiple) {
            var chips = widget.querySelector(".mp-picker__chips");
            if (chips) {
                chips.addEventListener("click", function (ev) {
                    var btn = ev.target.closest(".mp-picker__chip-remove");
                    if (btn) {
                        var chip = btn.closest(".mp-picker__chip");
                        if (chip) {
                            chip.parentNode.removeChild(chip);
                            notifyChanged(widget);
                        }
                    }
                });
            }
        }
    }

    function readOptions(widget) {
        var script = widget.querySelector(".mp-picker__data");
        if (!script) {
            return [];
        }
        try {
            return JSON.parse(script.textContent) || [];
        } catch (e) {
            return [];
        }
    }

    function currentSelection(widget) {
        var isMultiple = widget.getAttribute("data-multiple") === "true";
        var set = Object.create(null);
        if (isMultiple) {
            var inputs = widget.querySelectorAll(".mp-picker__chip input[type=hidden]");
            for (var i = 0; i < inputs.length; i++) {
                set[inputs[i].value] = true;
            }
        } else {
            var hidden = widget.querySelector(".mp-picker__value");
            if (hidden && hidden.value !== "") {
                set[hidden.value] = true;
            }
        }
        return set;
    }

    // --- Open / render -----------------------------------------------------

    function openFor(widget, dialog) {
        // Dialog labels follow the widget (each carries the server-side translation).
        var searchLabel = widget.getAttribute("data-search-label");
        if (searchLabel) { dialog._mp.search.setAttribute("placeholder", searchLabel); }
        var doneLabel = widget.getAttribute("data-done-label");
        if (doneLabel) { dialog._mp.done.textContent = doneLabel; }

        var mp = dialog._mp;
        mp.active = {
            widget: widget,
            multiple: widget.getAttribute("data-multiple") === "true",
            options: readOptions(widget),
            selected: currentSelection(widget),
            placeholder: widget.getAttribute("data-placeholder") || ""
        };

        dialog.classList.toggle("mp-picker-dialog--multiple", mp.active.multiple);
        mp.footer.style.display = mp.active.multiple ? "" : "none";
        mp.search.value = "";
        renderList(dialog, "");
        dialog.showModal();
        mp.search.focus();
    }

    // Rows built at once: a long list stays fast, the search narrows it down.
    var MAX_ROWS = 60;

    function renderList(dialog, filter) {
        var mp = dialog._mp;
        var active = mp.active;
        if (!active) {
            return;
        }

        var needle = (filter || "").toLowerCase();
        var list = mp.list;
        list.innerHTML = "";
        var shown = 0;
        var hidden = 0;
        var exact = false;

        if (!active.multiple) {
            list.appendChild(buildSingleRow(dialog, {
                v: "",
                t: active.placeholder,
                clear: true
            }, isEmpty(active.selected)));
        }

        for (var i = 0; i < active.options.length; i++) {
            var opt = active.options[i];
            var lower = opt.t.toLowerCase();
            if (needle && lower === needle) { exact = true; }
            if (needle && lower.indexOf(needle) === -1) {
                continue;
            }
            if (shown >= MAX_ROWS) {
                hidden++;
                continue;
            }
            shown++;
            var isSel = active.selected[opt.v] === true;
            if (active.multiple) {
                list.appendChild(buildMultiRow(active, opt, isSel));
            } else {
                list.appendChild(buildSingleRow(dialog, opt, isSel));
            }
        }

        if (hidden > 0) {
            var more = document.createElement("li");
            more.className = "mp-picker-dialog__more";
            more.textContent = ((active.widget.getAttribute("data-more-label")) || "{0} more").replace("{0}", String(hidden));
            list.appendChild(more);
        }

        // "+ create": a name the list does not have yet.
        var kind = active.widget.getAttribute("data-create");
        if (kind) {
            var typed = (filter || "").trim();
            var li = document.createElement("li");
            if (typed && !exact) {
                li.className = "mp-picker-dialog__item mp-picker-dialog__create";
                li.textContent = "+ " + ((active.widget.getAttribute("data-create-label")) || 'Create "{0}"').replace("{0}", typed);
                li.addEventListener("click", function () { createEntry(dialog, kind, typed, li); });
                list.insertBefore(li, list.firstChild);
            } else if (!typed) {
                li.className = "mp-picker-dialog__more";
                li.textContent = "+ " + (active.widget.getAttribute("data-create-hint") || "Type a name to create it.");
                list.appendChild(li);
            }
        }
    }

    function createEntry(dialog, kind, name, row) {
        var active = dialog._mp.active;
        if (!active) { return; }
        var tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
        var body = new FormData();
        body.append("__RequestVerificationToken", tokenEl ? tokenEl.value : "");
        body.append("kind", kind);
        body.append("name", name);
        row.classList.add("is-busy");
        fetch("/QuickCreate", { method: "POST", body: body, credentials: "same-origin" })
            .then(function (r) { return r.json(); })
            .then(function (result) {
                if (!result || !result.ok || !dialog._mp.active) { row.classList.remove("is-busy"); return; }
                var opt = { v: String(result.id), t: result.name };
                // Every picker of this kind on the page learns the new entry.
                var peers = document.querySelectorAll('.mp-picker[data-create="' + kind + '"]');
                for (var i = 0; i < peers.length; i++) {
                    var data = peers[i].querySelector(".mp-picker__data");
                    if (!data) { continue; }
                    var list = [];
                    try { list = JSON.parse(data.textContent) || []; } catch (e) { list = []; }
                    if (!list.some(function (o) { return o.v === opt.v; })) {
                        list.push(opt);
                        data.textContent = JSON.stringify(list);
                    }
                }
                if (!active.options.some(function (o) { return o.v === opt.v; })) { active.options.push(opt); }
                if (active.multiple) {
                    active.selected[opt.v] = true;
                    dialog._mp.search.value = "";
                    renderList(dialog, "");
                } else {
                    commitSingle(dialog, opt);
                    dialog.close();
                }
            })
            .catch(function () { row.classList.remove("is-busy"); });
    }

    function isEmpty(set) {
        for (var k in set) {
            if (Object.prototype.hasOwnProperty.call(set, k)) {
                return false;
            }
        }
        return true;
    }

    function buildSingleRow(dialog, opt, isSel) {
        var li = document.createElement("li");
        li.className = "mp-picker-dialog__item" +
            (isSel ? " is-selected" : "") +
            (opt.clear ? " mp-picker-dialog__item--clear" : "");
        li.setAttribute("role", "option");
        li.textContent = opt.t;
        li.addEventListener("click", function () {
            commitSingle(dialog, opt);
            dialog.close();
        });
        return li;
    }

    function buildMultiRow(active, opt, isSel) {
        var li = document.createElement("li");
        li.className = "mp-picker-dialog__item" + (isSel ? " is-selected" : "");

        var label = document.createElement("label");
        label.className = "mp-picker-dialog__check";

        var cb = document.createElement("input");
        cb.type = "checkbox";
        cb.value = opt.v;
        cb.checked = isSel;
        cb.addEventListener("change", function () {
            // Persist into the selection model so filtering never drops picks.
            if (cb.checked) {
                active.selected[opt.v] = true;
            } else {
                delete active.selected[opt.v];
            }
            li.classList.toggle("is-selected", cb.checked);
        });

        var text = document.createElement("span");
        text.textContent = opt.t;

        label.appendChild(cb);
        label.appendChild(text);
        li.appendChild(label);
        return li;
    }

    // --- Apply -------------------------------------------------------------

    // The picker writes its hidden input(s) programmatically, which does not fire the
    // usual events. Emit a bubbling "change" from the widget so pages can react to a
    // pick like they would to any other form control.
    function notifyChanged(widget) {
        if (!widget) {
            return;
        }
        try {
            widget.dispatchEvent(new Event("change", { bubbles: true }));
        } catch (e) {
            var ev = document.createEvent("Event");
            ev.initEvent("change", true, false);
            widget.dispatchEvent(ev);
        }
    }

    // Sets a single picker from code (the "suggest" button): learns the option, shows it, tells the page.
    window.MpPicker = {
        select: function (widget, opt) {
            var data = widget.querySelector(".mp-picker__data");
            if (data) {
                var list = [];
                try { list = JSON.parse(data.textContent) || []; } catch (e) { list = []; }
                if (!list.some(function (o) { return o.v === opt.v; })) { list.push(opt); data.textContent = JSON.stringify(list); }
            }
            var hidden = widget.querySelector(".mp-picker__value");
            var trigger = widget.querySelector(".mp-picker__trigger");
            var labelEl = trigger ? trigger.querySelector(".mp-picker__label") : null;
            if (hidden) { hidden.value = opt.v; }
            if (labelEl) { labelEl.textContent = opt.t; }
            if (trigger) { trigger.removeAttribute("data-placeholder-shown"); }
            notifyChanged(widget);
        }
    };

    function commitSingle(dialog, opt) {
        var active = dialog._mp.active;
        if (!active) {
            return;
        }
        var widget = active.widget;
        var hidden = widget.querySelector(".mp-picker__value");
        var trigger = widget.querySelector(".mp-picker__trigger");
        var labelEl = trigger ? trigger.querySelector(".mp-picker__label") : null;

        var value = opt.clear ? "" : opt.v;
        if (hidden) {
            hidden.value = value;
        }
        if (labelEl) {
            labelEl.textContent = value === "" ? active.placeholder : opt.t;
        }
        if (trigger) {
            if (value === "") {
                trigger.setAttribute("data-placeholder-shown", "true");
            } else {
                trigger.removeAttribute("data-placeholder-shown");
            }
        }
        dialog._mp.active = null;
        notifyChanged(widget);
    }

    function applyMultiple(dialog) {
        var active = dialog._mp.active;
        if (!active || !active.multiple) {
            return;
        }
        var widget = active.widget;
        var chips = widget.querySelector(".mp-picker__chips");
        if (!chips) {
            return;
        }

        var name = widget.getAttribute("data-name") || "";
        var order = [];
        // Build from the selection MODEL (not the filtered DOM) so a search filter never
        // drops previously-selected items; option order keeps chips stable.
        for (var j = 0; j < active.options.length; j++) {
            if (active.selected[active.options[j].v] === true) {
                order.push(active.options[j]);
            }
        }

        chips.innerHTML = "";
        for (var k = 0; k < order.length; k++) {
            chips.appendChild(buildChip(name, order[k], widget));
        }
        dialog._mp.active = null;
        notifyChanged(widget);
    }

    function buildChip(name, opt, widget) {
        var chip = document.createElement("span");
        chip.className = "mp-picker__chip";
        chip.setAttribute("data-value", opt.v);

        var hidden = document.createElement("input");
        hidden.type = "hidden";
        hidden.name = name;
        hidden.value = opt.v;

        var text = document.createElement("span");
        text.className = "mp-picker__chip-text";
        text.textContent = opt.t;

        var remove = document.createElement("button");
        remove.type = "button";
        remove.className = "mp-picker__chip-remove";
        remove.setAttribute("aria-label",
            (widget && widget.getAttribute("data-remove-label")) || "Remove");
        remove.setAttribute("tabindex", "-1");
        remove.textContent = "✕";

        chip.appendChild(hidden);
        chip.appendChild(text);
        chip.appendChild(remove);
        return chip;
    }

    // Correspondent suggestions: names proposed from the document's text. A click takes one (an existing entry
    // is selected, a new name is created first). The inbox asks once for the whole page and shows them right away.
    function suggestWidget(btn) {
        var scope = btn.closest("[data-doc-id], form, body");
        return (scope || document).querySelector('.mp-picker[data-create="correspondent"]');
    }

    function renderSuggestions(btn, items) {
        var box = btn.parentNode.querySelector(".suggest-box");
        var widget = suggestWidget(btn);
        if (!box || !widget) { return; }
        box.innerHTML = "";
        box.hidden = false;
        if (!items || items.length === 0) {
            var none = document.createElement("span");
            none.className = "inbox-suggest__note";
            none.textContent = btn.getAttribute("data-none") || "No suggestion found.";
            box.appendChild(none);
            return;
        }
        items.forEach(function (item) {
            var chip = document.createElement("button");
            chip.type = "button";
            chip.className = "suggest-chip" + (item.id ? " is-known" : "");
            chip.textContent = (item.id ? "" : "+ ") + (item.existing || item.name);
            chip.addEventListener("click", function () {
                if (item.id) {
                    window.MpPicker.select(widget, { v: String(item.id), t: item.existing || item.name });
                    box.hidden = true;
                    return;
                }
                var tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
                var body = new FormData();
                body.append("__RequestVerificationToken", tokenEl ? tokenEl.value : "");
                body.append("kind", "correspondent");
                body.append("name", item.name);
                chip.disabled = true;
                fetch("/QuickCreate", { method: "POST", body: body, credentials: "same-origin" })
                    .then(function (r) { return r.json(); })
                    .then(function (created) {
                        if (created && created.ok) {
                            window.MpPicker.select(widget, { v: String(created.id), t: created.name });
                            box.hidden = true;
                        } else { chip.disabled = false; }
                    })
                    .catch(function () { chip.disabled = false; });
            });
            box.appendChild(chip);
        });
    }

    document.addEventListener("click", function (ev) {
        var btn = ev.target.closest ? ev.target.closest("[data-suggest-correspondent]") : null;
        if (!btn) { return; }
        ev.preventDefault();
        btn.disabled = true;
        fetch("/QuickCreate?handler=Suggest&documentId=" + encodeURIComponent(btn.getAttribute("data-suggest-correspondent")), { credentials: "same-origin" })
            .then(function (r) { return r.json(); })
            .then(function (result) { btn.disabled = false; renderSuggestions(btn, (result && result.items) || []); })
            .catch(function () { btn.disabled = false; });
    });

    ready(function () {
        var buttons = Array.prototype.slice.call(document.querySelectorAll(".inbox-item:not(.is-processing) [data-suggest-correspondent]"));
        if (buttons.length === 0) { return; }
        var ids = buttons.map(function (b) { return b.getAttribute("data-suggest-correspondent"); });
        fetch("/QuickCreate?handler=SuggestMany&ids=" + encodeURIComponent(ids.join(",")), { credentials: "same-origin" })
            .then(function (r) { return r.json(); })
            .then(function (result) {
                var docs = (result && result.documents) || {};
                buttons.forEach(function (btn) {
                    btn.hidden = true;
                    renderSuggestions(btn, docs[btn.getAttribute("data-suggest-correspondent")] || []);
                });
            })
            .catch(function () {
                // Could not ask: the button is there for a manual try.
                buttons.forEach(function (btn) {
                    btn.hidden = false;
                    var box = btn.parentNode.querySelector(".suggest-box");
                    if (box) { box.innerHTML = ""; }
                });
            });
    });
})();
