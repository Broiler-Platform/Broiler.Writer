# Browser wheel scrolling and zoom

Vertical Ctrl+wheel intentionally changes Writer document zoom. A plain wheel
scrolls; horizontal motion is not used for document zoom. Trackpad pinch can
arrive as a wheel event with Ctrl set. Browser page zoom is separate.

Previously, the WebAssembly head remembered Ctrl from the last keyboard event.
Releasing Ctrl while another window or dialog owned focus could leave that state
set. Plain scrolling would then zoom until another keyboard event corrected it.

The wheel event now supplies its own ctrlKey through the JS/managed bridge.
There is no stored keyboard-derived Ctrl state in the browser zoom path.

Run tests/office-web/writer-wheel-zoom.mjs against a local Writer server using
WRITER_URL. It checks Ctrl-down, focus loss without key-up, plain scrolling,
explicit Ctrl+wheel and subsequent plain scrolling. Existing zoom unit tests
cover the steps and limits. Desktop input and Cells scrolling are unchanged.
