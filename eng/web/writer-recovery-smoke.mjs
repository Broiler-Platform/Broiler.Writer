import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { extname, resolve, sep } from 'node:path';
import { chromium } from 'playwright';

const directory = process.argv[2];
assert.ok(directory, 'Pass the extracted Writer wwwroot.');
const root = resolve(directory);
const prefix = '/writer-test/';
const types = { '.html': 'text/html', '.css': 'text/css', '.js': 'text/javascript',
    '.json': 'application/json', '.wasm': 'application/wasm' };
const server = createServer(async (request, response) => {
    const pathname = new URL(request.url, 'http://localhost').pathname;
    if (!pathname.startsWith(prefix)) { response.writeHead(404).end(); return; }
    const path = resolve(root, decodeURIComponent(pathname.slice(prefix.length)) || 'index.html');
    if (!path.startsWith(root + sep)) { response.writeHead(404).end(); return; }
    try {
        const bytes = await readFile(path);
        response.writeHead(200, { 'Content-Type': types[extname(path)] || 'application/octet-stream' }).end(bytes);
    } catch { response.writeHead(404).end(); }
});
await new Promise(resolveListen => server.listen(0, '127.0.0.1', resolveListen));

let browser;
try {
    browser = await chromium.launch();
    const page = await browser.newPage();
    await page.goto(`http://127.0.0.1:${server.address().port}${prefix}`);
    await openRecovery();
    await page.getByRole('checkbox', { name: 'Automatic checkpoints' }).uncheck();
    const docx = await readFile(new URL('./fixtures/m0-recovery.docx', import.meta.url));
    await importDraft('Legacy draft', 'rtf', Buffer.from(String.raw`{\rtf1\ansi Legacy recovery text}`).toString('base64'), 1);
    await importDraft('DOCX draft', 'docx-v1', docx.toString('base64'));
    await importDraft('Corrupt draft', 'docx-v1', Buffer.from('not a document').toString('base64'));
    await page.reload();
    await openRecovery();

    await restore('Legacy draft');
    await waitForOperation('Recovered', 'Legacy draft');
    assert.equal(await preview(), 'Legacy recovery text');
    await page.waitForFunction(() => document.querySelector('[data-backup-key]')?.textContent.includes('Legacy draft'));
    await waitForRecovery('restored');
    await openRecovery();

    await restore('DOCX draft');
    await waitForOperation('Recovered', 'DOCX draft');
    assert.equal(await preview(), 'DOCX recovery text');
    await page.locator('#writer-info').evaluate(details => { details.open = true; });
    await page.waitForFunction(() => !document.querySelector('#writer-view-notes').textContent.includes('writer.view.pagination'));
    await waitForRecovery('restored');
    await openRecovery();

    await restore('Corrupt draft');
    await waitForOperation('Failed');
    assert.equal(await preview(), 'DOCX recovery text');
    await waitForRecovery('unavailable');

    const before = await storedDrafts();
    assert.ok(before.some(draft => draft.name === 'DOCX draft' && draft.format === 'docx-v1'));
    await page.evaluate(() => {
        const originalPut = IDBObjectStore.prototype.put;
        IDBObjectStore.prototype.put = function (...args) {
            const request = originalPut.apply(this, args);
            if (this.name === 'drafts')
                this.transaction.abort();
            return request;
        };
    });
    await page.getByRole('button', { name: 'Save checkpoint now', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('#recovery-status').textContent.includes('transaction was aborted'));
    assert.deepEqual(await storedDrafts(), before);
    assert.equal(await preview(), 'DOCX recovery text');
    console.log('Writer recovery passed: legacy RTF, DOCX, corrupt payload, reload and aborted IndexedDB write.');

    async function openRecovery() {
        await page.waitForFunction(() => document.querySelector('#writer-status')?.hidden);
        await page.locator('.office-recovery').evaluate(details => {
            const dialog = details.closest('dialog');
            details.parentElement.hidden = false;
            document.querySelector('#writer-info').parentElement.hidden = true;
            details.open = true;
            if (!dialog.open) dialog.showModal();
        });
        await page.waitForFunction(() => document.querySelector('#recovery-status')?.dataset.state);
    }

    async function importDraft(name, format, payload, schema = 2) {
        const bundle = { schema, app: 'writer', id: name, name, format, payload, revision: 1, generation: 1, updatedAt: 1 };
        await page.getByLabel('Import recovery bundle').setInputFiles({
            name: 'draft.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(bundle)),
        });
        await draft(name).waitFor();
    }

    function draft(name) {
        return page.locator('.recovery-draft').filter({ hasText: `${name} ·` });
    }

    async function restore(name) {
        await draft(name).first().getByRole('button', { name: 'Restore', exact: true }).click();
        await page.getByRole('dialog', { name: 'Recovery confirmation' }).getByRole('button', { name: 'Continue' }).click();
    }

    function waitForOperation(outcome, name = null) {
        return page.waitForFunction(({ outcome, name }) =>
            document.querySelector('#writer-operation').dataset.outcome === outcome &&
            (name === null || document.querySelector('#writer-document-name').textContent.startsWith(name + ' ·')), { outcome, name });
    }

    function waitForRecovery(state) {
        return page.waitForFunction(value => document.querySelector('#recovery-status').dataset.state === value, state);
    }

    function preview() {
        return page.evaluate(async () => {
            const entry = document.querySelector('script[type=module][src]').src;
            return (await import(entry)).documentPreview();
        });
    }

    function storedDrafts() {
        return page.evaluate(async () => {
            const db = await new Promise((resolveOpen, reject) => {
                const request = indexedDB.open('broiler.writer.recovery', 1);
                request.onsuccess = () => resolveOpen(request.result);
                request.onerror = () => reject(request.error);
            });
            try {
                return await new Promise((resolveRead, reject) => {
                    const request = db.transaction('drafts').objectStore('drafts').getAll();
                    request.onsuccess = () => resolveRead(request.result);
                    request.onerror = () => reject(request.error);
                });
            } finally { db.close(); }
        });
    }
} finally {
    await browser?.close();
    await new Promise(close => server.close(close));
}
