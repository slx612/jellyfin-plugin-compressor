const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// Run the shipped page script with a small DOM and API boundary. No copied page logic.
class Element {
    constructor() { this.children = []; this.style = {}; this.dataset = {}; this.value = ''; this.textContent = ''; this.disabled = false; this.hidden = false; }
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
        globalThis.testPanel = { beginManual, pollManual, refresh, load, renderMovies, get id() { return manualOperationId; } };
    }());`), context);
    return { ...context.testPanel, get id() { return context.testPanel.id; }, element };
}
function text(node) { return [node.textContent, ...node.children.map(text)].join(' '); }
function find(node, predicate) { if (predicate(node)) return node; for (const child of node.children) { const found = find(child, predicate); if (found) return found; } }

const catalogMovie = { Id: 'movie-1', Name: 'Wonka', Path: '/movies/Wonka.mkv', Year: 2023,
    Size: 22226034037, Height: 2160, Format: 'Dolby Vision', Selectable: true, Reason: '', HasImage: false };
const savedConfig = { AutomaticCompressionEnabled: false, IncludedFolders: ['/movies'], ExcludedFolders: [],
    QuarantineDirectory: '/originals', RetentionDays: 7, QuarantineMaxBytes: 0, MinSavingsPercent: 15,
    FileStabilitySeconds: 60, MinMovieSizeGb: 5, ProcessingWindowStartMinutes: 0, ProcessingWindowStopMinutes: 0,
    Profiles: [{ Crf: 20, VideoEncoder: 'hevc_nvenc', ResolutionMode: 2, MaxHeight: 1080 }] };

test('opening the page shows movies and their media facts without a search or an analysis', async () => {
    const ui = panel(async route => route === 'Configuration' ? savedConfig : route === 'Catalog' ? {
        Items: [catalogMovie], Folders: [{ Name: 'Películas', Path: '/movies' }]
    } : []);
    await ui.load();
    const rendered = text(ui.element('jcMovieResults'));
    assert.match(rendered, /Wonka/);
    assert.match(rendered, /20\.70 GB/);
    assert.match(rendered, /Dolby Vision/);
    assert.match(rendered, /2160p/);
});

test('selecting on different filtered views keeps only those movies and requires a fresh review after a change', async () => {
    const other = { ...catalogMovie, Id: 'movie-2', Name: 'Dune', Path: '/movies/Dune.mkv', Format: 'SDR' };
    const calls = [];
    const ui = panel(async (route, options) => {
        calls.push([route, options.data && JSON.parse(options.data)]);
        if (route === 'Configuration') return savedConfig;
        if (route === 'Catalog') return { Items: [catalogMovie, other], Folders: [] };
        if (route === 'Selection/Review') return { Id: 'review-1', Kind: 'Review', State: 'Running', Progress: 0 };
        if (route === 'Manual/review-1') return { Id: 'review-1', Kind: 'Review', State: 'Completed', Result: [
            { ItemId: 'movie-1', Eligible: true, Reason: 'Apta preliminarmente' },
            { ItemId: 'movie-2', Eligible: false, Reason: 'Ya procesada; no se recomprime.' }] };
        return [];
    });
    await ui.load();
    ui.element('jcMovieSearch').value = 'Wonka'; ui.element('jcMovieSearch').oninput();
    ui.element('jcSelectVisible').onclick();
    ui.element('jcMovieSearch').value = 'Dune'; ui.element('jcMovieSearch').oninput();
    ui.element('jcSelectVisible').onclick();
    await ui.element('jcReviewSelection').onclick();
    assert.deepEqual(calls.find(([route]) => route === 'Selection/Review')[1], ['movie-1', 'movie-2']);
    assert.equal(calls.some(([route]) => /Selection\/Queue/.test(route)), false);
    assert.equal(ui.element('jcQueueSelection').hidden, false);
    assert.match(text(ui.element('jcMovieResults')), /Ya procesada/);
    ui.element('jcClearSelection').onclick();
    assert.equal(ui.element('jcQueueSelection').hidden, true);
    await ui.element('jcQueueSelection').onclick();
    assert.equal(calls.some(([route]) => /Selection\/Queue/.test(route)), false);
});

test('multiple selected movies explain that manual HDR batches run one at a time', async () => {
    const ui = panel(async route => route === 'Configuration' ? savedConfig : route === 'Catalog'
        ? { Items: [catalogMovie, { ...catalogMovie, Id: 'movie-2', Name: 'Dune', Format: 'HDR10' }], Folders: [] } : []);
    await ui.load(); ui.element('jcSelectVisible').onclick();
    assert.match(ui.element('jcSelectionHelp').textContent, /HDR.*lotes manuales/);
    assert.match(ui.element('jcSelectionHelp').textContent, /una película cada vez/);
});

test('a busy review cannot lose its selection through a catalog refresh', async () => {
    const ui = panel(async route => route === 'Configuration' ? savedConfig : route === 'Catalog' ? { Items: [catalogMovie], Folders: [] }
        : route === 'Selection/Review' ? { Id: 'review-2', Kind: 'Review', State: 'Running', Progress: 0 }
        : route === 'Manual/review-2' ? { Id: 'review-2', Kind: 'Review', State: 'Running', Progress: 25 } : []);
    await ui.load(); ui.element('jcSelectVisible').onclick();
    await ui.element('jcReviewSelection').onclick();
    assert.equal(ui.element('jcCatalogRefresh').disabled, true);
    assert.equal(ui.element('jcSelectionProgress').hidden, false);
    assert.equal(ui.element('jcSelectionProgress').value, 25);
    assert.equal(ui.element('jcCancelAnalysis').hidden, false);
    assert.equal(ui.element('jcCancelReview').hidden, false);
});

test('queueing uses only reviewed movies and shows a final refusal beside the movie', async () => {
    const calls = [];
    const ui = panel(async (route, options) => {
        calls.push([route, options.data && JSON.parse(options.data)]);
        if (route === 'Configuration') return savedConfig;
        if (route === 'Catalog') return { Items: [catalogMovie, { ...catalogMovie, Id: 'movie-2', Name: 'Dune', Path: '/movies/Dune.mkv' }], Folders: [] };
        if (route === 'Selection/Review') return { Id: 'review-3', Kind: 'Review' };
        if (route === 'Manual/review-3') return { Kind: 'Review', State: 'Completed', Result: [
            { ItemId: 'movie-1', Eligible: true, Reason: 'Apta preliminarmente' },
            { ItemId: 'movie-2', Eligible: false, Reason: 'En reproducción; se aplaza.' }] };
        if (route === 'Selection/Queue?batch=true') return { Id: 'queue-1', Kind: 'Selection' };
        if (route === 'Manual/queue-1') return { Kind: 'Selection', State: 'Completed', Result: [
            { ItemId: 'movie-1', Eligible: false, Reason: 'Ya procesada; no se recomprime.' }] };
        return [];
    });
    await ui.load(); ui.element('jcSelectVisible').onclick();
    await ui.element('jcReviewSelection').onclick();
    await ui.element('jcQueueSelection').onclick();
    assert.deepEqual(calls.find(([route]) => route === 'Selection/Queue?batch=true')[1], ['movie-1']);
    assert.match(text(ui.element('jcMovieResults')), /Ya procesada; no se recomprime/);
    assert.match(ui.element('jcMessage').textContent, /0 películas añadidas.*1 omitidas/);
    assert.equal(ui.element('jcQueueSelection').hidden, true);
});

test('reopening a completed review restores its selection and asks for a current review', async () => {
    const result = { Id: 'previous-review', Kind: 'Review', State: 'Completed', Result: [
        { ItemId: 'movie-1', Eligible: true, Reason: 'Apta preliminarmente' }] };
    const ui = panel(async route => route === 'Configuration' ? savedConfig
        : route === 'Catalog' ? { Items: [catalogMovie], Folders: [] } : route === 'Manual/previous-review' ? result : []);
    await ui.load(); await ui.beginManual(result, '', true);
    assert.equal(ui.element('jcSelectionBar').hidden, false);
    assert.match(ui.element('jcSelectionSummary').textContent, /1 seleccionada/);
    assert.match(ui.element('jcMessage').textContent, /recuperada.*ajustes actuales/);
    assert.equal(ui.element('jcQueueSelection').hidden, true);
    assert.equal(ui.element('jcReviewSelection').disabled, false);
});

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
    Request: { Id: 'transaction-1', ItemId: 'movie-1', SourcePath: '/movies/Movie.mkv', Source: { Sha256: 'source-1' }, Output: { Sha256: 'output-1' } },
    Phase: 'Restored', LibraryRefreshPending: false,
    RetentionStatus: 'Restauración verificada; copias de cuarentena eliminadas.'
};
const job = { Id: 'job-1', ItemId: 'movie-1', SourcePath: '/movies/Movie.mkv', DisplayName: 'Movie',
    CreatedUtc: '2026-10-03T10:00:00Z', Status: 'Completed', StatusDetail: 'Comprimida; original sujeto al plazo.',
    Snapshot: { Source: { Sha256: 'source-1' } }, VerifiedOutputIdentity: { Sha256: 'output-1' } };

test('each job uses the transaction for its own source and output at a reused movie path', async () => {
    const older = { ...job, DisplayName: 'Older encode' };
    const newer = { ...job, Id: 'job-2', DisplayName: 'Newer encode', CreatedUtc: '2026-10-03T11:00:00Z',
        Snapshot: { Source: { Sha256: 'source-2' } }, VerifiedOutputIdentity: { Sha256: 'output-2' } };
    const purged = { ...restored, Phase: 'Purged', CompletedUtc: '2026-10-03T10:00:00Z' };
    const newest = { ...restored, CompletedUtc: '2026-10-03T11:00:00Z', Request: { ...restored.Request,
        Source: { Sha256: 'source-2' }, Output: { Sha256: 'output-2' } } };
    const ui = panel(async route => route === 'Originals' ? [purged, newest] : { Jobs: [older, newer], Paused: false });
    await ui.refresh();
    const rendered = text(ui.element('jcHistory'));
    assert.match(rendered, /Newer encode.*Original restaurado/);
    assert.match(rendered, /Older encode.*Comprimida; original eliminado/);
});

test('jobs without verified identities retain their historical detail', async () => {
    const legacy = { ...job, Snapshot: null, VerifiedOutputIdentity: null };
    const ui = panel(async route => route === 'Originals' ? [restored] : { Jobs: [legacy], Paused: false });
    await ui.refresh();
    assert.match(text(ui.element('jcHistory')), /Comprimida; original sujeto al plazo/);
});

test('HDR verification shows its own percentage and keeps cancellation available', async () => {
    const processing = { ...job, Status: 'Processing', StatusDetail: 'Verificando metadatos HDR por fotograma', Progress: 42,
        Phase: { Name: 'Verificando metadatos HDR por fotograma', Progress: 42, StartedUtc: '2026-10-03T10:00:00Z' } };
    const ui = panel(async route => route === 'Originals' ? [] : { Jobs: [processing], Paused: false });
    await ui.refresh();
    assert.match(text(ui.element('jcRunning')), /42 % de esta fase/);
    assert.match(text(ui.element('jcRunning')), /Cancelar/);
});

test('restored movies show their current state instead of claiming the compressed file is still installed', async () => {
    const ui = panel(async route => route === 'Originals' ? [restored] : { Jobs: [job], Paused: false });
    await ui.refresh();
    assert.match(text(ui.element('jcHistory')), /Original restaurado/i);
    assert.doesNotMatch(text(ui.element('jcHistory')), /Comprimida; original sujeto/);
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
    assert.match(ui.element('jcMessage').textContent, /Cola/);
    assert.doesNotMatch(ui.element('jcMessage').textContent, /operación falló/);
});

test('active work remains visible beside a long history and pending films keep FIFO order', async () => {
    const jobs = Array.from({ length: 105 }, (_, i) => ({ ...job, Id: 'done-' + i, DisplayName: 'Finished ' + i }));
    jobs.push({ ...job, Id: 'running', DisplayName: 'Running movie', Status: 'Processing' },
        { ...job, Id: 'later', DisplayName: 'Later movie', Status: 'Pending', CreatedUtc: '2026-10-03T12:00:00Z' },
        { ...job, Id: 'first', DisplayName: 'First movie', Status: 'Pending', CreatedUtc: '2026-10-03T11:00:00Z' });
    const ui = panel(async route => route === 'Originals' ? [] : { Jobs: jobs, Paused: false });
    await ui.refresh();
    assert.match(text(ui.element('jcRunning')), /Running movie/);
    const pending = text(ui.element('jcPending'));
    assert.ok(pending.indexOf('First movie') < pending.indexOf('Later movie'));
    assert.doesNotMatch(pending, /Finished|Running movie/);
    assert.equal(ui.element('jcHistoryCount').textContent, '105');
    assert.equal(ui.element('jcHistoryNext').disabled, false);
});

test('legacy and unmeasured phases never show a stale encode percentage', async () => {
    let phase = null;
    const ui = panel(async route => route === 'Originals' ? [] : { Jobs: [{ ...job, Status: 'Processing',
        Progress: 100, StatusDetail: 'Comprimiendo', Phase: phase }], Paused: false });
    await ui.refresh();
    assert.match(text(ui.element('jcRunning')), /En ejecución/);
    assert.doesNotMatch(text(ui.element('jcRunning')), /100 %/);
    phase = { Name: 'Reinsertando Dolby Vision 8.1', Progress: null, StartedUtc: '2026-10-03T10:00:00Z' };
    await ui.refresh();
    assert.match(text(ui.element('jcRunning')), /Reinsertando Dolby Vision.*En ejecución/);
    assert.doesNotMatch(text(ui.element('jcRunning')), /100 %|0 %/);
});

test('cancel immediately shows pending feedback, survives polling, and does not claim completion on acknowledgement', async () => {
    let acknowledge, status = 'Processing';
    const ui = panel(async route => route.endsWith('/Cancel') ? new Promise(resolve => { acknowledge = resolve; })
        : route === 'Originals' ? [] : { Jobs: [{ ...job, Status: status }], Paused: false });
    await ui.refresh();
    const cancel = find(ui.element('jcRunning'), node => node.textContent === 'Cancelar');
    const request = cancel.onclick();
    assert.equal(cancel.disabled, true);
    assert.match(text(cancel), /Cancelando/);
    await Promise.resolve();
    await ui.refresh();
    assert.match(text(ui.element('jcRunning')), /Cancelando/);
    acknowledge(); await request;
    assert.match(text(ui.element('jcRunning')), /Cancelando/);
    status = 'Cancelled'; await ui.refresh();
    assert.match(text(ui.element('jcHistory')), /Cancelada/);
    assert.doesNotMatch(text(ui.element('jcRunning')), /Cancelando/);
    assert.match(ui.element('jcMessage').textContent, /Cancelada/);
});

test('pause displays its request immediately and a failed request makes controls usable again', async () => {
    let reject;
    const ui = panel(async route => route === 'Pause' ? new Promise((_, fail) => { reject = fail; })
        : route === 'Originals' ? [] : { Jobs: [], Paused: false });
    await ui.refresh();
    const request = ui.element('jcPause').onclick();
    assert.equal(ui.element('jcPause').disabled, true);
    assert.match(ui.element('jcQueueState').textContent, /Pausando/);
    await Promise.resolve(); reject(new Error('no connection')); await request;
    assert.match(ui.element('jcMessage').textContent, /no connection/);
    assert.equal(ui.element('jcPause').disabled, false);
});

test('completed summary uses actual output facts and shows separate phase durations', async () => {
    const completed = { ...job, Snapshot: { Source: { Length: 10737418240 } }, OutputSizeBytes: 5368709120,
        OutputWidth: 1920, OutputHeight: 902, VerifiedFormat: 'HDR10 · HDR10+',
        StartedUtc: '2026-10-03T10:00:00Z', FinishedUtc: '2026-10-03T10:07:00Z',
        CompletedPhases: [{ Name: 'Comprimiendo', StartedUtc: '2026-10-03T10:00:00Z', FinishedUtc: '2026-10-03T10:05:00Z' },
            { Name: 'Verificando el vídeo', StartedUtc: '2026-10-03T10:05:00Z', FinishedUtc: '2026-10-03T10:07:00Z' }] };
    const ui = panel(async route => route === 'Originals' ? [] : { Jobs: [completed], Paused: false });
    await ui.refresh();
    const summary = text(ui.element('jcHistory'));
    assert.match(summary, /10\.00 GB.*5\.00 GB.*50\.0 %/);
    assert.match(summary, /1920 × 902/);
    assert.match(summary, /HDR10 · HDR10\+/);
    assert.match(summary, /Comprimiendo.*5 min.*Verificando el vídeo.*2 min/);
    ui.element('jcHistoryFilter').value = 'Failed'; ui.element('jcHistoryFilter').onchange();
    assert.doesNotMatch(text(ui.element('jcHistory')), /Movie/);
});

test('an older status response cannot undo an acknowledged pause', async () => {
    let stale, reads = 0;
    const ui = panel(async route => route === 'Originals' ? [] : route === 'Pause' ? undefined
        : ++reads === 1 ? { Jobs: [], Paused: false } : new Promise(resolve => { stale = resolve; }));
    await ui.refresh();
    const inFlight = ui.refresh();
    await ui.element('jcPause').onclick();
    stale({ Jobs: [], Paused: false }); await inFlight;
    assert.match(ui.element('jcQueueState').textContent, /Cola pausada/);
    assert.equal(ui.element('jcResume').hidden, false);
});

test('a lost refresh after acknowledging cancel keeps the pending state until a terminal result arrives', async () => {
    let failRefresh = false, status = 'Processing';
    const ui = panel(async route => {
        if (route.endsWith('/Cancel')) { failRefresh = true; return; }
        if (route === 'Originals') return [];
        if (failRefresh) throw new Error('connection lost');
        return { Jobs: [{ ...job, Status: status }], Paused: false };
    });
    await ui.refresh();
    await find(ui.element('jcRunning'), node => node.textContent === 'Cancelar').onclick();
    assert.match(text(ui.element('jcRunning')), /Cancelando/);
    const pending = find(ui.element('jcRunning'), node => node.textContent === 'Cancelando…');
    assert.equal(pending.disabled, true);
    failRefresh = false; status = 'Cancelled'; await ui.refresh();
    assert.match(ui.element('jcMessage').textContent, /Cancelada/);
});
