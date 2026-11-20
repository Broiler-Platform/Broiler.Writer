# Writer editor layout refactor

## Decision

Share layout geometry and editor interaction first. Then move paragraph composition into a headless Documents component used by both views. Continuous flow and paginated flow remain separate placement policies. Continuous view wraps to the editor's available width and scrolls through the result. Paginated view wraps to the document's page and column widths and applies page breaks, section rules, running content, and other page constraints.

The first deliverable is consistent editing behavior over the two existing layouts. A single paragraph composer is a later deliverable with its own compatibility gates. The print path stays on `DocumentPaginator` throughout the migration.

## Current boundary

| Area | Continuous view | Paginated view |
| --- | --- | --- |
| Editing state | `StandardRichEdit` owns selection, document edits, undo, clipboard, and text services | `PageLayoutContent` hosts and delegates edits to that same instance |
| Composition and paint | `RichEditLayout` wraps into `VisualLine` records and `RichEditPainter` paints them | `DocumentPaginator` produces positioned pages for `StandardPrintPreview`; Writer adds editing overlays |
| Interaction geometry | `RichEditLayout` resolves visual lines, caret positions, and selection spans | `WriterPageOccurrences` reconstructs these from `LayoutLine` and `LayoutPiece`; Writer converts between page and control coordinates |
| Viewport | RichEdit's scrolling and wrapping policies | Current page, page-local scrolling, page navigation, and zoom transform |

There is already one editing engine. The duplication is in layout and geometry-dependent interaction. For example, paginated Up/Down enumerates page lines and can fall through to RichEdit's continuous navigation when it cannot resolve the move. Home/End already delegate to the continuous editor's visual lines even when the page is visible. `WriterPageOccurrences` also infers source spans from display text and remeasures pieces. A display string can differ from the source after shaping, capitalization, or hyphenation. Selection and hit testing need the geometry of the line that was actually laid out.

The main paginated editor, Page Layout dialog, and print preview share the paginator, with different presentation settings. The main editor uses a graphics scene that includes nonprinting shapes and maps rendered objects back to the editable document. Keep that identity mapping and the dialog's separate preview and draft lifecycle during the migration.

## Geometry contract

Start with the queries that editing consumes. Once the first migration proves the shape of the API, put the editor-facing contract alongside `StandardRichEdit` in Broiler.UI. Keep it internal while both adapters can be tested together and expose only the injection point and types Writer needs.

| Query | Result |
| --- | --- |
| Hit test a point in a visible fragment | Body caret, inline object, or no editable hit. The caller chooses exact versus nearest hit. |
| Resolve a caret or selection | Fragment and bounds for the caret, or clipped rectangles per visible fragment for the range. |
| Resolve a visual line and adjacent line | Fragment, source span, and ordered caret stops. Used for visual Home/End and Up/Down with a preferred inline coordinate. |

Each geometry snapshot is bound to the exact document revision and settings that produced it. A fragment is a page for paginated view and the flow region for continuous view. Use fragment-local coordinates and one transform per visible fragment for paint, pointer input, accessibility bounds, and IME caret placement. During adaptation, page coordinates remain in points and continuous coordinates in logical pixels. The common composer will use points, with pixel conversion, zoom, and scrolling applied by the view. Existing continuous reflow on zoom or resize still derives its logical wrap width from the viewport.

Use document positions and source ranges for edits. Pair a caret with a fragment or visual line hint and boundary affinity so one source offset at a soft wrap, page break, or bidi boundary can resolve to the intended visual position. Repeated header rows and running content can have multiple occurrences and must keep their source story identity. Navigation follows explicit reading order. Inline images, tabs, and rendered note markers have atomic source spans where applicable. Generated glyphs with no source character must provide a boundary or have no editable stop.

The viewport owns scrolling, page stepping, and reveal. The editor owns focus, gestures, selection updates, edit commands, and popup capture. Layout data carries no focus, scroll, or undo state. If a geometry query cannot resolve a position, use an explicit policy in the active view and avoid forwarding the gesture to the hidden layout. Lay out and publish a new snapshot together with its source mapping, then retire the previous page resources after the preview switches to the new snapshot. Selection-only updates may reveal the caret and repaint overlays using the same snapshot.

## Migration

1. **Index paginated body lines in Writer.** Build the index when `PageLayoutContent` installs a new page snapshot. Store ordered occurrences and look them up by page and source paragraph. Extract the existing Up/Down and caret reveal queries into this index. Route unmodified visual Home/End and Up/Down, including Shift extension, through the visible paginated layout. Leave Ctrl+Home/End as document commands and PageUp/PageDown as page navigation. Use existing geometry for this change, with explicit behavior at unsupported hits and document boundaries. This slice fits in Writer and works with the current packages.
2. **Share editor interaction across the two adapters.** Introduce the Broiler.UI geometry contract and adapt `RichEditLayout` first, then the Writer index. Move geometry-dependent keyboard navigation, pointer selection, caret reveal requests, and context menu placement behind the active adapter in separate changes. Use the same context menu command and enablement logic in both views. Check dismissal, focus restoration, and popup paint order before removing Writer's separate menu path. Integrate one adapter change at a time so the existing view can still be used as a comparison.
3. **Produce source geometry during composition.** In Broiler.Documents, carry source spans and cluster boundaries from shaping and line breaking into positioned output, including clipping and fragment identity. Reuse the existing `Broiler.Documents.Model.Text.ShapedCluster` source contract where appropriate. Derive caret stops and selection rectangles from the same advances used to paint. Migrate the paginated adapter away from substring matching and post-layout measurement. Verify representative print geometry and render output are unchanged when adding this metadata.
4. **Share paragraph composition.** Extract line breaking and line metrics from the existing Documents composer behind a headless API. It takes resolved styles, text and inline objects, font measurement, and a line width or available band. It returns composed lines with source mapping. Start with ordinary paragraphs and run the same composer under existing page placement and continuous placement. Add tabs, lists, tables and cell clipping, wrapping around objects, and other special cases in separate slices. Keep the old continuous path selectable during migration and switch defaults only after the full supported behavior and performance checks pass. Keep export and page-only placement rules with the paginator.

