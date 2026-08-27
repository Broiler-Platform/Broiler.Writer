// Broiler.Graphics.WebAssembly — direct Canvas 2D replay module.
//
// This is the reusable browser half of the Phase 5 rendering path. Managed code
// (CanvasFramePlanner) resolves a BRenderList into a flat op stream in backing
// (device) pixels with an identity transform and absolute clip rectangles, then
// hands the whole frame across the JS boundary in one presentFrame() call. This
// module never mirrors Broiler's clip/transform stacks; it only replays.
//
// The op codes below mirror CanvasReplayOp.cs exactly. Keep the two in sync.

const OP_SET_CLIP = 1;
const OP_CLEAR_CLIP = 2;
const OP_FILL_RECT = 3;
const OP_STROKE_RECT = 4;
const OP_FILL_ROUNDED_RECT = 5;
const OP_STROKE_ROUNDED_RECT = 6;
const OP_DRAW_IMAGE = 7;
const OP_DRAW_TEXT = 8;

const state = {
    canvas: null,
    ctx: null,
    resources: new Map(),
    fallbackImageData: null,
};

// Diagnostics surface consumed by the Phase 5 smoke test.
const diag = (globalThis.__broilerGraphicsWasm = {
    initialized: false,
    frames: 0,
    fallbackFrames: 0,
    lastOpCount: 0,
    lastStreamLength: 0,
    resourceCount: 0,
    disposed: false,
    lastError: null,
});

function color(argb) {
    const v = argb >>> 0;
    const a = ((v >>> 24) & 0xff) / 255;
    const r = (v >>> 16) & 0xff;
    const g = (v >>> 8) & 0xff;
    const b = v & 0xff;
    return `rgba(${r},${g},${b},${a})`;
}

// CSS generic font families. These are keywords, not real face names, so they must
// reach `ctx.font` unquoted (quoting them turns "monospace" into a lookup for a face
// literally named "monospace", which the browser then can't find).
const CSS_GENERIC_FAMILIES = new Set([
    'serif', 'sans-serif', 'monospace', 'cursive', 'fantasy', 'system-ui',
    'ui-serif', 'ui-sans-serif', 'ui-monospace', 'ui-rounded', 'math', 'emoji', 'fangsong',
]);

// Resolve a requested Broiler font-family name to a Canvas `ctx.font` family list.
// A generic keyword passes through as-is; a specific face is quoted and given a
// sans-serif backstop so an unavailable face still renders. Empty falls back to
// sans-serif, matching the pre-family-plumbing behavior.
function cssFontFamily(name) {
    const family = (name || '').trim();
    if (family === '') return 'sans-serif';
    if (CSS_GENERIC_FAMILIES.has(family.toLowerCase())) return family;
    return `"${family.replace(/["\\]/g, '\\$&')}", sans-serif`;
}

// The single source of truth for the Canvas `font` shorthand. Both text drawing and text
// measurement go through this so measured advances match painted glyphs exactly (otherwise
// caret/selection/grid layout drifts from what is drawn).
function fontString(fontPx, weight, italic, familyName) {
    return `${italic ? 'italic ' : ''}${weight} ${fontPx}px ${cssFontFamily(familyName)}`;
}

// A detached 2D context used only for text measurement, so a measure never disturbs the
// presenter context's state mid-frame.
let measureCtx = null;
function ensureMeasureContext() {
    if (measureCtx === null) {
        const canvas = (typeof OffscreenCanvas !== 'undefined')
            ? new OffscreenCanvas(1, 1)
            : document.createElement('canvas');
        measureCtx = canvas.getContext('2d');
    }
    return measureCtx;
}

function sizeCanvas(backingWidth, backingHeight, cssWidth, cssHeight) {
    const canvas = state.canvas;
    if (canvas.width !== backingWidth) canvas.width = backingWidth;
    if (canvas.height !== backingHeight) canvas.height = backingHeight;
    canvas.style.width = `${cssWidth}px`;
    canvas.style.height = `${cssHeight}px`;
}

