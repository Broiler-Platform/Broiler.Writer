# PDF import in browser Writer

The WebAssembly head registers the managed Broiler.Documents PDF reader directly.
Open accepts `.pdf` alongside the existing formats. Imported text can be edited;
supported PDF images, embedded font programs and ICC profiles use the same managed
services as desktop Writer. Diagnostics identify skipped or approximated content.
Import is a conversion to the Writer document model, not an exact PDF viewer.

The existing browser limits apply: 8 MiB input, 250,000 text characters, 10,000
paragraphs, and decoded images up to 16 MP each, 32 MP total and 8192 pixels per
side. Rejected imports leave the current document unchanged. Decoded images use
the same render path as desktop and are encoded when exporting to editable formats.

Export after opening a PDF asks for an editable filename, defaulting to `.rtf`.
PDF export remains disabled, matching the desktop release policy. Password-entry
and retry UI, OCR for scanned pages, exact-layout roundtripping, printing and PDF
export are not implemented. Encrypted or restricted documents that cannot be read
with the reader's default credentials report diagnostics without replacement.

Publish Writer with Web selected and deploy the website after publishing the
producer release. The codec is referenced by this head; shared Writer defaults
and Android do not acquire PDF support implicitly.
