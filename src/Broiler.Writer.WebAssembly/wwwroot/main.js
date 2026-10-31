import { OfficeRecovery } from './broiler.recovery.js';
export const officeInputProtocolVersion = 1;
if (location.origin === 'https://broiler-platform.github.io') document.querySelector('.office-nav a').href = '/demo/';
import { dotnet } from './_framework/dotnet.js';
import { maximumFileBytes, bytesToBase64, base64ToBytes, download, writeHandle } from './writer.files.js';

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
const EDITOR_CTRL_KEYS = new Set(['b', 'i', 'u', 'z', 'y', 'a', 's', 'o', 'n']);

let writerExports;
let recovery;
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
let compositionEcho = null;
let compositionEchoTimer = 0;
let fileBusy = false;
let currentName = 'Untitled.rtf';
let documentGeneration = 0;
let pickerGeneration = 0;
let currentModified = false;
let framePending = false;
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
    if (!started) return;
    if (frameInProgress) { framePending = true; return; }
    if (frameRequest !== 0) return;
    frameRequest = requestAnimationFrame(() => {
        frameRequest = 0;
        frameInProgress = true;
        try {
            writerExports.RenderUiFrame();
        } finally {
            frameInProgress = false;
            if (framePending) { framePending = false; scheduleFrame(); }
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
        (event.ctrlKey || event.metaKey ? 2 : 0) |
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
        (key === 'Space' ? event.target !== textInput :
            ['Tab', 'Up', 'Down', 'Left', 'Right', 'Home', 'End', 'Backspace', 'Delete', 'Enter'].includes(key))) {
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
    if (isText && active2 === canvas)
        textInput.focus({ preventScroll: true });
}

function setupTextInput() {
    addListener(textInput, 'keydown', event => dispatchKey(event, true));
    addListener(textInput, 'keyup', event => dispatchKey(event, false));
    addListener(textInput, 'beforeinput', event => {
        if (event.isComposing || nativeCompositionActive || (event.inputType || '').includes('Composition')) return;
        if (event.inputType === 'insertText' && event.data) {
            event.preventDefault();
            if (compositionEcho !== null && compositionEcho === event.data) compositionEcho = null;
            else writerExports.UiTextInput(event.data, event.timeStamp);
        } else if ((event.inputType || '').startsWith('delete')) {
            event.preventDefault();
        }
    });
    addListener(textInput, 'input', event => {
        if (compositionEcho !== null && compositionEcho === event.data) { compositionEcho = null; textInput.value = ''; return; }
        if (!nativeCompositionActive && !event.isComposing && event.data)
            writerExports.UiTextInput(event.data, event.timeStamp);
        if (!nativeCompositionActive) textInput.value = '';
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
        compositionEcho = event.data ?? '';
        clearTimeout(compositionEchoTimer);
        compositionEchoTimer = setTimeout(() => { compositionEcho = null; }, 0);
        writerExports.UiTextComposition(event.data ?? '', 2, textInput.selectionStart ?? 0, 0, event.timeStamp);
        textInput.value = '';
    });
    addListener(textInput, 'blur', event => {
        if (!nativeCompositionActive) return;
        nativeCompositionActive = false;
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

function requestOpenFile(accept) {
    if (!filePicker || fileBusy) { reportOperation('Failed', 'Finish the current file operation first.'); return; }
    fileBusy = true;
    pickerGeneration = documentGeneration;
    filePicker.value = '';
    filePicker.accept = accept || '';
    try { filePicker.click(); } catch (error) { fileBusy = false; writerExports.FileOperationMessage('Failed', 'Could not open the file picker: ' + error); }
}

async function onFilePicked(file) {
    try {
        if (file.size > maximumFileBytes) throw new Error('The file exceeds the 8 MiB browser limit.');
        const generation = pickerGeneration;
        const buffer = await file.arrayBuffer();
        if (buffer.byteLength > maximumFileBytes) throw new Error('The file exceeds the 8 MiB browser limit.');
        if (generation !== documentGeneration) throw new Error('The document changed while the picker was open. Please open the file again.');
        const base64 = bytesToBase64(new Uint8Array(buffer));
        writerExports.LoadDocument(file.name || 'Untitled.rtf', base64);
        (focusedIsText ? textInput : canvas).focus({ preventScroll: true });
    } catch (error) {
        writerExports.FileOperationMessage('Failed', 'Open failed: ' + (error.message || error));
    } finally {
        fileBusy = false;
    }
}

async function downloadFile(fileName, base64Data, token) {
    const result = fileBusy ? { outcome: 'Failed', message: 'Another file operation is in progress.' } : await download(fileName, base64Data);
    writerExports.ExportCompleted(token, result.outcome, result.message || '');
}

function reportOperation(outcome, message, notes = '') {
    const status = document.getElementById('writer-operation');
    status.dataset.outcome = outcome; status.textContent = message; status.title = message;
    document.getElementById('writer-operation-detail').textContent = message;
    document.getElementById('writer-notes').textContent = notes || 'No format notes for this operation.';
    if (/Warning|Error/.test(notes)) document.getElementById('writer-info').open = true;
}

function updatePreview() {
    if (started && document.getElementById('writer-info').open)
        document.getElementById('writer-preview').textContent = writerExports.DocumentPreview();
}

async function saveToHandle() {
    if (fileBusy) return;
    fileBusy = true;
    const generation = documentGeneration;
    let token = 0;
    try {
        const handle = await window.showSaveFilePicker({ suggestedName: /\.(rtf|docx|odt|html?|md|markdown)$/i.test(currentName) ? currentName : 'Untitled.rtf', types: [
            { description: 'Writer documents', accept: { 'application/rtf': ['.rtf'], 'application/vnd.openxmlformats-officedocument.wordprocessingml.document': ['.docx'],
                'application/vnd.oasis.opendocument.text': ['.odt'], 'text/html': ['.html', '.htm'], 'text/markdown': ['.md', '.markdown'] } },
        ], excludeAcceptAllOption: true });
        if (generation !== documentGeneration) throw new Error('The document changed while choosing a file. Please save again.');
        const prepared = writerExports.PrepareHandleExport(handle.name);
        if (!prepared) return;
        const fields = prepared.split('\0'); token = Number(fields[0]);
        if (fields[1] !== handle.name) throw new Error('Choose a supported document extension.');
        const result = await writeHandle(handle, base64ToBytes(fields[2]));
        writerExports.ExportCompleted(token, result.outcome, result.message || '');
    } catch (error) {
        const outcome = error?.name === 'AbortError' ? 'Canceled' : 'Failed';
        if (token) writerExports.ExportCompleted(token, outcome, String(error));
        else writerExports.FileOperationMessage(outcome, 'Save ' + (outcome === 'Canceled' ? 'canceled.' : 'failed: ' + error));
    } finally { fileBusy = false; }
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
        else { fileBusy = false; writerExports.FileOperationMessage('Canceled', 'Open canceled. The document is unchanged.'); }
    });
    addListener(filePicker, 'cancel', () => { fileBusy = false; writerExports.FileOperationMessage('Canceled', 'Open canceled. The document is unchanged.'); });
    addListener(document.getElementById('writer-info'), 'toggle', updatePreview);
    const saveFile = document.getElementById('writer-save-file');
    saveFile.hidden = typeof window.showSaveFilePicker !== 'function';
    addListener(saveFile, 'click', saveToHandle);
    const exportDialog = document.getElementById('writer-export-name');
    addListener(exportDialog, 'close', () => {
        if (started) writerExports.ExportNameSelected(exportDialog.returnValue === 'export' ? document.getElementById('writer-filename').value : '');
    });

    started = true;

    addListener(canvas, 'pointermove', event => {
        const point = localPoint(event);
        writerExports.UiPointerMove(point.x, point.y, event.buttons, event.timeStamp);
    });
    // The app draws its own context menu. Keep native menus elsewhere on the page.
    for (const target of [canvas, textInput])
        addListener(target, 'contextmenu', event => event.preventDefault());

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

    addListener(window, 'blur', () => {
        writerExports.UiCancelPointer(performance.now());
        if (nativeCompositionActive) { nativeCompositionActive = false; writerExports.UiTextComposition('', 3, 0, 0, performance.now()); }
    });
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
    filePicker?.remove(); clearTimeout(compositionEchoTimer);
    window.removeEventListener('beforeunload', confirmUnsavedChanges);
    started = false;
    try { writerExports?.Dispose(); } catch { /* teardown best-effort */ }
}

const { setModuleImports, getAssemblyExports, runMain } = await dotnet.create();

setModuleImports('main.js', {
    writer: {
        openUrl: value => {
            try {
                const url = new URL(value, location.href);
                if (!['http:', 'https:', 'mailto:'].includes(url.protocol) || value.startsWith('#')) {
                    writerExports.FileOperationMessage('Failed', 'This link cannot be opened in the browser edition.'); return;
                }
                window.open(url.href, '_blank', 'noopener,noreferrer');
            } catch { writerExports.FileOperationMessage('Failed', 'This document link is invalid.'); }
        },
        scheduleFrame: () => scheduleFrame(),
        setCursor: cursor => { if (canvas) canvas.style.cursor = CURSORS[cursor] || 'default'; },
        // The tab is this application's title bar, so the open document's name belongs here.
        setTitle: title => { document.title = title; },
        ready: (logicalWidth, logicalHeight) => {
            attachInput();
            observedWidth = Math.max(1, Math.round(host.clientWidth || logicalWidth));
            observedHeight = Math.max(1, Math.round(host.clientHeight || logicalHeight));
            writerExports.ResizeUi(observedWidth, observedHeight, window.devicePixelRatio || 1);
            scheduleFrame();
            statusLine.hidden = true;
            queueMicrotask(() => recovery.start());
        },
        failed: (exceptionType, details) => showError(`${exceptionType}: ${details}`),
        publishFrame,
        prefersDarkScheme: () => prefersDark(),
        prefersReducedMotion: () => prefersReducedMotion(),
        requestOpenFile: accept => requestOpenFile(accept),
        setDocumentState: (name, modified, generation, revision) => {
            recovery?.changed({name, modified, generation, revision});
            currentName = name; currentModified = modified; documentGeneration = generation;
            document.body.dataset.modified = String(modified);
            document.getElementById('writer-document-name').textContent = name + (modified ? ' · Modified' : ' · No unsaved edits');
            queueMicrotask(updatePreview);
        },
        reportOperation,
        setHasUnsavedChanges: modified => {
            if (modified === currentModified) return;
            currentModified = modified;
            window.removeEventListener('beforeunload', confirmUnsavedChanges);
            if (modified) window.addEventListener('beforeunload', confirmUnsavedChanges);
        },
        downloadFile,
        requestExportName: defaultName => {
            const dialog = document.getElementById('writer-export-name');
            document.getElementById('writer-filename').value = defaultName || 'Untitled.rtf';
            dialog.returnValue = '';
            try { dialog.showModal(); document.getElementById('writer-filename').select(); }
            catch { writerExports.ExportNameSelected(''); }
        },
    },
});

const assemblyExports = await getAssemblyExports('Broiler.Writer.WebAssembly');
writerExports = assemblyExports.Broiler.Writer.WebAssembly.BrowserWriterExports;
recovery = new OfficeRecovery({
    app: 'writer',
    capture: () => JSON.parse(writerExports.RecoverySnapshot()),
    restore: record => { if (!writerExports.RestoreRecovery(record.name, record.payload)) throw new Error('The draft could not be restored; current content retained.'); },
    acceptFormat: format => format === 'rtf',
});


window.addEventListener('pagehide', stop, { once: true });
window.addEventListener('pageshow', event => { if (event.persisted) location.reload(); });

// Browsers own the navigation/close prompt; custom asynchronous dialogs cannot delay unloading.
function confirmUnsavedChanges(event) {
    event.preventDefault();
    event.returnValue = '';
}

runMain().catch(error => {
    showError(error instanceof Error ? error.stack ?? error.message : String(error));
});

// Read-only host diagnostics; mutation remains in the editor and recovery commands.
export function recoverySnapshot() { return JSON.parse(writerExports.RecoverySnapshot()); }
export function documentPreview() { return writerExports.DocumentPreview(); }
