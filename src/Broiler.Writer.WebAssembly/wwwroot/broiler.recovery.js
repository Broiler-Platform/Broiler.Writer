// Vendored identically in Writer and Cells until the shared browser-host package is released.
export const recordSchema = 2;
export const maximumPayloadCharacters = 16 * 1024 * 1024;
const maximumDrafts = 30;
const id = () => crypto.randomUUID();

export function validateRecord(record, app) {
    if (!record || record.app !== app || ![1, recordSchema].includes(record.schema) ||
        typeof record.id !== 'string' || record.id.length > 128 || typeof record.name !== 'string' || record.name.length > 255 ||
        !['rtf', 'cells-json'].includes(record.format) || typeof record.payload !== 'string' ||
        record.payload.length > maximumPayloadCharacters || !Number.isSafeInteger(record.revision) || record.revision < 0 ||
        !Number.isSafeInteger(record.generation) || record.generation < 0 || !Number.isFinite(record.updatedAt) ||
        (record.notes !== undefined && (typeof record.notes !== 'string' || record.notes.length > 65536)))
        throw new Error('Unsupported or invalid recovery record. The stored draft has been retained.');
    return record;
}

export class RecoveryStore {
    constructor(app) { this.app = app; this.databaseName = `broiler.${app}.recovery`; }
    async open() {
        if (this.database) return this.database;
        return new Promise((resolve, reject) => {
            const request = indexedDB.open(this.databaseName, 1);
            request.onupgradeneeded = () => {
                const db = request.result;
                db.createObjectStore('drafts', { keyPath: 'id' });
                db.createObjectStore('backups', { keyPath: 'key' });
            };
            request.onerror = () => reject(request.error || new Error('IndexedDB could not be opened.'));
            request.onblocked = () => reject(new Error('Another app version is holding local storage open. Close it and retry.'));
            request.onsuccess = () => {
                this.database = request.result;
                this.database.onversionchange = () => { this.close(); };
                resolve(this.database);
            };
        });
    }
    close() { this.database?.close(); this.database = null; }
    async transaction(stores, mode, action) {
        const db = await this.open();
        return new Promise((resolve, reject) => {
            const transaction = db.transaction(stores, mode);
            let result, actionError;
            transaction.oncomplete = () => resolve(typeof result === 'function' ? result() : result);
            transaction.onabort = () => {
                this.close();
                reject(actionError || transaction.error || new Error('Local storage transaction was aborted.'));
            };
            // Request errors keep their default abort behavior. Report only after
            // abort completes, when the previous committed data is authoritative.
            transaction.onerror = () => {};
            try { result = action(transaction); } catch (error) {
                actionError = error;
                try { transaction.abort(); } catch { this.close(); reject(error); }
            }
        });
    }
    async list() {
        return this.transaction(['drafts'], 'readonly', transaction => {
            const request = transaction.objectStore('drafts').getAll();
            return () => request.result.filter(record => record.app === this.app).sort((a,b) => b.updatedAt - a.updatedAt);
        });
    }
    async backups() {
        return this.transaction(['backups'], 'readonly', transaction => {
            const request = transaction.objectStore('backups').getAll();
            return () => request.result.filter(backup => backup.original?.app === this.app);
        });
    }
    async put(record) {
        validateRecord(record, this.app);
        let capacityExceeded = false;
        await this.transaction(['drafts'], 'readwrite', transaction => {
            const store = transaction.objectStore('drafts');
            const count = store.count(), existing = store.get(record.id);
            existing.onsuccess = () => {
                // Both requests precede this callback in the same transaction.
                if (!existing.result && count.result >= maximumDrafts) { capacityExceeded = true; return; }
                store.put(record);
            };
        });
        if (capacityExceeded) throw new Error(`The ${maximumDrafts}-draft limit was reached. Export/delete old drafts before adding another.`);
    }
    async migrate(record) {
        validateRecord(record, this.app);
        if (record.schema === recordSchema) return record;
        let migrated;
        await this.transaction(['drafts','backups'], 'readwrite', transaction => {
            const store = transaction.objectStore('drafts'), latest = store.get(record.id);
            latest.onsuccess = () => {
                let current;
                try { current = validateRecord(latest.result, this.app); } catch { transaction.abort(); return; }
                if (current.schema === recordSchema) { migrated = current; return; }
                migrated = { ...current, schema: recordSchema, tabId: current.tabId || 'legacy', documentId: current.documentId || current.id };
                transaction.objectStore('backups').put({key: `${current.id}:schema1`, original: current});
                store.put(migrated);
            };
        });
        return migrated;
    }
    async remove(recordId) {
        await this.transaction(['drafts','backups'], 'readwrite', transaction => {
            transaction.objectStore('drafts').delete(recordId);
            transaction.objectStore('backups').delete(`${recordId}:schema1`);
        });
    }
    async clear() {
        await this.transaction(['drafts','backups'], 'readwrite', transaction => {
            transaction.objectStore('drafts').clear(); transaction.objectStore('backups').clear();
        });
    }
}


