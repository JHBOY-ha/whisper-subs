import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';

// Minimal DOM for testing actual shipped event handlers without Jellyfin or an AMD loader.
class Element {
    constructor(tag) { this.tagName = tag; this.children = []; this.style = {}; this.events = {}; this.attrs = {}; this._text = ''; }
    set textContent(value) { this._text = value; this.children = []; }
    get textContent() { return this._text + this.children.map(c => c.textContent).join(''); }
    appendChild(child) { this.children.push(child); child.parent = this; return child; }
    setAttribute(key, value) { this.attrs[key] = value; }
    addEventListener(key, action) { this.events[key] = action; }
    remove() { this.parent.children = this.parent.children.filter(c => c !== this); }
    querySelector(selector) {
        for (const child of this.children) {
            if ((selector[0] === '#' && child.id === selector.slice(1)) ||
                (selector[0] === '.' && child.className === selector.slice(1))) return child;
            const match = child.querySelector(selector); if (match) return match;
        }
        return null;
    }
}
function environment() {
    const body = new Element('body');
    const document = { body, createElement: tag => new Element(tag), addEventListener() {},
        getElementById: id => body.querySelector('#' + id), querySelector: selector => body.querySelector(selector) };
    return { document, console: { log() {}, debug() {} },
        window: { location: { hash: '' }, addEventListener() {}, confirm: () => true },
        MutationObserver: class { observe() {} disconnect() {} }, setTimeout() {}, clearTimeout() {},
        ApiClient: { getCurrentUser: () => Promise.resolve({ Policy: { IsAdministrator: true } }),
            getUrl: (route, params) => route + '?' + new URLSearchParams(params), ajax: () => Promise.resolve({ queued: 1 }) } };
}
function client(env) {
    const source = readFileSync(new URL('../Web/whisperSubs.js', import.meta.url), 'utf8');
    const end = source.lastIndexOf('})();');
    runInNewContext(source.slice(0, end) + 'globalThis.actions = { runAction, showToast };' + source.slice(end), env);
    return env.actions;
}
function panel(env) {
    const source = readFileSync(new URL('../Web/configPage.html', import.meta.url), 'utf8');
    const start = source.indexOf('                _cancellingQueue: false,');
    const end = source.indexOf('                pollQueueStatus: function () {', start);
    runInNewContext('var WhisperSubsConfig = {' + source.slice(start, end) + '};', env);
    const config = env.WhisperSubsConfig;
    config.getAuthHeader = () => ({ Authorization: 'test-token' });
    config.describeLoadError = error => error.message;
    config.pollQueueStatus = () => { env.refreshed = true; };
    const status = new Element('div'); status.id = 'queueActionStatus'; env.document.body.appendChild(status);
    return config;
}
const message = env => env.document.getElementById('whisperSubsActionStatus').querySelector('.whisperSubsActionMessage').textContent;

test('generate displays submitting and queued without require/toast', async () => {
    const env = environment(); let finish;
    env.ApiClient.ajax = () => new Promise(resolve => { finish = resolve; });
    const promise = client(env).runAction('admin', 'film');
    assert.match(message(env), /Queuing/);
    await Promise.resolve(); finish({ queued: 2 }); await promise;
    assert.match(message(env), /Queued 2/);
    assert.match(message(env), /View progress/);
    assert.equal(env.document.body.children.length, 1);
});
test('duplicate and malicious server messages remain visible plain text', async () => {
    const env = environment(); env.ApiClient.ajax = () => Promise.resolve({ queued: 0, message: '<img src=x> Already queued' });
    await client(env).runAction('admin', 'film');
    assert.match(message(env), /<img src=x> Already queued/);
    assert.equal(env.document.getElementById('whisperSubsActionStatus').querySelector('.whisperSubsActionMessage').children.length, 0);
});
test('HTTP and synchronous submission failures produce visible failure', async () => {
    for (const failure of [() => Promise.reject({ status: 403 }), () => { throw Error('offline'); }]) {
        const env = environment(); env.ApiClient.ajax = failure;
        await client(env).runAction('admin', 'film');
        assert.match(message(env), /Failed to queue/);
    }
});
test('status can be dismissed and re-created', () => {
    const env = environment(); const actions = client(env); actions.showToast('one');
    env.document.body.children[0].children[1].events.click();
    assert.equal(env.document.body.children.length, 0);
    actions.showToast('two'); assert.equal(message(env), 'two');
});
test('queue renders exact waiting count, safe labels and cancel keys', async () => {
    const env = environment(); const config = panel(env); const root = new Element('div');
    const job = { key: 'film|auto|>en', name: '<script>bad</script>', language: 'auto' };
    let request; env.fetch = (url, options) => { request = { url, options }; return Promise.resolve({ ok: true, json: () => Promise.resolve({ message: 'Cancelled 1' }) }); };
    config.renderPendingQueue(root, { pendingCount: 205, remaining: 999, pending: [job] });
    assert.match(root.textContent, /Waiting \(205\)/);
    assert.match(root.textContent, /204 more/);
    assert.match(root.textContent, /<script>bad<\/script>/);
    await config.cancelPendingQueue(job.key, 1);
    assert.match(request.url, /Queue\/CancelPending/);
    assert.equal(new URLSearchParams(request.url.split('?')[1]).get('key'), job.key);
    assert.equal(request.options.headers.Authorization, 'test-token');
    assert.equal(env.document.getElementById('queueActionStatus').textContent, 'Cancelled 1');
    assert.equal(env.refreshed, true);
});
test('declined cancellation sends no request', async () => {
    const env = environment(); const config = panel(env); env.window.confirm = () => false;
    env.fetch = () => { throw Error('must not request'); };
    await config.cancelPendingQueue(null, 4); assert.equal(config._cancellingQueue, false);
});
test('running conflict or save failure is shown, controls unlock and queue refreshes', async () => {
    for (const status of [409, 500]) {
        const env = environment(); const config = panel(env);
        env.fetch = () => Promise.resolve({ ok: false, status, json: () => Promise.resolve({ message: 'Job kept' }) });
        await config.cancelPendingQueue('film|auto', 1);
        assert.match(env.document.getElementById('queueActionStatus').textContent, /Cancellation not confirmed: Job kept/);
        assert.equal(config._cancellingQueue, false); assert.equal(env.refreshed, true);
    }
});
test('clear-all uses its explicit route, suppresses double submission', async () => {
    const env = environment(); const config = panel(env); let finish; let requests = 0;
    env.fetch = url => { requests++; assert.match(url, /Queue\/ClearPending/); return new Promise(resolve => { finish = resolve; }); };
    const first = config.cancelPendingQueue(null, 205);
    await config.cancelPendingQueue(null, 205);
    assert.equal(requests, 1);
    finish({ ok: true, json: () => Promise.resolve({ message: 'Cancelled 205' }) }); await first;
    assert.equal(config._cancellingQueue, false);
});
