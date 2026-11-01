// Browser-only Office panels invoked from each application's managed Office menu.
export function createOfficeWebMenu({ product, recovery }) {
    document.body.style.setProperty('--office-operation-height', product === 'writer' ? '32px' : '28px');
    const nav = document.querySelector('.office-nav');
    const information = document.querySelector(product === 'writer' ? '#writer-info' : '.limits');
    const recoveryDetails = document.getElementById('office-recovery');
    const dialog = document.createElement('dialog');
    dialog.id = 'office-web-dialog'; dialog.setAttribute('aria-labelledby', 'office-web-heading');
    const heading = document.createElement('h2'); heading.id = 'office-web-heading';
    const close = document.createElement('button'); close.type = 'button'; close.textContent = 'Close';
    close.addEventListener('click', () => dialog.close());
    const header = document.createElement('header'); header.append(heading, close); dialog.append(header);
    const recoveryPanel = document.createElement('section'); recoveryPanel.append(recoveryDetails);
    const informationPanel = document.createElement('section'); informationPanel.append(information);
    const name = product === 'cell' ? document.getElementById('cells-name') : null;
    if (name) informationPanel.prepend(name);
    dialog.append(recoveryPanel, informationPanel); document.body.append(dialog);
    const notice = document.createElement('button'); notice.id = 'office-web-notice'; notice.hidden = true;
    notice.type = 'button'; notice.addEventListener('click', () => show('recovery')); document.body.append(notice);
    new MutationObserver(() => {
        const state = recovery.status.dataset.state;
        notice.hidden = !['warning', 'unavailable'].includes(state);
        notice.textContent = notice.hidden ? '' : 'Recovery needs attention — open Recovery';
        notice.setAttribute('aria-label', recovery.status.textContent);
        if (state === 'restored' && dialog.open && !recoveryPanel.hidden) dialog.close();
    }).observe(recovery.status, { attributes: true, childList: true, subtree: true });
    const save = document.getElementById('writer-save-file');
    function show(action) {
        if (action === 'home') { nav.querySelector('a').click(); return; }
        if (action === 'save-file') {
            if (save && !save.hidden) save.click();
            else document.getElementById(product === 'writer' ? 'writer-operation' : 'cells-operation').textContent = 'Save to file is unavailable in this browser. Use File → Export download.';
            return;
        }
        heading.textContent = action === 'recovery' ? 'Recovery' : product === 'writer' ? 'Document information' : 'Spreadsheet information';
        recoveryPanel.hidden = action !== 'recovery'; informationPanel.hidden = action === 'recovery';
        recoveryDetails.open = true; information.open = true;
        if (!dialog.open) dialog.showModal();
        close.focus();
    }
    dialog.addEventListener('close', () => document.querySelector('canvas')?.focus({ preventScroll: true }));
    window.addEventListener('broiler-open-office-panel', event => show(event.detail));
    return { show, ready() { nav.hidden = true; document.body.classList.add('office-menu-ready'); } };
}
