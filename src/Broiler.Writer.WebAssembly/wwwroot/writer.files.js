// Application transport seam. Only a resolved writable.close() is SavedToHandle.
export const maximumFileBytes = 8 * 1024 * 1024;
export function bytesToBase64(bytes) {
    let binary = '';
    for (let offset = 0; offset < bytes.length; offset += 0x8000)
        binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
    return btoa(binary);
}
export function base64ToBytes(base64) {
    if (base64.length > Math.ceil(maximumFileBytes / 3) * 4) throw new Error('The export exceeds the 8 MiB browser limit.');
    return Uint8Array.from(atob(base64), character => character.charCodeAt(0));
}
export function mimeType(name) {
    return ({ rtf: 'application/rtf', docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
        odt: 'application/vnd.oasis.opendocument.text', html: 'text/html', htm: 'text/html', md: 'text/markdown', markdown: 'text/markdown' })[name.split('.').at(-1).toLowerCase()] || 'application/octet-stream';
}
export async function download(name, base64) {
    // Leave the synchronous managed interop stack before asking the browser to navigate/download.
    await new Promise(resolve => setTimeout(resolve, 0));
    let url;
    try {
        const blob = new Blob([base64ToBytes(base64)], { type: 'application/octet-stream' });
        url = URL.createObjectURL(blob);
        const anchor = document.createElement('a'); anchor.href = url; anchor.download = name;
        document.body.append(anchor);
        try { anchor.click(); } finally { anchor.remove(); }
        setTimeout(() => URL.revokeObjectURL(url), 60000);
        return { outcome: 'ExportInitiated' };
    } catch (error) {
        if (url) URL.revokeObjectURL(url);
        return { outcome: 'Failed', message: String(error) };
    }
}
export async function writeHandle(handle, bytes) {
    let writable;
    try {
        writable = await handle.createWritable();
        await writable.write(bytes);
        await writable.close();
        return { outcome: 'SavedToHandle' };
    } catch (error) {
        try { await writable?.abort(); } catch { /* Preserve the original error. */ }
        return { outcome: error?.name === 'AbortError' ? 'Canceled' : 'Failed', message: String(error) };
    }
}
