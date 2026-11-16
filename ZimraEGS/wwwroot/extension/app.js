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
        if (!response.ok) throw new Error((data && (data.Error || data.error)) || ('The Fiscal Managerio server answered ' + response.status));
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

    // ---------- the ZIMRA button ----------

    function sameEndpoint(value) {
        var mine = (location.origin + '/extension').replace(/^https?:\/\//, '').replace(/\/$/, '').toLowerCase();
        return String(value || '').replace(/^https?:\/\//, '').replace(/\/$/, '').toLowerCase() === mine;
    }

    // The buttons this business already has for this server, as [{ key, placement }].
    async function existingButtons() {
        var list = await manager('GET', '/api4/extension-batch?pageSize=500', null, true).catch(function () { return null; });
        var items = (list && (list.items || list.Items)) || [];
        return items.map(function (it) {
            var v = it.item || it.Item || it.value || it.Value || {};
            return { key: String(it.key || it.Key || '').toLowerCase(), placement: v.placement || v.Placement, endpoint: v.endpoint || v.Endpoint };
        }).filter(function (b) { return sameEndpoint(b.endpoint); });
    }

    // Adds the button where it is missing. A page that already has one, such as the button added by hand
    // to get here, is left alone so it does not end up with two.
    async function installButtons() {
        var existing = await existingButtons();
        var current = (state.page.path || '').replace(/^\//, '');
        for (var i = 0; i < PLACEMENTS.length; i++) {
            var p = PLACEMENTS[i];
            var others = existing.filter(function (b) { return b.placement === p.placement && b.key !== p.key; });
            if (others.length || (p.placement === current && !existing.length)) continue;
            await manager('PUT', '/api4/extension', {
                key: p.key, business: state.page.business,
                value: { name: EXTENSION_NAME, source: 0, endpoint: location.origin + '/extension', placement: p.placement }
            });
        }
    }

    // An earlier version added its own button next to one added by hand. Remove that extra one.
    async function removeDuplicateButtons() {
        try {
            var existing = await existingButtons();
            for (var i = 0; i < PLACEMENTS.length; i++) {
                var p = PLACEMENTS[i];
                var here = existing.filter(function (b) { return b.placement === p.placement; });
                if (here.length > 1 && here.some(function (b) { return b.key === p.key; })) {
                    await manager('DELETE', '/api4/extension?key=' + encodeURIComponent(p.key) + '&business=' + encodeURIComponent(state.page.business || ''), null, true);
                }
            }
        } catch (e) { console.warn(e); }
    }


    // ---------- HS codes for items ----------

    var HS_FIELD = '329de867-9cf1-4dfe-8b06-5084bce788c7';
    var ITEM_KINDS = [
        { kind: 'inventory', label: 'Inventory item', batch: '/api4/inventory-item-batch', form: '/api3/inventory-item-form/', name: ['itemName', 'ItemName'], code: ['itemCode', 'ItemCode'] },
        { kind: 'non-inventory', label: 'Non-inventory item', batch: '/api4/non-inventory-item-batch', form: '/api3/non-inventory-item-form/', name: ['name', 'Name'], code: ['code', 'Code'] }
    ];
    var LIST_PLACEMENTS = [
        { key: '2b1f6c1e-5a0e-4a53-9e0b-0f6a3c5d7a03', placement: 'inventory-items' },
        { key: '2b1f6c1e-5a0e-4a53-9e0b-0f6a3c5d7a04', placement: 'non-inventory-items' }
    ];

    function pick(obj, names) { for (var i = 0; i < names.length; i++) if (obj && obj[names[i]] !== undefined && obj[names[i]] !== null) return obj[names[i]]; return undefined; }
    function businessQuery() { return state.page && state.page.business ? '&business=' + encodeURIComponent(state.page.business) : ''; }

    // Every record of one kind, page by page.
    async function listAll(batchPath) {
        var all = [], skip = 0, size = 500;
        for (;;) {
            var page = await manager('GET', batchPath + '?pageSize=' + size + '&skip=' + skip + businessQuery(), null, true);
            var items = (page && (page.items || page.Items)) || [];
            all = all.concat(items);
            if (items.length < size) return all;
            skip += size;
        }
    }

    function formatCode(c) { return /^\d{8}$/.test(c) ? c.slice(0, 4) + '.' + c.slice(4, 6) + '.' + c.slice(6) : c; }

    // ZIMRA: 4 or 8 digits; all 8 when the item is zero-rated or exempt.
    function checkCode(value, row, known) {
        var v = (value || '').trim();
        if (!v) return { kind: 'bad', text: 'No HS code. ZIMRA needs one on every line of a VAT registered business.' };
        if (!/^\d+$/.test(v) || (v.length !== 4 && v.length !== 8)) return { kind: 'bad', text: 'An HS code has 4 or 8 digits, without dots or spaces.' };
        if (row.zero && v.length !== 8) return { kind: 'bad', text: 'Zero-rated or exempt: ZIMRA needs all 8 digits.' };
        var info = known[v];
        if (info && info.known === false) return { kind: 'warn', text: 'Not in ZIMRA\'s tariff list. Check the code.' };
        if (info && info.known) return { kind: 'ok', text: info.description ? info.description : 'Heading: ' + (info.heading || '').toLowerCase() };
        return { kind: 'ok', text: '' };
    }

    async function renderHsTool() {
        document.querySelector('main').classList.add('wide');
        subtitle.textContent = 'HS codes for items';
        show(card(el('div', { class: 'small', text: 'Reading your items from Manager…' })));

        var taxCodes = {};
        (await listAll('/api4/tax-code-batch')).forEach(function (t) {
            var v = t.item || t.Item || {};
            var taxRate = pick(v, ['taxRate', 'TaxRate']), rate = Number(pick(v, ['rate', 'Rate']) || 0);
            taxCodes[String(t.key || t.Key).toLowerCase()] = { name: pick(v, ['name', 'Name']) || '', zero: taxRate === 0 || taxRate === 'ZeroRate' || rate === 0 };
        });

        var rows = [];
        for (var k = 0; k < ITEM_KINDS.length; k++) {
            var kind = ITEM_KINDS[k];
            (await listAll(kind.batch)).forEach(function (it) {
                var v = it.item || it.Item || {};
                if (pick(v, ['inactive', 'Inactive'])) return;
                var cf = pick(v, ['customFields2', 'CustomFields2']) || {};
                var strings = pick(cf, ['strings', 'Strings']) || {};
                var hs = '';
                Object.keys(strings).forEach(function (key) { if (key.toLowerCase() === HS_FIELD) hs = strings[key] || ''; });
                var taxKey = pick(v, ['hasDefaultTaxCode', 'HasDefaultTaxCode']) ? String(pick(v, ['defaultTaxCode', 'DefaultTaxCode']) || '').toLowerCase() : '';
                var tax = taxCodes[taxKey];
                rows.push({
                    key: it.key || it.Key, kind: kind, name: pick(v, kind.name) || '(no name)', itemCode: pick(v, kind.code) || '',
                    tax: tax ? tax.name : '', zero: tax ? tax.zero : false, original: String(hs).trim(), value: String(hs).trim(), selected: false
                });
            });
        }
        rows.sort(function (a, b) { return a.name.localeCompare(b.name); });

        var known = {};
        async function lookup(codes) {
            var unknown = codes.filter(function (c) { return c && !(c in known); });
            if (!unknown.length) return;
            var r = await server('/extension/api/hscodes/lookup', { codes: unknown });
            Object.keys(r.codes || {}).forEach(function (c) { known[c] = r.codes[c]; });
        }
        await lookup(rows.map(function (r) { return r.value; }));

        var onlyNeeding = rows.some(function (r) { return checkCode(r.value, r, known).kind !== 'ok'; });
        var chosen = null;

        // ---- finder: search the tariff, then apply a code to the ticked items
        var findInput = el('input', { type: 'search', placeholder: 'Search the tariff, e.g. cement, tyres, 2523', autocomplete: 'off' });
        var findResults = el('div', { class: 'suggest' });
        var applyButton = el('button', { class: 'primary', text: 'Apply to ticked items', disabled: true });
        var chosenText = el('span', { class: 'small', text: 'Pick a code from the search results.' });

        function results(list, onPick) {
            if (!list.length) return [el('div', { class: 'small', text: 'Nothing found. Try another word, or a services code (99001000, 99002000, 99003000).' })];
            return list.map(function (c) {
                return el('button', { type: 'button', class: 'pick', on: { click: function () { onPick(c); } } }, [
                    el('span', { class: 'c', text: formatCode(c.code) }), c.description,
                    el('span', { class: 'h', text: (c.heading || '').toLowerCase() })
                ]);
            });
        }

        var searchTimer = null;
        findInput.addEventListener('input', function () {
            clearTimeout(searchTimer);
            searchTimer = setTimeout(async function () {
                var q = findInput.value.trim();
                if (!q) { findResults.replaceChildren(); return; }
                var r = await fetch('/extension/api/hscodes?limit=12&q=' + encodeURIComponent(q)).then(function (x) { return x.json(); });
                findResults.replaceChildren.apply(findResults, results(r.results || [], function (c) {
                    chosen = c;
                    chosenText.textContent = 'Chosen: ' + formatCode(c.code) + ' ' + c.description;
                    updateBar();
                }));
            }, 250);
        });

        // ---- the item table
        var summary = el('div', { class: 'small' });
        var onlyBox = el('input', { type: 'checkbox' });
        onlyBox.checked = onlyNeeding;
        var allBox = el('input', { type: 'checkbox', title: 'Tick all shown' });
        var tbody = el('tbody');
        var saveButton = el('button', { class: 'primary', text: 'Save changes', disabled: true });
        var saveStatus = el('span', { class: 'small' });

        function changed() { return rows.filter(function (r) { return r.value !== r.original; }); }
        function updateBar() {
            var ticked = rows.filter(function (r) { return r.selected; }).length;
            applyButton.disabled = !chosen || !ticked;
            applyButton.textContent = chosen ? 'Apply ' + formatCode(chosen.code) + ' to ' + ticked + ' ticked item' + (ticked === 1 ? '' : 's') : 'Apply to ticked items';
            var n = changed().length;
            saveButton.disabled = !n;
            saveButton.textContent = n ? 'Save ' + n + ' change' + (n === 1 ? '' : 's') + ' to Manager' : 'Save changes';
            var counts = { ok: 0, warn: 0, bad: 0 };
            rows.forEach(function (r) { counts[checkCode(r.value, r, known).kind]++; });
            summary.textContent = rows.length + ' items: ' + counts.ok + ' ready, ' + counts.warn + ' to check, ' + counts.bad + ' missing or not valid.';
        }

        function renderRows() {
            var shown = rows.filter(function (r) { return !onlyBox.checked || checkCode(r.value, r, known).kind !== 'ok' || r.value !== r.original; });
            tbody.replaceChildren.apply(tbody, shown.map(function (r) {
                var box = el('input', { type: 'checkbox' });
                box.checked = r.selected;
                box.addEventListener('change', function () { r.selected = box.checked; updateBar(); });
                var input = el('input', { type: 'text', value: r.value, inputmode: 'numeric', 'aria-label': 'HS code for ' + r.name });
                var status = el('div', { class: 'status' });
                var suggestions = el('div', { class: 'suggest' });
                function paint() {
                    var c = checkCode(r.value, r, known);
                    status.className = 'status ' + c.kind;
                    status.textContent = (r.value !== r.original ? 'Changed. ' : '') + c.text;
                }
                input.addEventListener('input', async function () {
                    r.value = input.value.trim();
                    paint(); updateBar();
                    if (/^\d{4}$|^\d{8}$/.test(r.value)) { await lookup([r.value]); paint(); updateBar(); }
                });
                var suggest = el('button', {
                    type: 'button', text: 'Suggest', on: {
                        click: async function () {
                            suggest.disabled = true;
                            var r2 = await fetch('/extension/api/hscodes?limit=6&q=' + encodeURIComponent(r.name)).then(function (x) { return x.json(); });
                            suggestions.replaceChildren.apply(suggestions, results(r2.results || [], async function (c) {
                                r.value = c.code; input.value = c.code; suggestions.replaceChildren();
                                await lookup([c.code]); paint(); updateBar();
                            }));
                            suggest.disabled = false;
                        }
                    }
                });
                paint();
                return el('tr', {}, [
                    el('td', {}, [box]),
                    el('td', {}, [el('div', { text: r.name }), el('div', { class: 'small', text: [r.kind.label, r.itemCode, r.tax].filter(Boolean).join(' · ') })]),
                    el('td', {}, [el('div', { class: 'toolbar' }, [input, suggest]), status, suggestions])
                ]);
            }));
            if (!shown.length) tbody.replaceChildren(el('tr', {}, [el('td', { colspan: '3', text: rows.length ? 'Every item has a valid HS code.' : 'No active inventory or non-inventory items found.' })]));
            allBox.checked = false;
            updateBar();
        }

        onlyBox.addEventListener('change', renderRows);
        allBox.addEventListener('change', function () {
            var shown = rows.filter(function (r) { return !onlyBox.checked || checkCode(r.value, r, known).kind !== 'ok' || r.value !== r.original; });
            shown.forEach(function (r) { r.selected = allBox.checked; });
            renderRows(); allBox.checked = shown.length > 0 && shown.every(function (r) { return r.selected; });
        });
        applyButton.addEventListener('click', async function () {
            if (!chosen) return;
            await lookup([chosen.code]);
            rows.forEach(function (r) { if (r.selected) { r.value = chosen.code; r.selected = false; } });
            renderRows();
        });
        saveButton.addEventListener('click', async function () {
            var todo = changed();
            var blocked = todo.filter(function (r) { return checkCode(r.value, r, known).kind === 'bad' && r.value; });
            if (blocked.length && !confirm(blocked.length + ' of the changed codes are not valid for ZIMRA. Save anyway?')) return;
            saveButton.disabled = true;
            var done = 0, failed = [];
            for (var i = 0; i < todo.length; i++) {
                var r = todo[i];
                saveStatus.textContent = 'Saving ' + (i + 1) + ' of ' + todo.length + '…';
                var strings = {}; strings[HS_FIELD] = r.value ? r.value : null;   // null removes the value
                try {
                    await manager('PATCH', r.kind.form + r.key, { customFields2: { strings: strings } });
                    r.original = r.value; done++;
                } catch (e) { failed.push(r.name + ': ' + e.message); }
            }
            saveStatus.textContent = done + ' saved' + (failed.length ? ', ' + failed.length + ' failed: ' + failed.join('; ') : '.');
            renderRows();
        });

        var addButtons = el('button', {
            type: 'button', text: 'Add a ZIMRA button to the item lists', on: {
                click: async function () {
                    addButtons.disabled = true;
                    try {
                        for (var i = 0; i < LIST_PLACEMENTS.length; i++) {
                            await manager('PUT', '/api4/extension', {
                                key: LIST_PLACEMENTS[i].key, business: state.page.business,
                                value: { name: 'ZIMRA HS codes', source: 0, endpoint: location.origin + '/extension', placement: LIST_PLACEMENTS[i].placement }
                            });
                        }
                        addButtons.textContent = 'Added: open Inventory Items or Non-inventory Items and click "ZIMRA HS codes".';
                    } catch (e) { addButtons.textContent = 'Could not add the buttons: ' + e.message; }
                }
            }
        });

        var back = state.page && ['/sales-invoice-view', '/credit-note-view'].indexOf(state.page.path) >= 0
            ? el('button', { type: 'button', text: 'Back', on: { click: function () { document.querySelector('main').classList.remove('wide'); route(state.server); } } })
            : el('button', { type: 'button', text: 'Back to Manager', on: { click: backToInvoice } });

        show(
            card(
                el('p', { class: 'small', style: 'margin-top:0', text: 'Find the HS code of each item in ZIMRA\'s tariff and save it on the item. Every invoice line then carries it. Standard-rated items need at least 4 digits; zero-rated and exempt items need all 8.' }),
                el('label', { text: 'Find an HS code' }), findInput, findResults,
                el('div', { class: 'row' }, [chosenText, applyButton])
            ),
            card(
                el('div', { class: 'toolbar', style: 'justify-content:space-between' }, [summary, el('label', {}, [onlyBox, 'Only items that need attention'])]),
                el('table', { class: 'hs-table', style: 'margin-top:10px' }, [
                    el('thead', {}, [el('tr', {}, [el('th', {}, [allBox]), el('th', { text: 'Item' }), el('th', { text: 'HS code' })])]),
                    tbody
                ]),
                el('div', { class: 'row' }, [back, el('div', { class: 'toolbar' }, [saveStatus, saveButton])]),
                el('details', {}, [el('summary', { text: 'More' }), el('div', { style: 'margin-top:10px' }, [addButtons]),
                    el('p', { class: 'small', text: 'Codes come from ZIMRA\'s Tariff Handbook (S.I. 203 of 2022). If unsure, confirm a code on ZIMRA\'s E-Tariff tool or with ZIMRA.' })])
            )
        );
        renderRows();
    }

    function hsToolCard() {
        return el('details', {}, [el('summary', { text: 'HS codes for items' }),
            el('p', { class: 'small', style: 'margin:10px 0', text: 'Find and set the HS code of your inventory and non-inventory items.' }),
            el('button', { type: 'button', text: 'Open the HS code tool', on: { click: function () { renderHsTool().catch(fail); } } })]);
    }

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

    // Subscription notice: warns before a live device's paid period ends, and says why sending is paused.
    function billingNote() {
        var b = (state.server || {}).billing;
        if (!b || b.state === 'Free' || b.state === 'Active') return null;
        var kind = b.maySend ? 'warn' : 'bad';
        var box = el('div', { class: 'note ' + kind }, [
            el('div', { text: b.text }),
            b.instructions ? el('div', { style: 'margin-top:4px', text: b.instructions }) : null,
            el('div', { style: 'margin-top:6px' }, [el('a', { href: b.accountUrl, target: '_blank', rel: 'noopener', text: 'Open your account' })])
        ]);
        return box;
    }

    function dayCard() {
        var s = state.server || {};
        if (!s.day || !s.deviceLink) return null;
        var summary = el('div', { class: 'small', text: !s.day.number ? 'No fiscal day has been opened yet. The first document sent opens one.'
            : 'Fiscal day ' + s.day.number + (s.day.open ? ' is open. It is closed automatically every night.' : ' is closed. The next document opens a new day.') });
        var children = [summary];
        if (s.day.open && s.day.endingSoon) children.push(note('warn', s.day.hoursLeft > 0
            ? 'This fiscal day reaches the maximum length ZIMRA allows in about ' + s.day.hoursLeft + ' hours. It will be closed automatically before then.'
            : 'This fiscal day has reached the maximum length ZIMRA allows. It is closed before the next document is sent.'));
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
                            if (r.success) {
                                // The day is closed now: say so once, instead of next to the old "is open" line.
                                summary.textContent = r.message + ' The next document opens a new day.';
                                status.textContent = '';
                                button.remove();
                            } else { button.disabled = false; }
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
            billingNote(),
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
            settingsCard(), taxCodesCard(), hsToolCard()
        ), dayCard());
    }

    function renderProblem(s) {
        subtitle.textContent = 'Not sent';
        var subscriptionProblem = /subscription|not linked to an account/i.test(s.title || '');
        show(card(
            el('h2', { style: 'font-size:16px;margin:0 0 10px', text: s.title || 'This document cannot be sent yet' }),
            subscriptionProblem && s.billing ? el('div', { style: 'margin-bottom:10px' }, [el('a', { class: 'primary', href: s.billing.accountUrl, target: '_blank', rel: 'noopener', text: 'Open your account' })]) : null,
            el('ul', {}, (s.problems || []).map(function (p) { return el('li', { text: p }); })),
            el('p', { class: 'small', text: subscriptionProblem
                ? 'Nothing was sent. Once the subscription is renewed, open this panel again and send the document.'
                : 'Nothing was changed. Fix these points in Manager and open this panel again.' }),
            el('div', { class: 'row' }, [el('button', { text: 'Back to the document', on: { click: backToInvoice } }),
                el('div', { class: 'toolbar' }, [
                    (s.problems || []).some(function (p) { return /HS Code/i.test(p); })
                        ? el('button', { class: 'primary', text: 'Fix HS codes on items', on: { click: function () { renderHsTool().catch(fail); } } }) : null,
                    el('button', { text: 'Check again', on: { click: start } })])]),
            settingsCard(), taxCodesCard(), hsToolCard()
        ), dayCard());
    }

    function renderDone(s, saved) {
        subtitle.textContent = s.approved ? (s.alreadyFiscalised ? 'Already fiscalised' : 'Fiscalised') : 'Recorded by ZIMRA with errors';
        var qr = el('div', { class: 'qr' });
        if (s.qrCode && window.QRCode) {
            try { new QRCode(qr, { text: s.qrCode, width: 116, height: 116, correctLevel: QRCode.CorrectLevel.L }); } catch (e) { /* no QR */ }
        }
        var children = [billingNote()];
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
        children.push(hsToolCard());
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
        f.accountEmail = el('input', { id: 'accountEmail', type: 'email', autocomplete: 'username' });
        f.accountPassword = el('input', { id: 'accountPassword', type: 'password', autocomplete: 'current-password' });
        var accountBox = el('div', { class: 'full hidden' }, [
            el('div', { class: 'note info', style: 'margin:0 0 8px' }, [
                'A live device needs an account. ',
                el('a', { href: location.origin + '/account/signup', target: '_blank', rel: 'noopener', text: 'Create one' }),
                ' if you have none, then enter it here.'
            ]),
            el('div', { class: 'grid' }, [
                el('div', {}, [el('label', { for: 'accountEmail', text: 'Account e-mail' }), f.accountEmail]),
                el('div', {}, [el('label', { for: 'accountPassword', text: 'Account password' }), f.accountPassword])
            ])
        ]);
        f.environment.addEventListener('change', function () { accountBox.className = f.environment.value === 'Production' ? 'full' : 'full hidden'; });
        f.defaultMoneyType = el('select', { id: 'setupMoneyType' }, MONEY_TYPES.map(function (m) { return el('option', { value: m[0], text: m[1] }); }));
        var message = el('div', { class: 'hidden' });
        var taxpayer = el('div', { class: 'note info hidden' });
        var steps = el('ul', { class: 'steps hidden' });
        var action = el('button', { class: 'primary', text: 'Check device' });
        var change = el('button', { class: 'hidden', text: 'Change details' });
        var phase = 'check', setup = null;

        function say(kind, text) { message.className = 'note ' + kind; message.textContent = text; }
        // The account fields stay editable, so a mistyped password can be corrected.
        function lock(locked) { ['deviceId', 'serialNumber', 'activationKey', 'environment', 'modelName', 'modelVersion'].forEach(function (id) { f[id].disabled = locked; }); }
        function step(text) { var li = el('li', { text: '… ' + text }); steps.appendChild(li); return { done: function () { li.textContent = '✓ ' + text; }, fail: function () { li.textContent = '✗ ' + text; } }; }
        function request() {
            return {
                IntegrationType: f.environment.value, DeviceID: parseInt(f.deviceId.value, 10) || 0,
                DeviceSerialNumber: f.serialNumber.value.trim(), ActivationKey: f.activationKey.value.trim(),
                DeviceModelName: f.modelName.value.trim(), DeviceModelVersion: f.modelVersion.value.trim(),
                DefaultMoneyType: f.defaultMoneyType.value === '' ? null : parseInt(f.defaultMoneyType.value, 10),
                BusinessDetailsJson: null, Api: null,
                AccountEmail: f.environment.value === 'Production' ? f.accountEmail.value.trim() : null,
                AccountPassword: f.environment.value === 'Production' ? f.accountPassword.value : null
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

            var s3 = step('Add the ZIMRA button to invoices and credit notes');
            try { await installButtons(); s3.done(); }
            catch (e) { s3.fail(); console.warn(e); /* not essential: the button can be added by hand */ }

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
                accountBox,
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
            return show(card(note('bad', 'This business is linked to a device this Fiscal Managerio server does not know. Check that the extension points to the server the device was registered on.')));
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

            if (['/inventory-items', '/non-inventory-items'].indexOf(state.page.path) >= 0) {
                return await renderHsTool();
            }
            if (['/sales-invoice-view', '/credit-note-view'].indexOf(state.page.path) < 0 || !state.page.key) {
                subtitle.textContent = 'Open a document first';
                return show(card(note('info', 'Open a sales invoice or a credit note in Manager and click the ZIMRA button there.'),
                    el('div', { class: 'row' }, [el('span'), el('button', { text: 'Set HS codes on items', on: { click: function () { renderHsTool().catch(fail); } } })])));
            }

            await load();
            removeDuplicateButtons();
            busy('Checking the document…');
            await prepare();
        } catch (e) { fail(e); }
    }

    start();
})();
