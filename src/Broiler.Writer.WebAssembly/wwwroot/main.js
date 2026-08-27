import { dotnet } from './_framework/dotnet.js';

// Page glue for Broiler Writer in WebAssembly. The managed side owns rendering (it presents straight
// to #writer-canvas through the direct-Canvas 2D backend), so this module only bridges input,
// animation-frame scheduling, resize, cursor, caret-driven text, clipboard, and the browser-native
// open (file picker) and save (download) — the browser sandbox has no ambient file system.

const CURSORS = {
    Arrow: 'default', Text: 'text', Hand: 'pointer', Wait: 'wait', Move: 'move',
    ResizeHorizontal: 'ew-resize', ResizeVertical: 'ns-resize',
};

// Ctrl/Cmd chords the managed editor consumes; prevent the browser's own default so they reach it.
// Copy/Cut/Paste (C/X/V) are deliberately excluded so the trusted clipboard events still fire.
const EDITOR_CTRL_KEYS = new Set(['b', 'i', 'u', 'z', 'y', 'a', 's', 'o']);

let writerExports;
let canvas;
let textInput;
let host;
let statusLine;
let filePicker;
let started = false;
let frameRequest = 0;
let frameInProgress = false;
let observedWidth = 1280;
let observedHeight = 800;
let nativeCompositionActive = false;
let suppressNextInput = false;
let caretActive = false;
let focusedIsText = false;
const activePointers = new Set();
const listenerCleanups = [];

function prefersDark() { return matchMedia('(prefers-color-scheme: dark)').matches; }
function prefersReducedMotion() { return matchMedia('(prefers-reduced-motion: reduce)').matches; }

function setStatus(text, failed) {
    if (!statusLine) return;
    statusLine.textContent = text;
    if (failed) statusLine.dataset.state = 'failed';
    else delete statusLine.dataset.state;
}

function showError(message) {
    const panel = document.getElementById('error-panel');
    const body = document.getElementById('error');
    if (body) body.textContent = message;
    if (panel) panel.hidden = false;
    setStatus('Managed failure — see details below.', true);
    console.error(message);
}

function scheduleFrame() {
    if (!started || frameRequest !== 0 || frameInProgress) return;
    frameRequest = requestAnimationFrame(() => {
        frameRequest = 0;
        frameInProgress = true;
        try {
            writerExports.RenderUiFrame();
        } finally {
            frameInProgress = false;
        }
    });
}

function addListener(target, type, listener, options) {
    target.addEventListener(type, listener, options);
    listenerCleanups.push(() => target.removeEventListener(type, listener, options));
}

function localPoint(event) {
    const rect = canvas.getBoundingClientRect();
    return {
        x: (event.clientX - rect.left) * observedWidth / Math.max(1, rect.width),
        y: (event.clientY - rect.top) * observedHeight / Math.max(1, rect.height),
    };
}

function keyboardModifiers(event) {
    return (event.shiftKey ? 1 : 0) |
        (event.ctrlKey ? 2 : 0) |
        (event.altKey ? 4 : 0) |
        (event.metaKey ? 8 : 0);
}

function canonicalKey(key) {
    if (key === ' ') return 'Space';
    if (key === 'Esc') return 'Escape';
    if (key.startsWith('Arrow')) return key.slice(5);
    return key || 'Unknown';
}

function dispatchKey(event, down) {
    const key = canonicalKey(event.key);
    if (nativeCompositionActive && (event.isComposing || key === 'Process')) return;
    if ((event.ctrlKey || event.metaKey) && EDITOR_CTRL_KEYS.has(key.toLowerCase())) {
        // Keep the browser's Save-page / open / formatting defaults from firing; the managed editor
        // (and the app-level Ctrl+S / Ctrl+O accelerators) handle these instead.
        event.preventDefault();
    } else if (!event.ctrlKey && !event.metaKey &&
        ['Tab', 'Space', 'Up', 'Down', 'Left', 'Right', 'Home', 'End', 'Backspace', 'Delete', 'Enter'].includes(key)) {
        event.preventDefault();
    }
    writerExports.UiKeyboardKey(
        key, down, keyboardModifiers(event), event.keyCode || event.which || 0,
        Boolean(event.repeat), Number(event.location || 0), event.timeStamp);
}

