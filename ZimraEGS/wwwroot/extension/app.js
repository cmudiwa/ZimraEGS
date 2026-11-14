/* ZimraEGS extension page. Manager shows this page in a panel on the invoice and credit note views.
   It reads the open document through Manager's API bridge (postMessage), lets the ZimraEGS server
   fiscalise it, and writes the result back the same way. */
(function () {
    'use strict';

    var EXTENSION_NAME = 'ZIMRA';
    // Fixed keys, so installing again updates the buttons instead of adding more.
    var PLACEMENTS = [
        { key: '2b1f6c1e-5a0e-4a53-9e0b-0f6a3c5d7a01', placement: 'sales-invoice-view' },
        { key: '2b1f6c1e-5a0e-4a53-9e0b-0f6a3c5d7a02', placement: 'credit-note-view' }
    ];
    var MONEY_TYPES = [['', 'Do not assume — ask me on the invoice'], ['0', 'Cash'], ['1', 'Card'], ['2', 'Mobile wallet'],
        ['4', 'Credit (not paid yet)'], ['5', 'Bank transfer'], ['6', 'Other']];

    var view = document.getElementById('view');
    var subtitle = document.getElementById('subtitle');
    var state = { page: null, business: null, document: null, baseCurrency: null, payload: null, server: null };

    // ---------- small DOM helpers ----------

    function el(tag, attrs, children) {
        var node = document.createElement(tag);
        Object.keys(attrs || {}).forEach(function (k) {
            if (k === 'text') node.textContent = attrs[k];
            else if (k === 'on') Object.keys(attrs.on).forEach(function (e) { node.addEventListener(e, attrs.on[e]); });
            else if (attrs[k] !== null && attrs[k] !== undefined && attrs[k] !== false) node.setAttribute(k, attrs[k] === true ? '' : attrs[k]);
        });
        (children || []).forEach(function (c) { if (c) node.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
        return node;
    }
    function show() { view.replaceChildren.apply(view, Array.prototype.filter.call(arguments, Boolean)); }
    function note(kind, text) { return el('div', { class: 'note ' + kind, text: text }); }
    function card() { return el('div', { class: 'card' }, Array.prototype.slice.call(arguments)); }
    function rows(pairs) {
        return el('table', {}, pairs.filter(function (p) { return p[1] !== null && p[1] !== undefined && p[1] !== ''; })
            .map(function (p) { return el('tr', {}, [el('th', { text: p[0] }), el('td', { text: String(p[1]) })]); }));
    }
    function money(currency, amount) { return (currency || '') + ' ' + Number(amount).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }); }

    // ---------- Manager bridge ----------

    var inManager = window.parent !== window;

    function ask(message, timeoutMs) {
        return new Promise(function (resolve, reject) {
            if (!inManager) { reject(new Error('Not running inside Manager.')); return; }
            var requestId = (crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random());
            var timer = setTimeout(function () {
                window.removeEventListener('message', onMessage);
                reject(new Error('Manager did not answer (' + (message.path || message.type) + ').'));
            }, timeoutMs || 30000);
            function onMessage(event) {
                var d = event.data;
                if (event.source !== window.parent || !d || d.requestId !== requestId) return;
                if (!d.type || !/-response$/.test(d.type)) return;
                clearTimeout(timer);
                window.removeEventListener('message', onMessage);
                resolve(d);
            }
            window.addEventListener('message', onMessage);
            var out = { requestId: requestId };
            Object.keys(message).forEach(function (k) { out[k] = message[k]; });
            window.parent.postMessage(out, '*');
        });
    }

    // Calls Manager's API as the signed-in user. Returns the body, or null for "not found".
    async function manager(method, path, body, optional) {
        var message = { type: 'api-request', method: method, path: path };
        if (body !== undefined && body !== null) message.body = body;
        var response = await ask(message);
        if (response.status >= 200 && response.status < 300) return response.body;
        if (optional) return null;
        var detail = response.error || (typeof response.body === 'string' ? response.body : '');
        throw new Error('Manager answered ' + response.status + ' for ' + method + ' ' + path + (detail ? ': ' + String(detail).slice(0, 200) : ''));
    }

    async function server(path, body) {
        var response = await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
        var data = null;
        try { data = await response.json(); } catch (e) { /* not JSON */ }
        if (!response.ok) throw new Error((data && (data.Error || data.error)) || ('The ZimraEGS server answered ' + response.status));
        return data;
    }

    // Applies the updates the server asks for (custom field values, setup records) in Manager.
    async function applyCalls(calls) {
        for (var i = 0; i < (calls || []).length; i++) {
            var c = calls[i];
            await manager(c.method || c.Method, c.path || c.ApiUrl, c.body || c.Body);
        }
    }

    function isKey(value) { return typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value); }

    // ---------- reading the open document ----------

    async function readDocument() {
        var isCredit = state.page.path === '/credit-note-view';
        var doc = await manager('GET', '/api3/' + (isCredit ? 'credit-note-form' : 'sales-invoice-form') + '/' + state.page.key);
        if (!doc || typeof doc !== 'object') throw new Error('Manager did not return the document.');

        var cache = {};
        async function lookup(paths, key) {
            if (!isKey(key)) return null;
            if (cache[key] !== undefined) return cache[key];
            var found = null;
            for (var i = 0; i < paths.length && !found; i++) {
                found = await manager('GET', '/api3/' + paths[i] + '/' + key, null, true).catch(function () { return null; });
                if (found && typeof found !== 'object') found = null;
            }
            cache[key] = found;
            return found;
        }

        // Replace references by the records they point to, as far as the receipt needs them.
        var customer = await lookup(['customer-form'], doc.customer);
        if (customer) {
            var currency = await lookup(['foreign-currency-form'], customer.currency);
            if (currency) customer.currency = currency; else delete customer.currency;
            doc.customer = customer;
        }
        if (isCredit && doc.salesInvoice) {
            doc.salesInvoice = await lookup(['sales-invoice-form'], doc.salesInvoice) || doc.salesInvoice;
        }
        var lines = Array.isArray(doc.lines) ? doc.lines : [];
        for (var i = 0; i < lines.length; i++) {
            var line = lines[i];
            if (line.item) line.item = await lookup(['inventory-item-form', 'non-inventory-item-form', 'inventory-kit-form'], line.item) || line.item;
            if (line.taxCode) line.taxCode = await lookup(['tax-code-form'], line.taxCode) || line.taxCode;
        }
        return doc;
    }

    async function load() {
        subtitle.textContent = 'Reading the document from Manager…';
        state.business = await manager('GET', '/api3/business-details-form', null, true) || {};
        state.document = await readDocument();
        var base = await manager('GET', '/api3/base-currency-form', null, true).catch(function () { return null; });
        state.baseCurrency = base && base.code ? base.code : null;
        state.payload = {
            path: state.page.path, key: state.page.key, document: state.document,
            business: state.business, baseCurrency: state.baseCurrency
        };
    }

    // ---------- screens ----------

    function fail(error) {
        subtitle.textContent = '';
        show(card(note('bad', error && error.message ? error.message : String(error)),
            el('div', { class: 'row' }, [el('button', { text: 'Try again', on: { click: start } })])));
    }

    function busy(text) { subtitle.textContent = text; show(card(el('div', { class: 'small', text: 'Please wait…' }))); }

    function backToInvoice() { window.parent.postMessage({ type: 'reload' }, '*'); }

    function settingsCard() {
        var s = state.server || {};
        if (!s.deviceLink) return null;
        var select = el('select', { id: 'defaultMoneyType' }, MONEY_TYPES.map(function (m) {
            return el('option', { value: m[0], text: m[1], selected: String(s.defaultMoneyType === null || s.defaultMoneyType === undefined ? '' : s.defaultMoneyType) === m[0] });
        }));
        var status = el('span', { class: 'small' });
        var save = el('button', {
            text: 'Save default', on: {
                click: async function () {
                    save.disabled = true;
                    try {
                        var value = select.value === '' ? null : parseInt(select.value, 10);
                        var r = await server('/extension/api/settings', { deviceLink: s.deviceLink, defaultMoneyType: value });
                        if (!r.success) throw new Error(r.message);
                        await prepare();
                    } catch (e) { status.textContent = e.message; save.disabled = false; }
                }
            }
        });
        return el('details', {}, [el('summary', { text: 'Default payment type' }),
            el('div', { style: 'margin-top:10px' }, [el('label', { for: 'defaultMoneyType', text: 'When a document does not say how it was paid, report it as' }), select]),
            el('div', { class: 'row' }, [status, save])]);
    }

    // ZIMRA can change the taxes of a device. This creates or updates the matching tax codes in Manager.
    function taxCodesCard() {
        var s = state.server || {};
        if (!s.deviceLink) return null;
        var status = el('div', { class: 'small', style: 'margin-top:8px' });
        var button = el('button', {
            text: 'Update tax codes in Manager', on: {
                click: async function () {
                    button.disabled = true; status.textContent = 'Reading the taxes from ZIMRA…';
                    try {
                        var r = await server('/extension/api/taxcodes', { deviceLink: s.deviceLink });
                        if (!r.success) throw new Error(r.message);
                        await applyCalls(r.calls);
                        status.textContent = 'Tax codes in Manager now match ZIMRA: ' + r.names.join(', ') + '. Use these on your documents.';
                    } catch (e) { status.textContent = e.message; }
                    button.disabled = false;
                }
            }
        });
        return el('details', {}, [el('summary', { text: 'Tax codes' }),
            el('p', { class: 'small', style: 'margin:10px 0', text: 'Creates or updates a tax code in Manager for every tax ZIMRA allows on this device.' }), button, status]);
    }

    function dayCard() {
        var s = state.server || {};
        if (!s.day || !s.deviceLink) return null;
        var children = [el('div', { class: 'small', text: 'Fiscal day ' + s.day.number + (s.day.open ? ' is open. It is closed automatically every night.' : ' is closed. The next document opens a new day.') })];
        if (s.day.lastCloseError) children.push(note('warn', 'Last attempt to close the day failed: ' + s.day.lastCloseError));
        if (s.day.open) {
            var status = el('span', { class: 'small' });
            var button = el('button', {
                class: 'danger', text: 'Close fiscal day now', on: {
                    click: async function () {
                        if (!confirm('Close the fiscal day now? The next document will open a new day.')) return;
                        button.disabled = true; status.textContent = 'Closing…';
                        try {
                            var r = await server('/extension/api/closeday', { deviceLink: s.deviceLink });
                            status.textContent = r.message;
                            if (r.success) { button.remove(); } else { button.disabled = false; }
                        } catch (e) { status.textContent = e.message; button.disabled = false; }
                    }
                }
            });
            children.push(el('div', { class: 'row' }, [status, button]));
        }
        return card.apply(null, children);
    }

    function renderReady(s) {
        var sum = s.summary;
        subtitle.textContent = 'Ready to send';
        var status = el('span', { class: 'small' });
        var send = el('button', {
            class: 'primary', text: 'Send to ZIMRA', on: {
                click: async function () {
                    send.disabled = true; status.textContent = 'Sending to ZIMRA…';
                    try { await fiscalise(); } catch (e) { status.textContent = e.message; send.disabled = false; }
                }
            }
        });
        show(card(
            rows([
                ['Type', sum.receiptType === 'FiscalInvoice' ? 'Invoice' : sum.receiptType === 'CreditNote' ? 'Credit note' : 'Debit note'],
                ['Customer', sum.buyer || 'No customer details (walk-in sale)'],
                ['Lines', sum.lines],
                ['Tax', money(sum.currency, sum.tax)],
                ['Total', money(sum.currency, sum.total)],
                ['Paid by', sum.payments.map(function (p) { return p.type + ' ' + money('', p.amount).trim(); }).join(', ')]
            ]),
            el('p', { class: 'small', style: 'margin:12px 0 0', text: 'Sending cannot be undone. The date and number of the document are set by fiscalisation.' }),
            el('div', { class: 'row' }, [status, send]),
            settingsCard(), taxCodesCard()
        ), dayCard());
    }

    function renderProblem(s) {
        subtitle.textContent = 'Not sent';
        show(card(
            el('h2', { style: 'font-size:16px;margin:0 0 10px', text: s.title || 'This document cannot be sent yet' }),
            el('ul', {}, (s.problems || []).map(function (p) { return el('li', { text: p }); })),
            el('p', { class: 'small', text: 'Nothing was changed. Fix these points in Manager and open this panel again.' }),
            el('div', { class: 'row' }, [el('button', { text: 'Back to the document', on: { click: backToInvoice } }), el('button', { text: 'Check again', on: { click: start } })]),
            settingsCard(), taxCodesCard()
        ), dayCard());
    }

    function renderDone(s, saved) {
        subtitle.textContent = s.approved ? (s.alreadyFiscalised ? 'Already fiscalised' : 'Fiscalised') : 'Recorded by ZIMRA with errors';
        var qr = el('div', { class: 'qr' });
        if (s.qrCode && window.QRCode) {
            try { new QRCode(qr, { text: s.qrCode, width: 116, height: 116, correctLevel: QRCode.CorrectLevel.L }); } catch (e) { /* no QR */ }
        }
        var children = [];
        if (saved === false) children.push(note('warn', 'ZIMRA has the receipt, but the result could not be saved on the document in Manager. Use "Save to Manager again".'));
        if (!s.approved) children.push(note('bad', 'ZIMRA recorded this receipt but flagged it. It stays part of the fiscal day and cannot be sent again. Correct it with a credit note and a new invoice.'));
        if ((s.validationErrors || []).length) {
            children.push(el('ul', {}, s.validationErrors.map(function (e) { return el('li', { text: e.color + ' ' + e.code + ': ' + (e.text || '') }); })));
        }
        children.push(el('div', { class: 'split' }, [
            rows([
                ['Status', s.status], ['Number', s.invoiceNo], ['Receipt date', s.receiptDate],
                ['Total', s.total !== undefined && s.total !== null ? money(s.currency, s.total) : null],
                ['Receipt global number', s.receiptGlobalNo], ['Verification code', s.verificationCode]
            ]),
            s.qrCode ? qr : null
        ]));
        if (s.qrCode) children.push(el('p', { class: 'mono', style: 'margin:10px 0 0', text: s.qrCode }));
        var buttons = [el('button', { class: 'primary', text: 'Back to the document', on: { click: backToInvoice } })];
        if (saved === false) {
            buttons.push(el('button', { text: 'Save to Manager again', on: { click: function () { finish(s, false); } } }));
        }
        children.push(el('div', { class: 'row' }, buttons));
        children.push(settingsCard());
        children.push(taxCodesCard());
        show(card.apply(null, children), dayCard());
    }

    // Stores the result on the document in Manager, then shows it.
    async function finish(s, returnWhenSaved) {
        var saved = true;
        try { await applyCalls(s.calls); } catch (e) { saved = false; console.error(e); }
        renderDone(s, saved);
        // A clean, new receipt: go straight back so the document shows its QR code.
        if (saved && returnWhenSaved && s.approved && !(s.validationErrors || []).length) {
            subtitle.textContent = 'Fiscalised. Returning to the document…';
            setTimeout(backToInvoice, 900);
        }
    }

    async function fiscalise() {
        var s = state.server = await server('/extension/api/fiscalise', state.payload);
        if (s.state === 'done') return finish(s, !s.alreadyFiscalised);
        route(s);
    }

    // ---------- setup ----------

    function renderSetup() {
        subtitle.textContent = 'Connect this business to ZIMRA';
        var f = {};
        function field(id, labelText, attrs) {
            f[id] = el('input', Object.assign({ id: id, autocomplete: 'off' }, attrs || {}));
            return el('div', {}, [el('label', { for: id, text: labelText }), f[id]]);
        }
        f.environment = el('select', { id: 'environment' }, [el('option', { value: 'Simulation', text: 'Test' }), el('option', { value: 'Production', text: 'Production (live)' })]);
        f.defaultMoneyType = el('select', { id: 'setupMoneyType' }, MONEY_TYPES.map(function (m) { return el('option', { value: m[0], text: m[1] }); }));
        var message = el('div', { class: 'hidden' });
        var taxpayer = el('div', { class: 'note info hidden' });
        var steps = el('ul', { class: 'steps hidden' });
        var action = el('button', { class: 'primary', text: 'Check device' });
        var change = el('button', { class: 'hidden', text: 'Change details' });
        var phase = 'check', setup = null;

        function say(kind, text) { message.className = 'note ' + kind; message.textContent = text; }
        function lock(locked) { ['deviceId', 'serialNumber', 'activationKey', 'environment', 'modelName', 'modelVersion'].forEach(function (id) { f[id].disabled = locked; }); }
        function step(text) { var li = el('li', { text: '… ' + text }); steps.appendChild(li); return { done: function () { li.textContent = '✓ ' + text; }, fail: function () { li.textContent = '✗ ' + text; } }; }
        function request() {
            return {
                IntegrationType: f.environment.value, DeviceID: parseInt(f.deviceId.value, 10) || 0,
                DeviceSerialNumber: f.serialNumber.value.trim(), ActivationKey: f.activationKey.value.trim(),
                DeviceModelName: f.modelName.value.trim(), DeviceModelVersion: f.modelVersion.value.trim(),
                DefaultMoneyType: f.defaultMoneyType.value === '' ? null : parseInt(f.defaultMoneyType.value, 10),
                BusinessDetailsJson: null, Api: null
            };
        }

        async function check() {
            var info = await server('/setup/verifytaxpayer', request());
            var a = info.deviceBranchAddress || {};
            taxpayer.className = 'note info';
            taxpayer.replaceChildren(el('div', { class: 'small', text: 'ZIMRA says this device belongs to' }), el('strong', { text: info.taxPayerName || '' }),
                el('div', { class: 'small', text: [info.taxPayerTIN ? 'TIN ' + info.taxPayerTIN : '', info.vatNumber ? 'VAT ' + info.vatNumber : '', info.deviceBranchName, [a.houseNo, a.street, a.city].filter(Boolean).join(' ')].filter(Boolean).join(' · ') }));
            lock(true); phase = 'confirm'; action.textContent = 'Yes, connect this business'; change.classList.remove('hidden');
        }

        async function connect() {
            change.classList.add('hidden'); steps.className = 'steps'; steps.replaceChildren();
            var s1 = step('Register the device with ZIMRA');
            if (!setup) { try { setup = await server('/setup/complete', request()); } catch (e) { s1.fail(); throw e; } }
            s1.done();

            var s2 = step('Create the custom fields, tax codes and invoice theme in Manager');
            try { await applyCalls(setup.ApiCalls); } catch (e) { s2.fail(); throw e; }
            s2.done();

            var s3 = step('Add the ZIMRA button to credit notes');
            try {
                var endpoint = location.origin + '/extension';
                for (var i = 0; i < PLACEMENTS.length; i++) {
                    await manager('PUT', '/api4/extension', {
                        key: PLACEMENTS[i].key, business: state.page.business,
                        value: { name: EXTENSION_NAME, source: 0, endpoint: endpoint, placement: PLACEMENTS[i].placement }
                    });
                }
                s3.done();
            } catch (e) { s3.fail(); console.warn(e); /* not essential: the button can be added by hand */ }

            await start();
        }

        change.addEventListener('click', function () { phase = 'check'; lock(false); taxpayer.className = 'note info hidden'; message.className = 'hidden'; change.classList.add('hidden'); action.textContent = 'Check device'; });
        var form = el('form', {
            on: {
                submit: async function (event) {
                    event.preventDefault(); action.disabled = true; message.className = 'hidden';
                    try { if (phase === 'check') await check(); else await connect(); }
                    catch (e) { say('bad', e.message + (phase === 'confirm' ? ' Click the button to try again.' : '')); if (phase === 'confirm') action.textContent = 'Try again'; }
                    finally { action.disabled = false; }
                }
            }
        }, [
            el('p', { class: 'small', style: 'margin-top:0', text: 'Enter the three values ZIMRA gave you for the device. Everything else is set up for you.' }),
            el('div', { class: 'grid' }, [
                field('deviceId', 'Device ID', { inputmode: 'numeric', required: true }),
                field('serialNumber', 'Serial Number', { required: true }),
                field('activationKey', 'Activation Key', { required: true }),
                el('div', {}, [el('label', { for: 'environment', text: 'ZIMRA environment' }), f.environment]),
                el('div', { class: 'full' }, [el('label', { for: 'setupMoneyType', text: 'When a document does not say how it was paid, report it as' }), f.defaultMoneyType]),
                field('modelName', 'Device model name (as on the ZIMRA form)', { value: 'Server', required: true }),
                field('modelVersion', 'Device model version', { value: 'v1', required: true })
            ]),
            el('div', { style: 'margin-top:12px' }, [message, taxpayer]),
            steps,
            el('div', { class: 'row' }, [el('button', { type: 'button', text: 'Back to the document', on: { click: backToInvoice } }), el('div', { style: 'display:flex;gap:10px' }, [change, action])])
        ]);
        show(card(form));
    }

    // ---------- flow ----------

    function route(s) {
        if (s.state === 'setup') return renderSetup();
        if (s.state === 'unknown-device') {
            subtitle.textContent = 'Device not found';
            return show(card(note('bad', 'This business is linked to a device this ZimraEGS server does not know. Check that the extension points to the server the device was registered on.')));
        }
        if (s.state === 'ready') return renderReady(s);
        if (s.state === 'done') return finish(s, false);
        return renderProblem(s);
    }

    async function prepare() {
        var s = state.server = await server('/extension/api/prepare', state.payload);
        // Housekeeping the server asks for, such as replacing a key still stored in Manager by a device link.
        if (s.state !== 'done' && (s.calls || []).length) { try { await applyCalls(s.calls); } catch (e) { console.warn(e); } }
        route(s);
    }

    function outsideManager() {
        subtitle.textContent = 'Manager extension';
        var endpoint = location.origin + '/extension';
        show(card(
            el('p', { style: 'margin-top:0', text: 'This page works inside Manager. Add it as an extension (custom button) in your business:' }),
            rows([['Name', EXTENSION_NAME], ['Source', 'Url'], ['Endpoint', endpoint], ['Placement', 'sales-invoice-view']]),
            el('p', { class: 'small', style: 'margin:12px 0 0', text: 'Then open a sales invoice and click the ZIMRA button. The button for credit notes is added automatically during setup.' })
        ));
    }

    async function start() {
        try {
            if (!inManager) return outsideManager();
            busy('Asking Manager which document is open…');
            var page = await ask({ type: 'page-request' }, 8000);
            var body = page.body || {};
            var query = body.query || {};
            state.page = { path: body.path || '', key: query.key || query.Key || null, business: query.business || query.Business || null };

            if (['/sales-invoice-view', '/credit-note-view'].indexOf(state.page.path) < 0 || !state.page.key) {
                subtitle.textContent = 'Open a document first';
                return show(card(note('info', 'Open a sales invoice or a credit note in Manager and click the ZIMRA button there.')));
            }

            await load();
            busy('Checking the document…');
            await prepare();
        } catch (e) { fail(e); }
    }

    start();
})();
