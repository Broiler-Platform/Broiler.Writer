# Picture selection and paginated editing

The former main RichEdit surface treated an image click as a text-caret hit.
It also used continuous layout: its paper grew vertically, while Page Layout
and printing used the document paginator. Enabling page-structure commands
did not enable pagination in that surface.

The main editor uses the same paginator as Page Layout and printing. RichEdit remains the document editing
engine, including undo, clipboard, formatting, and text services. Its continuous
surface is not displayed. Pages update after edits, and selection navigation
reveals the page containing the caret. The mouse wheel scrolls within a page
before changing pages; Previous/Next and PageUp/PageDown navigate pages.
Shift-wheel scrolls horizontally, and the existing zoom controls scale the page.

View > Pagination switches the main editor between paginated and continuous
layout on desktop and in the browser. Pagination starts enabled. The choice
lasts for the current application session; switching views preserves document
content, selection, zoom and undo history. Printing and the Page Layout dialog
continue to paginate in either mode.

Clicking an inline picture selects its entire object character, outlines the
picture, and selects its `[Image]` token in Formatting Codes. Activating the
picture token also selects the picture. The Formatting Codes pane heading shows
the selected picture's dimensions in points, payload type and size, name,
alternative text, and missing-payload status where available. Floating pictures
can also be selected and outlined in the main editor; their metadata appears as
`[Floating Image ...]` in the pane heading. The canonical FormatCodes projection
still represents inline pictures as `[Image]` and does not project floating
objects as body-text tokens.

Regression coverage includes picture clicks on later pages and at different
zoom levels, compact mode, selection from picture codes, floating pictures,
automatic and explicit pagination, undo, scrolling, typing and drag selection.
Rendering tests account for the page-to-screen transform and verify picture
pixels. Editing-view notes no longer report the main editor as continuous.