Broiler.Documents owns composition and source geometry. Broiler.UI owns layout queries as consumed by the control and the interaction logic. Writer owns view mode, page preview integration, and page commands. Keep the UI contract independent of the pagination package so the interaction migration can ship before sharing composition. Validate cross-repository changes using candidate packages and the existing UI source build option. Advance published dependency versions together where required, and test the Writer build with and without page structure enabled.

## Verification and rollout

- **First slice:** use a fixture where the same paragraph wraps differently in the two layouts. Check Home/End and Shift extension against the visible paginated line, Up/Down across a page break and at document boundaries, and correct caret reveal after a page switch. Assert that moving the selection doesn't rebuild pages or add an undo entry.
- **Shared interaction:** verify hit-to-caret round trips and clipped selection bounds at different zoom levels, in table cells, around inline images, and across wraps. Exercise grapheme boundaries, transformed text, repeated rows, and bidi/vertical text where supported. Test context menu selection preservation, dismissals, and clipboard enablement and execution in both views.
- **Host and print checks:** run Writer, RichEdit, and Documents pagination suites for the affected slices. Exercise desktop and browser pointer, keyboard, clipboard, and IME, plus compact/touch selection and caret placement. Verify mode changes preserve document, selection, undo, and zoom. Compare preview and print against existing page, section, running-content, table, shape, and printability fixtures.
- **Performance:** record first layout, edit-to-paint latency, navigation time, and peak memory on a long document with tables and images, on desktop and WebAssembly. Compare against the current implementations before enabling the shared path. Measure full repagination separately. Incremental repagination is follow-up work if measurements justify it.

The first slice is a focused Writer change. Shared interaction requires a Broiler.UI release. Sharing composition is a separate cross-repository project because line breaks, font metrics, and object placement affect editing and print fidelity. Each earlier slice remains useful if composition migration is deferred.

The shared composer fits measured text and inline objects into lines and breaks oversized words at grapheme boundaries. It yields each line before requesting the next width, so both views can wrap around objects using their own vertical placement. Mixed styles, list paragraphs, table cell content, and explicit line breaks can use the shared path. Table and page placement stay with their respective views. The views also share tab-stop measurements. Continuous RichEdit resolves tab widths in the composer; pagination retains its Word-compatible wrapping policies for tabs and shrinking justified spaces. The paginator uses the shared break handling for discretionary hyphens. Continuous RichEdit falls back when it cannot render that break faithfully.

Run `eng/test-documents-candidate.ps1` in PowerShell 7 with `-CheckUiPackages` to pack Documents and RichEdit into a local feed and test Writer with package references. The script checks the restored packages against the files it just built. `-CheckSourceGeometry -CheckSharedComposition` still exercises both views with Documents packages and the sibling Broiler.UI checkout. The default rollout depends on compatible published packages and a physical touch check.

In Windows pagination, DirectWrite supplies the visual edges for bidirectional runs. Writer retains the edge nearest the pointer or keyboard navigation target when a source offset has two possible visual edges. Other renderers still need to expose their visual edges, and continuous RichEdit still uses its existing caret geometry. The paginator lays out body text horizontally even when `VerticalWriting` is set. Vertical caret placement requires the paginator and painter to lay out and draw vertical body columns first.

The candidate passed the paginator, RichEdit, and Writer suites, built for Windows, Linux, and Android, and published locally for WebAssembly. The browser smoke test passed in Chromium, Firefox, and WebKit. Chromium also exercised keyboard navigation, context-menu Copy, and composition commits in continuous and paginated views.

In a local comparison using 350 body paragraphs, 35 tables, and 35 images, the first desktop run measured initial layout at about 33 ms with legacy composition and 22 ms with the shared path. Edit-to-frame took about 35 ms and 27 ms, respectively. In Chromium, the medians for the same document were about 791 and 445 ms to open, and 508 and 168 ms to edit and present a frame. Browser open timing includes file reading and decoding.

In a repeat with a fresh desktop process for each mode, peak working set was 73.36 MiB with legacy composition and 70.23 MiB with shared composition. Managed allocations were about 82 and 85 MiB, and the legacy path was faster for initial layout and editing in that repeat. Across two fresh Chromium launches per mode, renderer-process peak working set was 246.61–248.91 MiB with legacy composition and 230.93–232.83 MiB with shared composition. These figures cover one fixture, and the Chromium measurement covers renderer processes only. No physical touch device was available for validation.
