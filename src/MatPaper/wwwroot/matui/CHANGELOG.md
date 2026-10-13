# Changelog of MatUi

The toolkit that the Mat apps copy: the CSS (`matui.css`), the script (`matui.js`, `matui.boot.js`) and the icon sprite (`matui-icons.svg`).
This file says what changed in which version, so that an app that carries a copy knows what it takes with an update and what it has to do.
How the numbers work, and how an app updates: [docs/VERSIONING.md](docs/VERSIONING.md).

The format is [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) with two sections of our own:

- **Breaking**: what an app has to change in its own code or markup when it takes the version (a class that was renamed or removed, a changed `data-` attribute, a changed behaviour that apps rely on).
- **Migration**: the steps for it. A version with a Breaking section needs a Migration section.

A change that an app can see is written down under **Unreleased** in the pull request that makes it. `node tools/bump.js <patch|minor|major>` turns Unreleased into the next version.

## [Unreleased]

### Added

- A menu in the switcher can have a start page (`SHELL-15`): an entry that is a link (`<a class="mat-menu__item" href="…" role="menuitemradio" data-nav-set="id" data-nav-title="…">`) loads its page instead of swapping the items, and the page that loads shows its own menu. Without `href` an entry is a button and swaps the items as before; `matui.js` leaves a link alone (a click with a modifier key opens a new tab and changes nothing here), and the Space key chooses a link in an open menu as it does a button. For an app whose menus are areas with a page each (mail, administration, account), where swapping the items would leave the content of the old area on the screen. `NavSet.Href` in the shell model of this site; a menu with a start page needs no hidden panel in the pages of the other menus.
- The toolkit has a version of its own (`MatUi.version`, the first line of `matui.css` and the header of `matui.js`, `/ui/manifest.json`, `/api/version`), this changelog (also at `/ui/CHANGELOG.md`, `/api/changelog` and on the Changelog page) and a guide for updating an app (docs/VERSIONING.md), with `tools/bump.js` that makes a version and `tools/matui-check.js` that compares an app's copy with the toolkit. The archive an app copies holds the changelog and `matui.manifest.json` with the SHA-256 of every file.
- Menu switcher (`SHELL-15`): it can sit under the logo, at the foot of the menu directly above the user, at both places (`mat-nav__switch--foot`, `data-menu-place="top"`) or at the end of the menu, after the panels (`mat-nav__switch--end`), and menus can be grouped below a parent in its popup (`data-nav-group`, `data-nav-parent`, `mat-menu__parent`, `mat-menu__sub`).
- A main action in the menu: `mat-nav__item--primary`, the first item, filled with the accent, one per app (`SHELL-19`); the main action of the header bar is one icon in the accent colour, `mat-btn--primary mat-btn--icon` last in `mat-topbar__actions`, and a second action beside it is `mat-btn--secondary mat-btn--icon` (`SHELL-17`); the gap and the missing negative margin of the bar apply to both (`:has(> .mat-btn--primary, > .mat-btn--secondary)`).
- Colour schemes (`COLOR-09`): `data-scheme="paper|contrast|oled"` on `<html>` next to `data-theme`, an opt-in change of the surfaces only (the accent never changes); `MatUi.scheme.get/set`, `data-scheme-set`, `data-scheme-save-url`, the event `mat:scheme`, and `matui.boot.js` applies the saved scheme before the first paint. The standard look is unchanged.
- A select can create what is missing and suggest (`FORM-16`): `data-create-url` (a first row Create "text") and `data-suggest-url` (Suggestions above All), both opt-in, with `MatUi.picker.set`, `MatUi.picker.addOption` and `MatUi.picker.suggest`.
- Colours a user chose (`COLOR-10`): `data-colour="#rrggbb"` on `mat-tag` (a dot before the text), the new `mat-dot`, `mat-icon` and an option of a select picker; `MatUi.refresh(scope)` for markup added later.
- Tag input: `data-picker="tags"`, a field of chips with suggestions (`FORM-15`).
- Dropdown with a list of checkboxes: `data-picker="select" data-multiple` with `data-select-all` (`FORM-15`).
- Appointment picker: `data-picker="slot"` shows the days that have a free time and then the times of the chosen day; what is taken is marked or hidden (`data-unavailable`), `data-min`, `data-max` and `data-min-notice` keep out the past, Next free appointment, and availability that is loaded per month (`mat:slots-needed`, `MatUi.picker.setSlots`). It posts `2026-10-14T09:30` (`FORM-18`).
- Calendar: `mat-calendar` with `data-calendar`, a calendar that is always shown, for one day, a period or several days, with minimum and maximum, disabled dates and weekdays, events with an agenda and week numbers (`FORM-18`).
- The date pickers have a jump to a month and a year, and `data-min` and `data-max` also take `today`, `tomorrow`, `+2d`, `+3m` and the like.
- Schedule: `mat-schedule` with `data-schedule`, the events one below another in the views Agenda, Day, Week, Month and Year, with calendar weeks, paging by buttons, keys and swipe, and events that are loaded for the visible period (`LIST-14`).
- Lists of rows: `mat-itemlist` and `mat-item` for contacts, messages, settings, files, tasks and the like, with sliding actions, reordering, a filter and select all (`LIST-15`).
- Carousel: `mat-carousel` with `data-carousel`, `data-loop` and `data-autoplay` (`LIST-13`).
- Groups in a table: `tbody.mat-group` with a heading row that opens and closes the group (`data-groups`, `data-group`, `data-group-toggle`, `data-group-fixed`, `data-groups-set`), the rows hanging on a line and the add row (`mat-group__add`) as the last row of the group; the choice is remembered (`LIST-16`).
- Number stepper: `mat-stepper` with `data-stepper` (`FORM-20`).
- Slider and range slider with two thumbs: `mat-slider` with `data-slider` (`FORM-17`).
- Code input: `mat-otp` with `data-otp`, for one-time codes and PINs (`FORM-19`).

