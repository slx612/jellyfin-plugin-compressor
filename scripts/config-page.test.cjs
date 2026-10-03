const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// Run the shipped page script with a small DOM and API boundary. No copied page logic.
class Element {
    constructor() { this.children = []; this.style = {}; this.textContent = ''; this.disabled = false; this.hidden = false; }
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.children = children; }
    setAttribute() {}
    addEventListener() {}
    querySelectorAll() { return []; }
    createTHead() { const head = new Element(); this.append(head); return head; }
    createTBody() { const body = new Element(); this.append(body); return body; }
    insertRow() { const row = new Element(); this.append(row); return row; }
    insertCell() { const cell = new Element(); this.append(cell); return cell; }
}
function panel(request) {
    const nodes = new Map();
    const element = id => { if (!nodes.has(id)) nodes.set(id, new Element()); return nodes.get(id); };
    const page = element('CompressorPage');
    page.querySelector = selector => element(selector.slice(1));
    const html = fs.readFileSync(path.join(__dirname, '../Jellyfin.Plugin.PreTranscode/Configuration/configPage.html'), 'utf8');
    const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
    const context = vm.createContext({
        document: { querySelector: () => page, createElement: () => new Element() }, Node: Element,
        ApiClient: { getUrl: route => route, ajax: options => request(options.url.replace('JellyfinCompressor/', ''), options) },
        setInterval: () => 1, clearInterval() {}, setTimeout, clearTimeout
    });
    vm.runInContext(script.replace(/\}\(\)\);\s*$/, `
        active = true; config = {};
        globalThis.testPanel = { beginManual, pollManual, refresh, get id() { return manualOperationId; } };
    }());`), context);
    return { ...context.testPanel, get id() { return context.testPanel.id; }, element };
}
function text(node) { return [node.textContent, ...node.children.map(text)].join(' '); }

test('a transient polling failure keeps tracking the operation until its real completion', async () => {
    let response = { Id: 'restore-1', Kind: 'Restore', State: 'Running', Progress: 30 };
    const ui = panel(async () => { if (response instanceof Error) throw response; return response; });
    await ui.beginManual(response, 'Movie');
    response = new Error('connection lost');
    await ui.pollManual();
    assert.equal(ui.id, 'restore-1');
    assert.match(ui.element('jcMessage').textContent, /conexión|conectar/i);
    response = { Id: 'restore-1', Kind: 'Restore', State: 'Completed', Result: null };
    await ui.pollManual();
    assert.equal(ui.id, null);
    assert.match(ui.element('jcMessage').textContent, /restaurado/i);
});

const restored = {
    Request: { Id: 'transaction-1', ItemId: 'movie-1', SourcePath: '/movies/Movie.mkv' },
    Phase: 'Restored', LibraryRefreshPending: false,
    RetentionStatus: 'Restauración verificada; copias de cuarentena eliminadas.'
};
const job = { Id: 'job-1', ItemId: 'movie-1', SourcePath: '/movies/Movie.mkv', DisplayName: 'Movie',
    CreatedUtc: '2026-10-03T10:00:00Z', Status: 'Completed', StatusDetail: 'Comprimida; original sujeto al plazo.' };

test('restored movies show their current state instead of claiming the compressed file is still installed', async () => {
    const ui = panel(async route => route === 'Originals' ? [restored] : { Jobs: [job], Paused: false });
    await ui.refresh();
    assert.match(text(ui.element('jcJobs')), /Original restaurado/i);
    assert.doesNotMatch(text(ui.element('jcJobs')), /Comprimida; original sujeto/);
});

test('a deferred quarantine cleanup remains visible after restoration', async () => {
    const deferred = { ...restored, RetentionStatus: 'Limpieza de restauración aplazada: falta verificar el original.' };
    const ui = panel(async route => route === 'Originals' ? [deferred] : { Jobs: [], Paused: false });
    await ui.refresh();
    assert.match(text(ui.element('jcOriginals')), /aplazada/);
    assert.equal(ui.element('jcOriginalsCount').textContent, '0');
});

test('manual cleanup follows the background operation and reports its result', async () => {
    let complete = false;
    const operation = { Id: 'purge-1', Kind: 'Purge', State: 'Running', Progress: 0 };
    const ui = panel(async route => {
        if (route === 'Originals/Purge') return operation;
        if (route === 'Manual/purge-1') return complete ? { ...operation, State: 'Completed', Result: { Deleted: 2 } } : operation;
        return route === 'Originals' ? [] : { Jobs: [], Paused: false };
    });
    await ui.element('jcPurge').onclick();
    assert.equal(ui.id, 'purge-1');
    assert.equal(ui.element('jcPurge').disabled, true);
    complete = true;
    await ui.pollManual();
    assert.equal(ui.id, null);
    assert.match(ui.element('jcMessage').textContent, /2 originales/);
    assert.equal(ui.element('jcPurge').disabled, false);
});

test('loss of the operation after a server restart is reported without claiming the file operation failed', async () => {
    let missing = false;
    const operation = { Id: 'restore-1', Kind: 'Restore', State: 'Running', Progress: 50 };
    const ui = panel(async () => { if (missing) throw { status: 404 }; return operation; });
    await ui.beginManual(operation);
    missing = true;
    await ui.pollManual();
    assert.equal(ui.id, null);
    assert.match(ui.element('jcMessage').textContent, /Actividad/);
    assert.doesNotMatch(ui.element('jcMessage').textContent, /operación falló/);
});
