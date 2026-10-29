# Browser-local recovery

The WASM edition's **Recovery** panel checkpoints modified content in IndexedDB.
It uses a separate identity for each page and document, so concurrent tabs cannot
overwrite one another. Reloading or reopening lists drafts for explicit restore;
it never replaces the current document automatically.

A checkpoint waits 500 ms after changes, with a 2-second maximum wait during
continuous typing, subject to browser/main-thread/storage availability. Hidden
page and pagehide events request another flush. **Saved** means an IndexedDB
transaction committed; later edits are marked pending. Browser data may still be
evicted or unavailable. Export a separate document/recovery bundle as a backup.

Writer uses bounded RTF through its existing codec and resource context and checks
that text round-trips before storing it. Conversion notes remain visible. This is
not a lossless serialization of every rich document-model feature; complex layout
may have notes or refuse checkpointing. Keep the original-format export when
needed. Undo history and caret/selection are not persisted.

Restore validates before replacing content, marks the result modified, and creates
a separate identity while retaining the original draft. It first checkpoints
current modified content and refuses replacement if that write fails. The panel
supports direct bundle export even if local writes fail, bundle import, confirmed
draft deletion/clear and a best-effort persistent-storage request. It retains
migration backups for separate bundle export/import. Preferences contain only the
automatic-checkpoint switch. Local drafts are not cloud-synchronized or encrypted
by application code. They remain until deleted or reclaimed by the browser.

The app keeps up to 30 drafts without automatically evicting old ones. When the
limit or browser quota is reached, it retains editing content and offers export
and deletion controls. Disabling checkpoints leaves existing drafts intact. Clear
local data leaves the open document intact and pauses checkpoints for that page.

The enclosing Office checkout has `tests/office-web/verify-w5.ps1` and
`docs/broiler-office-web-w5.md`. Verification includes exact published artifacts,
forced termination of test-owned browser processes, persistent-profile restart,
tab isolation, migration rollback, storage failures and independent RTF checks.
