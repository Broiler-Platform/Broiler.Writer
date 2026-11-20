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

Writer first tries a bounded RTF checkpoint. If it detects a partial conversion
or formatting loss, it tries a DOCX checkpoint using the same resource permissions.
DOCX preserves basic bullet and numbered lists that the current RTF codec drops.
Before storing either format, Writer reads the candidate back and checks text,
paragraph structure, selected formatting and image limits. If both candidates
fail, the last good draft stays available. Existing RTF drafts remain readable.
Recovery bundles with the versioned DOCX format require a Writer version that
supports it. Recovery covers a subset of the rich document model and does not
persist undo history or caret/selection.

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
Writer's `eng/web/writer-recovery-smoke.mjs` additionally runs against the
extracted web artifact in CI. It checks legacy RTF migration, DOCX restoration
after reload, corrupt-bundle rejection and an aborted IndexedDB write.
