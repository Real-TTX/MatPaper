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
        search.setAttribute("placeholder", "Search…");
        search.setAttribute("autocomplete", "off");
        header.appendChild(search);

        var list = document.createElement("ul");
        list.className = "mp-picker-dialog__list";

        var footer = document.createElement("div");
        footer.className = "mp-picker-dialog__footer";
        var done = document.createElement("button");
        done.type = "button";
        done.className = "btn btn--primary mp-picker-dialog__done";
        done.textContent = "Done";
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

    function renderList(dialog, filter) {
        var mp = dialog._mp;
        var active = mp.active;
        if (!active) {
            return;
        }

        var needle = (filter || "").toLowerCase();
        var list = mp.list;
        list.innerHTML = "";

        if (!active.multiple) {
            list.appendChild(buildSingleRow(dialog, {
                v: "",
                t: active.placeholder,
                clear: true
            }, isEmpty(active.selected)));
        }

        for (var i = 0; i < active.options.length; i++) {
            var opt = active.options[i];
            if (needle && opt.t.toLowerCase().indexOf(needle) === -1) {
                continue;
            }
            var isSel = active.selected[opt.v] === true;
            if (active.multiple) {
                list.appendChild(buildMultiRow(active, opt, isSel));
            } else {
                list.appendChild(buildSingleRow(dialog, opt, isSel));
            }
        }
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
            chips.appendChild(buildChip(name, order[k]));
        }
        dialog._mp.active = null;
    }

    function buildChip(name, opt) {
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
        remove.setAttribute("aria-label", "Remove");
        remove.setAttribute("tabindex", "-1");
        remove.textContent = "✕";

        chip.appendChild(hidden);
        chip.appendChild(text);
        chip.appendChild(remove);
        return chip;
    }
})();