export class OfficeRecovery {
    constructor({app, capture, restore, acceptFormat}) {
        this.app = app; this.capture = capture; this.restore = restore; this.acceptFormat = acceptFormat;
        this.store = new RecoveryStore(app); this.tabId = id(); this.documentId = id();
        this.queue = Promise.resolve(); this.enabled = true; this.lastKey = null;
        this.preferenceKey = `broiler.${app}.recovery.preferences.v1`;
        this.state = null; this.generation = null; this.ready = false;
        this.mount();
        try {
            const saved = localStorage.getItem(this.preferenceKey);
            if (saved) {
                const preference = JSON.parse(saved);
                if (preference.schema !== 1 || typeof preference.enabled !== 'boolean') throw new Error('Unsupported recovery preferences.');
                this.enabled = preference.enabled;
            }
        } catch { this.preferenceWarning = 'Preferences unavailable; checkpoint setting applies to this page only.'; }
        this.toggle.checked = this.enabled;
        this.visibility = () => { if (document.hidden) this.flush(); };
        this.pageHide = () => { this.flush(); clearTimeout(this.timer); };
        document.addEventListener('visibilitychange', this.visibility);
        window.addEventListener('pagehide', this.pageHide);
    }
    message(state, text) {
        this.status.dataset.state = state; this.status.textContent = text;
        this.summary.textContent = `Recovery: ${state}`;
    }
    async start() {
        await this.refresh();
        this.ready = true;
        if (this.status.dataset.state !== 'unavailable') {
            if (this.preferenceWarning) this.message('warning', this.preferenceWarning);
            else if (!this.enabled) this.message('paused', 'Automatic checkpoints are paused. Export current recovery remains available.');
        }
        if (this.state?.modified) this.schedule();
    }
    changed(state) {
        if (this.generation !== state.generation) {
            this.generation = state.generation; this.documentId = id(); this.lastKey = null;
            this.createdAt = null;
            clearTimeout(this.timer); this.firstChange = null;
        }
        this.state = state;
        const key = JSON.stringify([state.generation,state.revision,state.name]);
        if (this.ready && !this.restoring && state.modified && key !== this.lastKey && key !== this.observedKey) {
            this.observedKey = key; this.schedule();
        }
    }
    schedule() {
        if (!this.enabled) return;
        this.message('pending', 'Changes are waiting for a local checkpoint. Export a separate backup.');
        const now = performance.now(); this.firstChange ??= now;
        clearTimeout(this.timer);
        this.timer = setTimeout(() => this.flush(), Math.max(0, Math.min(500, 2000 - (now - this.firstChange))));
    }
    snapshot() {
        const snapshot = this.capture();
        if (!snapshot) throw new Error('The current document cannot be checkpointed. Use the document export command.');
        if (this.state?.generation === snapshot.generation) this.state = {...this.state, revision:snapshot.revision, name:snapshot.name};
        const now = Date.now();
        return validateRecord({ ...snapshot, schema:recordSchema, app:this.app, id:this.documentId,
            documentId:this.documentId, tabId:this.tabId, producerPath:location.pathname,
            updatedAt:now, createdAt:this.createdAt || (this.createdAt = now) }, this.app);
    }
    flush(force = false) {
        clearTimeout(this.timer); this.firstChange = null;
        if (!this.ready || this.restoring || this.clearing || (!this.enabled && !force) || (!this.state?.modified && !force)) return this.queue;
        let record, key;
        try {
            record = this.snapshot(); key = JSON.stringify([record.generation,record.revision,record.name]);
            if (!force && key === this.lastKey) return this.queue;
        } catch (error) { this.failure(error); return this.queue; }
        this.message('saving', 'Writing a local checkpoint…');
        this.queue = this.queue.then(async () => {
            await this.store.put(record);
            this.lastKey = key;
            if (record.id === this.documentId && record.generation === this.state?.generation && record.revision === this.state?.revision && record.name === this.state?.name)
                this.message(record.notes ? 'warning' : 'saved', record.notes ? `Local checkpoint committed with format notes: ${record.notes}` : 'Local checkpoint committed. Browser storage can still be evicted; export a separate backup.');
            else this.message('pending','A previous revision was committed; newer document content is still waiting for a checkpoint.');
            await this.renderList();
        }).catch(error => this.failure(error));
        return this.queue;
    }
    failure(error) {
        this.message('unavailable', `Local recovery failed: ${error?.message || error}. The document stays open. Export current recovery or delete old drafts (maximum ${maximumDrafts}).`);
    }
    async refresh() {
        try { await this.renderList(); this.message('ready', 'Local recovery is available. Choose a draft to restore; the current document is never replaced automatically.'); }
        catch (error) { this.failure(error); }
    }
    async renderList() {
        const records = await this.store.list(); this.list.replaceChildren();
        if (!records.length) this.list.textContent = 'No local drafts. Missing/evicted browser data cannot be reconstructed; import an exported recovery bundle if available.';
        for (const record of records) {
            const row = document.createElement('div'); row.className = 'recovery-draft'; row.dataset.draftId = record.id;
            const title = document.createElement('p'); title.textContent = `${record.name} · ${new Date(record.updatedAt).toLocaleString()} · revision ${record.revision} · ${record.tabId === this.tabId ? 'this page' : 'another/previous page'}`;
            row.append(title);
            this.button(row, 'Restore', async () => {
                if (!await this.confirm('Restore this draft? Current content stays recoverable only if its checkpoint succeeds or you export it first.' + (record.notes ? ` Recovery format notes: ${record.notes}` : ''))) return;
                try {
                    await this.flush(true);
                    if (this.state?.modified && this.status.dataset.state === 'unavailable') throw new Error('Checkpoint the current document or export it and create a new document before restoring.');
                    const migrated = await this.store.migrate(record);
                    if (!this.acceptFormat(migrated.format)) throw new Error('This app does not support the draft format. Export its recovery bundle instead.');
                    this.restoring = true;
                    try { await this.restore(migrated); } finally { this.restoring = false; }
                    this.documentId = id(); this.lastKey = null; this.observedKey = null;
                    await this.flush(true);
                    if (this.status.dataset.state !== 'unavailable') this.message('restored', 'Draft restored into a separate document identity. Original checkpoint retained. Export the document to keep a separate backup.' + (record.notes ? ` Format notes: ${record.notes}` : ''));
                } catch (error) { this.failure(error); }
            });
            this.exportLink(row, 'Export bundle', () => record);
            this.button(row, 'Delete', async () => {
                if (!await this.confirm('Delete this local draft and its migration backup? This does not delete an exported file or the open document.')) return;
                try { await this.queue; await this.store.remove(record.id); await this.renderList(); this.message('deleted', 'Selected local draft deleted. Other drafts and the open document remain.'); }
                catch (error) { this.failure(error); }
            });
            this.list.append(row);
        }
        for (const backup of await this.store.backups()) {
            const row = document.createElement('div'); row.dataset.backupKey = backup.key;
            const label = document.createElement('p'); label.textContent = `Migration backup: ${backup.original.name}. Export and import its bundle to restore the older snapshot separately.`; row.append(label);
            this.exportLink(row, 'Export migration backup', () => backup.original);
            this.list.append(row);
        }
    }
    exportLink(parent, text, getRecord) {
        const link = document.createElement('a'); link.href = '#'; link.textContent = text; link.className = 'recovery-export';
        link.download = 'Broiler.broiler-recovery.json';
        let prepared = false;
        const prepare = () => {
            const record = getRecord();
            const url = URL.createObjectURL(new Blob([JSON.stringify(record)], {type:'application/json'}));
            link.href = url;
            link.download = `${String(record.name || 'document').replace(/[^\p{L}\p{N}._-]/gu,'_')}.broiler-recovery.json`;
            setTimeout(() => URL.revokeObjectURL(url), 60000);
            prepared = true;
        };
        const prepareSafely = event => { try { prepare(); } catch (error) { prepared = false; event.preventDefault(); this.failure(error); } };
        // Prepare the filename and URL before native mouse/keyboard activation.
        link.addEventListener('pointerdown', prepareSafely);
        link.addEventListener('focus', event => { if (!prepared) prepareSafely(event); });
        link.addEventListener('blur', () => { prepared = false; });
        link.addEventListener('keydown', event => { if (event.key === 'Enter') prepareSafely(event); });
        link.addEventListener('click', event => {
            try {
                if (!prepared) prepare();
                this.message('export','Recovery-bundle download requested; disk persistence is not confirmed. Local drafts and modified state remain.');
                // Preserve the trusted native anchor default, including keyboard Enter.
            } catch (error) { event.preventDefault(); this.failure(error); }
        });
        parent.append(link); return link;
    }
    button(parent, text, action) {
        const button = document.createElement('button'); button.type = 'button'; button.textContent = text;
        button.addEventListener('click', () => Promise.resolve().then(action).catch(error => this.failure(error)));
        parent.append(button); return button;
    }
    confirm(text) {
        return new Promise(resolve => {
            const dialog = document.createElement('dialog'); dialog.setAttribute('aria-label','Recovery confirmation');
            const form = document.createElement('form'); form.method = 'dialog'; const label = document.createElement('p'); label.textContent = text; form.append(label);
            for (const [text,value] of [['Continue','continue'],['Cancel','cancel']]) { const button = document.createElement('button'); button.textContent = text; button.value = value; form.append(button); }
            dialog.append(form); document.body.append(dialog);
            dialog.addEventListener('close', () => { const accepted = dialog.returnValue === 'continue'; dialog.remove(); resolve(accepted); }, {once:true});
            dialog.showModal();
        });
    }
    mount() {
        const style = document.createElement('style');
        style.textContent = '.office-recovery{display:inline-block;margin-left:10px;font-size:12px}.office-recovery summary{cursor:pointer}.office-recovery section{position:fixed;z-index:20;right:12px;top:42px;width:min(620px,calc(100vw - 24px));max-height:75vh;overflow:auto;padding:16px;border:1px solid #94a3b8;background:#fff;color:#172033;box-shadow:0 5px 24px #0003}.office-recovery button,dialog button{margin:4px;padding:6px 10px}.office-recovery a.recovery-export{display:inline-block;margin:4px;padding:6px 10px;background:#f3f4f6;color:#172033;border:1px solid #888;border-radius:3px;text-decoration:none}.recovery-draft{border-top:1px solid #cbd5e1;padding:8px 0}.office-recovery [data-state=unavailable],.office-recovery [data-state=warning]{color:#9a3412}';
        document.head.append(style);
        const details = document.createElement('details'); details.id = 'office-recovery'; details.className = 'office-recovery';
        this.summary = document.createElement('summary'); this.summary.textContent = 'Recovery: starting'; details.append(this.summary);
        const section = document.createElement('section'); section.setAttribute('aria-label','Local recovery and storage'); details.append(section);
        const explanation = document.createElement('p'); explanation.textContent = 'Drafts stay on this browser and origin until deleted. They are separate from cached app files and are not synchronized. Quota, private browsing, user clearing or eviction may remove them. Export a separate backup.'; section.append(explanation);
        this.status = document.createElement('p'); this.status.id = 'recovery-status'; this.status.setAttribute('role','status'); this.status.setAttribute('aria-live','polite'); section.append(this.status);
        const label = document.createElement('label'); this.toggle = document.createElement('input'); this.toggle.type = 'checkbox'; this.toggle.checked = true; label.append(this.toggle, ' Automatic checkpoints'); section.append(label);
        this.toggle.addEventListener('change', () => {
            this.enabled = this.toggle.checked;
            try { localStorage.setItem(this.preferenceKey, JSON.stringify({schema:1,enabled:this.enabled})); }
            catch { this.preferenceWarning = 'Preferences could not be saved; setting applies to this page only.'; }
            this.message(this.enabled ? 'ready' : 'paused', this.preferenceWarning || (this.enabled ? 'Automatic checkpoints enabled.' : 'Automatic checkpoints paused. Existing drafts remain.'));
            if (this.enabled) this.flush(true); else clearTimeout(this.timer);
        });
        this.button(section,'Save checkpoint now', () => this.flush(true));
        this.button(section,'Refresh drafts', () => this.refresh());
        this.exportLink(section,'Export current recovery', () => this.snapshot());
        this.button(section,'Request persistent storage', async () => {
            const granted = await navigator.storage?.persist?.();
            this.message('persistence', granted ? 'Persistent storage granted. This is still not a backup guarantee.' : 'Persistent storage was not granted or is unsupported. Export a separate backup.');
        });
        this.button(section,'Clear local data', async () => {
            if (!await this.confirm('Clear all recovery drafts, migration backups and recovery preferences for this app and origin? Export important drafts first. The open document remains; automatic checkpoints will pause.')) return;
            try {
                this.clearing = true;
                this.enabled = false; this.toggle.checked = false; clearTimeout(this.timer); await this.queue;
                await this.store.clear();
                let preferencesCleared = true;
                try { localStorage.removeItem(this.preferenceKey); } catch { preferencesCleared = false; }
                await this.renderList(); this.message('paused',`Local recovery drafts cleared. Open document retained; automatic checkpoints paused for this page.${preferencesCleared ? '' : ' Preferences could not be cleared.'}`);
            } catch (error) { this.failure(error); } finally { this.clearing = false; }
        });
        const importLabel = document.createElement('label'); importLabel.textContent = 'Import recovery bundle ';
        const input = document.createElement('input'); input.type = 'file'; input.accept = '.json,application/json'; input.setAttribute('aria-label','Import recovery bundle'); importLabel.append(input); section.append(importLabel);
        input.addEventListener('change', async () => {
            try {
                const file = input.files?.[0]; if (!file) return;
                if (this.clearing) throw new Error('Wait until local-data clearing finishes.');
                if (file.size > maximumPayloadCharacters + 65536) throw new Error('Recovery bundle exceeds the size limit.');
                const record = validateRecord(JSON.parse(await file.text()), this.app);
                await this.store.put({...record,id:id(),documentId:id(),tabId:this.tabId,updatedAt:Date.now()});
                await this.refresh();
            } catch (error) { this.failure(error); } finally { input.value = ''; }
        });
        this.list = document.createElement('div'); this.list.id = 'recovery-drafts'; section.append(this.list);
        (document.querySelector('.office-nav') || document.body).append(details);
    }
}