// Managed → page: reposition the hidden editor under the managed caret so IME candidate windows land
// in the right place, and keep it focused while a text control owns focus.
function publishFrame(
    frameIndex, active, caretX, caretY, caretWidth, caretHeight,
    caretIndex, selectionStart, selectionLength, isText, statusText, darkTheme) {
    // The very first managed frame is presented inside StartAsync, before ready() wires the DOM.
    if (!started) return;
    caretActive = active;
    focusedIsText = isText;
    setStatus(statusText || '');
    if (active) {
        textInput.style.left = `${Math.max(0, caretX)}px`;
        textInput.style.top = `${Math.max(0, caretY)}px`;
        textInput.style.width = `${Math.max(1, caretWidth)}px`;
        textInput.style.height = `${Math.max(8, caretHeight)}px`;
    }
    const active2 = document.activeElement;
    if (isText && (active2 === canvas || active2 === textInput))
        textInput.focus({ preventScroll: true });
}

function setupTextInput() {
    addListener(textInput, 'keydown', event => dispatchKey(event, true));
    addListener(textInput, 'keyup', event => dispatchKey(event, false));
    addListener(textInput, 'beforeinput', event => {
        if (event.isComposing || nativeCompositionActive || (event.inputType || '').includes('Composition')) return;
        if (event.inputType === 'insertText' && event.data) {
            event.preventDefault();
            writerExports.UiTextInput(event.data, event.timeStamp);
        } else if ((event.inputType || '').startsWith('delete')) {
            event.preventDefault();
        }
    });
    addListener(textInput, 'input', event => {
        if (suppressNextInput) { suppressNextInput = false; return; }
        if (!nativeCompositionActive && event.data)
            writerExports.UiTextInput(event.data, event.timeStamp);
    });
    addListener(textInput, 'compositionstart', event => {
        nativeCompositionActive = true;
        writerExports.UiTextComposition('', 0, textInput.selectionStart ?? 0, 0, event.timeStamp);
    });
    addListener(textInput, 'compositionupdate', event => {
        const start = textInput.selectionStart ?? 0;
        writerExports.UiTextComposition(event.data ?? '', 1, start, 0, event.timeStamp);
    });
    addListener(textInput, 'compositionend', event => {
        nativeCompositionActive = false;
        suppressNextInput = true;
        writerExports.UiTextComposition(event.data ?? '', 2, textInput.selectionStart ?? 0, 0, event.timeStamp);
        textInput.value = '';
    });
    addListener(textInput, 'blur', event => {
        if (!nativeCompositionActive) return;
        nativeCompositionActive = false;
        suppressNextInput = false;
        writerExports.UiTextComposition('', 3, 0, 0, event.timeStamp);
    });
    for (const operation of ['copy', 'cut', 'paste']) {
        addListener(textInput, operation, event => {
            if (!event.isTrusted) return;
            const incoming = operation === 'paste' ? event.clipboardData?.getData('text/plain') ?? '' : '';
            const outgoing = writerExports.UiClipboardEvent(operation, incoming);
            if (operation === 'paste') {
                event.preventDefault();
            } else if (outgoing) {
                event.clipboardData?.setData('text/plain', outgoing);
                event.preventDefault();
            }
            textInput.value = '';
        });
    }
}

// ---- Browser-native open / save ------------------------------------------------------------------

function bytesToBase64(bytes) {
    let binary = '';
    const chunk = 0x8000;
    for (let i = 0; i < bytes.length; i += chunk)
        binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
    return btoa(binary);
}

function base64ToBytes(base64) {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes;
}

function requestOpenFile(accept) {
    if (!filePicker) return;
    filePicker.value = '';
    filePicker.accept = accept || '';
    filePicker.click();
}

async function onFilePicked(file) {
    try {
        const buffer = await file.arrayBuffer();
        const base64 = bytesToBase64(new Uint8Array(buffer));
        writerExports.LoadDocument(file.name || 'Untitled.rtf', base64);
        (focusedIsText ? textInput : canvas).focus({ preventScroll: true });
    } catch (error) {
        showError(error instanceof Error ? error.stack ?? error.message : String(error));
    }
}