### Changed

- `MatUi.version` is the version of the toolkit (a string such as `0.1.0`); it used to be the fixed string `1`.
- The first line of `matui.css` is `/* MatUi <version> (<hash>) - build <build> - ... */`; the build that it used to name first is now second.
- The panels of the pickers only fade in: they no longer start lower and smaller and grow into place (`mat-picker-in` replaces `mat-menu-in` for them).
- The date pickers and the calendar draw their months with the same code; nothing that an app writes has changed.
- The label of the theme button, AM and PM of a 12-hour time and the hint of a typed time are texts of `PK_TEXT` in English and German, chosen by `<html lang>`, and can be changed with `data-theme-label`, `data-theme-name-system|light|dark` and `data-pk-<name>` on `<html>`; they were English only.
- `data-selectable` also marks cards and tiles: the item of a ticked check is `is-selected` (its table row, its `mat-tile`, or the nearest element with `data-select-item`), a tint behind the whole item.
- `--mat-accent-text` is mixed with `--mat-text` by `--mat-accent-text-keep` (100 % in the standard look, so nothing changes there; a colour scheme sets that one share).
- A toast is shown above an open dialog, inside the topmost one, instead of behind it.

### Fixed

- The index of a long page ("On this page") can be scrolled by the user again: it followed the page on every scroll event and pulled itself back.
- The `code` inside a `.mat-code` block has no padding of its own: the padding of an inline code moved the first line of a block to the right.
- A submit button with `formnovalidate` sends the form without the check of the toolkit, as in the browser (`FORM-05`), also after a `data-confirm` on the form, which now submits again with the button that was pressed.

## [0.1.0]

The toolkit as the apps copy it before it had a version: the shell with the menu (badges, submenus, switcher), the header bar and the footer; buttons, forms and the pickers (select with pictures, date and period, time and time range, colour); lists with toolbar, table and pager; cards, tabs, dialogs, alerts, toasts and the other content components; the layout system and the utilities; the icon sprite and the optional icon library; the tokens for light and dark and the accent of every app.

### Added

- The baseline of the changelog. Every later version says what changed from this one.