export function initialize(canvasSelector) {
    const canvas = document.querySelector(canvasSelector);
    if (!canvas) throw new Error(`Broiler.Graphics.WebAssembly: canvas '${canvasSelector}' was not found.`);
    // { alpha: false } — the presenter surface is opaque; this lets the browser skip
    // per-frame alpha compositing of the canvas backing store.
    state.ctx = canvas.getContext('2d', { alpha: false });
    if (!state.ctx) throw new Error('Broiler.Graphics.WebAssembly: 2D context is unavailable.');
    state.canvas = canvas;
    state.resources.clear();
    state.fallbackImageData = null;
    diag.initialized = true;
    diag.disposed = false;
}

export function presentFrame(stream, streamLength, strings, backingWidth, backingHeight, clearColorArgb, cssWidth, cssHeight, opCount) {
    const ctx = state.ctx;
    if (!ctx) throw new Error('Broiler.Graphics.WebAssembly: present before initialize.');

    sizeCanvas(backingWidth, backingHeight, cssWidth, cssHeight);
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.globalAlpha = 1;

    // Clear the whole backing to the (opaque) clear color with no clip active.
    ctx.fillStyle = color(clearColorArgb);
    ctx.fillRect(0, 0, backingWidth, backingHeight);

    // One base save; SetClip/ClearClip reconstruct from this baseline so a logical
    // clip pop never disturbs anything else. All geometry is absolute device px.
    ctx.save();
    let i = 0;
    while (i < streamLength) {
        const op = stream[i++];
        switch (op) {
            case OP_SET_CLIP: {
                const x = stream[i++], y = stream[i++], w = stream[i++], h = stream[i++];
                ctx.restore();
                ctx.save();
                ctx.beginPath();
                ctx.rect(x, y, w, h);
                ctx.clip();
                break;
            }
            case OP_CLEAR_CLIP: {
                ctx.restore();
                ctx.save();
                break;
            }
            case OP_FILL_RECT: {
                const x = stream[i++], y = stream[i++], w = stream[i++], h = stream[i++];
                ctx.fillStyle = color(stream[i++]);
                ctx.fillRect(x, y, w, h);
                break;
            }
            case OP_STROKE_RECT: {
                const x = stream[i++], y = stream[i++], w = stream[i++], h = stream[i++];
                const t = stream[i++];
                ctx.fillStyle = color(stream[i++]);
                // Inside stroke as four bands, matching the CPU renderer's
                // DrawRectangleStroke (which fills inset edges) rather than Canvas
                // strokeRect (which centers the stroke on the edge).
                ctx.fillRect(x, y, w, t);
                ctx.fillRect(x, y + h - t, w, t);
                ctx.fillRect(x, y, t, h);
                ctx.fillRect(x + w - t, y, t, h);
                break;
            }
            case OP_FILL_ROUNDED_RECT: {
                const x = stream[i++], y = stream[i++], w = stream[i++], h = stream[i++];
                const rx = stream[i++], ry = stream[i++];
                ctx.fillStyle = color(stream[i++]);
                roundedPath(ctx, x, y, w, h, rx, ry);
                ctx.fill();
                break;
            }
            case OP_STROKE_ROUNDED_RECT: {
                const x = stream[i++], y = stream[i++], w = stream[i++], h = stream[i++];
                const rx = stream[i++], ry = stream[i++];
                const t = stream[i++];
                ctx.strokeStyle = color(stream[i++]);
                ctx.lineWidth = t;
                roundedPath(ctx, x, y, w, h, rx, ry);
                ctx.stroke();
                break;
            }
            case OP_DRAW_IMAGE: {
                const id = stream[i++];
                const sx = stream[i++], sy = stream[i++], sw = stream[i++], sh = stream[i++];
                const dx = stream[i++], dy = stream[i++], dw = stream[i++], dh = stream[i++];
                const opacity = stream[i++];
                const res = state.resources.get(id);
                if (res) {
                    ctx.globalAlpha = opacity;
                    ctx.drawImage(res, sx, sy, sw, sh, dx, dy, dw, dh);
                    ctx.globalAlpha = 1;
                }
                break;
            }
            case OP_DRAW_TEXT: {
                const bx = stream[i++], by = stream[i++];
                const fontPx = stream[i++], weight = stream[i++], italic = stream[i++];
                ctx.fillStyle = color(stream[i++]);
                const text = strings[stream[i++]];
                ctx.font = fontString(fontPx, weight, italic, strings[stream[i++]]);
                ctx.textBaseline = 'alphabetic';
                ctx.textAlign = 'left';
                ctx.fillText(text, bx, by);
                break;
            }
            default:
                ctx.restore();
                diag.lastError = `Unknown replay op ${op} at ${i - 1}`;
                throw new Error(diag.lastError);
        }
    }

    ctx.restore();
    diag.frames++;
    diag.lastOpCount = opCount;
    diag.lastStreamLength = streamLength;
}