function downloadFile(fileName, base64Data) {
    const blob = new Blob([base64ToBytes(base64Data)], { type: 'application/octet-stream' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName || 'document.rtf';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 0);
}

function attachInput() {
    if (started) return;
    canvas = document.getElementById('writer-canvas');
    textInput = document.getElementById('writer-text-input');
    statusLine = document.getElementById('writer-status');
    host = document.getElementById('writer-host');

    filePicker = document.createElement('input');
    filePicker.type = 'file';
    filePicker.style.display = 'none';
    document.body.appendChild(filePicker);
    addListener(filePicker, 'change', () => {
        const file = filePicker.files && filePicker.files[0];
        if (file) onFilePicked(file);
    });

    started = true;

    addListener(canvas, 'pointermove', event => {
        const point = localPoint(event);
        writerExports.UiPointerMove(point.x, point.y, event.buttons, event.timeStamp);
    });
    addListener(canvas, 'pointerdown', event => {
        event.preventDefault();
        (focusedIsText ? textInput : canvas).focus({ preventScroll: true });
        activePointers.add(event.pointerId);
        canvas.setPointerCapture(event.pointerId);
        const point = localPoint(event);
        writerExports.UiPointerButton(point.x, point.y, event.buttons, event.button, true, event.timeStamp);
        // Focus the hidden editor after the managed hit-test may have focused a text control.
        if (focusedIsText || caretActive) textInput.focus({ preventScroll: true });
    });
    addListener(canvas, 'pointerup', event => {
        const point = localPoint(event);
        writerExports.UiPointerButton(point.x, point.y, event.buttons, event.button, false, event.timeStamp);
        activePointers.delete(event.pointerId);
        if (canvas.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
    });
    const cancelPointer = event => {
        if (event.pointerId !== undefined && !activePointers.has(event.pointerId)) return;
        if (event.pointerId !== undefined) activePointers.delete(event.pointerId);
        writerExports.UiCancelPointer(event.timeStamp ?? performance.now());
    };
    addListener(canvas, 'pointercancel', cancelPointer);
    addListener(canvas, 'lostpointercapture', cancelPointer);
    addListener(canvas, 'wheel', event => {
        event.preventDefault();
        const point = localPoint(event);
        const unit = event.deltaMode === WheelEvent.DOM_DELTA_PIXEL ? 100 : event.deltaMode === WheelEvent.DOM_DELTA_LINE ? 3 : 1;
        const horizontal = Math.abs(event.deltaX) > Math.abs(event.deltaY);
        const delta = horizontal ? -event.deltaX / unit : -event.deltaY / unit;
        writerExports.UiPointerWheel(point.x, point.y, event.buttons || 0, horizontal, delta, event.timeStamp);
    }, { passive: false });
    addListener(canvas, 'keydown', event => dispatchKey(event, true));
    addListener(canvas, 'keyup', event => dispatchKey(event, false));
    setupTextInput();

    addListener(window, 'blur', () => writerExports.UiCancelPointer(performance.now()));
    addListener(document, 'visibilitychange', () => {
        if (document.hidden) writerExports.UiCancelPointer(performance.now());
    });

    const resizeObserver = new ResizeObserver(entries => {
        const entry = entries[entries.length - 1];
        observedWidth = Math.max(1, Math.round(entry.contentRect.width));
        observedHeight = Math.max(1, Math.round(entry.contentRect.height));
        writerExports.ResizeUi(observedWidth, observedHeight, window.devicePixelRatio || 1);
    });
    resizeObserver.observe(host);
    listenerCleanups.push(() => resizeObserver.disconnect());
}

function stop() {
    if (!started) return;
    if (frameRequest !== 0) { cancelAnimationFrame(frameRequest); frameRequest = 0; }
    for (const cleanup of listenerCleanups.splice(0)) cleanup();
    activePointers.clear();
    started = false;
    try { writerExports?.Dispose(); } catch { /* teardown best-effort */ }
}

const { setModuleImports, getAssemblyExports, runMain } = await dotnet.create();

setModuleImports('main.js', {
    writer: {
        scheduleFrame: () => scheduleFrame(),
        setCursor: cursor => { if (canvas) canvas.style.cursor = CURSORS[cursor] || 'default'; },
        ready: (logicalWidth, logicalHeight) => {
            attachInput();
            observedWidth = Math.max(1, Math.round(host.clientWidth || logicalWidth));
            observedHeight = Math.max(1, Math.round(host.clientHeight || logicalHeight));
            writerExports.ResizeUi(observedWidth, observedHeight, window.devicePixelRatio || 1);
            scheduleFrame();
            statusLine.hidden = true;
        },
        failed: (exceptionType, details) => showError(`${exceptionType}: ${details}`),
        publishFrame,
        prefersDarkScheme: () => prefersDark(),
        prefersReducedMotion: () => prefersReducedMotion(),
        requestOpenFile: accept => requestOpenFile(accept),
        downloadFile: (fileName, base64Data) => downloadFile(fileName, base64Data),
        promptFileName: defaultName => {
            const chosen = window.prompt('Save document as (extension selects RTF, DOCX, HTML, or MD):', defaultName || 'Untitled.rtf');
            return chosen == null ? '' : chosen.trim();
        },
    },
});

const assemblyExports = await getAssemblyExports('Broiler.Writer.WebAssembly');
writerExports = assemblyExports.Broiler.Writer.WebAssembly.BrowserWriterExports;

window.addEventListener('pagehide', stop, { once: true });

runMain().catch(error => {
    showError(error instanceof Error ? error.stack ?? error.message : String(error));
});
