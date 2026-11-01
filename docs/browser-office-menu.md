# Browser Office menu

The WebAssembly app's managed menu bar includes **Office**:

- **Back to Office** follows the existing Office link, including the website's
  `/demo/` return path. The browser's existing unsaved-changes prompt still applies.
- **Recovery** opens local checkpoint settings, status, draft restoration/deletion
  and recovery bundle import/export in an accessible modal panel.
- **Document information** (Writer) or **Spreadsheet information** (Cells) shows
  the document name, conversion notes/preview where supported, browser limits and
  unsupported features. Writer also offers **Save to file** from the Office menu;
  unsupported browsers receive an Export download instruction.

The old navigation/recovery/limits strips no longer occupy editing space after
startup. A short current-operation status remains visible. Recovery warnings and
storage failures show an attention button outside the menu, so failed checkpoint
storage cannot disappear inside a closed panel. Close or Escape returns focus to
the canvas; successful restoration closes the recovery panel. Existing confirmation
dialogs for destructive recovery actions are retained.

If the runtime fails, the plain Office navigation link is shown again. Desktop
Cells does not receive browser-only menu entries. The shared panel JS/CSS is
vendored identically in Writer and Cells to preserve independent releases.