export function presentImageData(backingWidth, backingHeight, rgba, cssWidth, cssHeight) {
    const ctx = state.ctx;
    if (!ctx) throw new Error('Broiler.Graphics.WebAssembly: present before initialize.');

    sizeCanvas(backingWidth, backingHeight, cssWidth, cssHeight);
    if (!state.fallbackImageData || state.fallbackImageData.width !== backingWidth || state.fallbackImageData.height !== backingHeight) {
        state.fallbackImageData = ctx.createImageData(backingWidth, backingHeight);
    }
    state.fallbackImageData.data.set(rgba);
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.putImageData(state.fallbackImageData, 0, 0);
    diag.frames++;
    diag.fallbackFrames++;
}

export function uploadImage(id, width, height, rgba) {
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    const rctx = canvas.getContext('2d');
    // Straight-alpha RGBA in, drawn once into a reusable resource canvas so that
    // later drawImage calls are synchronous (no createImageBitmap promise).
    const image = new ImageData(new Uint8ClampedArray(rgba), width, height);
    rctx.putImageData(image, 0, 0);
    state.resources.set(id, canvas);
    diag.resourceCount = state.resources.size;
}

export function releaseImage(id) {
    state.resources.delete(id);
    diag.resourceCount = state.resources.size;
}

// Horizontal advance (CSS px) of `text` in the same font the replay draws with, so managed
// text layout agrees with what is painted. Measured at the logical font size; the presenter
// bakes the device scale separately, and the advance/size ratio is scale-invariant.
export function measureAdvance(text, fontPx, weight, italic, family) {
    if (!text) return 0;
    const ctx = ensureMeasureContext();
    ctx.font = fontString(fontPx, weight, italic, family);
    return ctx.measureText(text).width;
}

export function dispose() {
    state.resources.clear();
    state.fallbackImageData = null;
    state.ctx = null;
    state.canvas = null;
    diag.disposed = true;
    diag.initialized = false;
    diag.resourceCount = 0;
}

function roundedPath(ctx, x, y, w, h, rx, ry) {
    rx = Math.max(0, Math.min(rx, w / 2));
    ry = Math.max(0, Math.min(ry, h / 2));
    ctx.beginPath();
    if (typeof ctx.roundRect === 'function' && rx === ry) {
        ctx.roundRect(x, y, w, h, rx);
        return;
    }
    // Elliptical corners (or older engines without roundRect): build the path.
    ctx.moveTo(x + rx, y);
    ctx.lineTo(x + w - rx, y);
    ctx.ellipse(x + w - rx, y + ry, rx, ry, 0, -Math.PI / 2, 0);
    ctx.lineTo(x + w, y + h - ry);
    ctx.ellipse(x + w - rx, y + h - ry, rx, ry, 0, 0, Math.PI / 2);
    ctx.lineTo(x + rx, y + h);
    ctx.ellipse(x + rx, y + h - ry, rx, ry, 0, Math.PI / 2, Math.PI);
    ctx.lineTo(x, y + ry);
    ctx.ellipse(x + rx, y + ry, rx, ry, 0, Math.PI, (3 * Math.PI) / 2);
    ctx.closePath();
}
