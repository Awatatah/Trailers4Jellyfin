import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const html = fs.readFileSync(new URL('../Jellyfin.Plugin.Trailers4Jellyfin/Configuration/config.html', import.meta.url), 'utf8');
const script = html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];
const elements = new Map([...html.matchAll(/id="([^"]+)"/g)].map(([, id]) => [id, {
    value: '', checked: false, disabled: false, hidden: false, textContent: '',
    listeners: {}, addEventListener(event, callback) { this.listeners[event] = callback; }
}]));
const get = id => { assert.ok(elements.has(id), 'Unknown element: ' + id); return elements.get(id); };
const handlers = {};
let config = {};
let saved;
let failSave = false;
const requests = [];
let task = { Key: 'Trailers4JellyfinReprocessAudio', Id: 'audio-task', State: 'Idle' };
const context = vm.createContext({
    document: { getElementById: get, querySelector: () => ({ reportValidity: () => true }) },
    $: selector => ({ on: (event, callback) => { handlers[selector + ':' + event] = callback; } }),
    Dashboard: { showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {}, alert() {} },
    ApiClient: {
        getPluginConfiguration: async () => structuredClone(config),
        updatePluginConfiguration: async (_, value) => {
            if (failSave) throw Error('save failed');
            saved = structuredClone(value); config = saved;
        },
        getUrl: path => path,
        ajax: async request => {
            requests.push(request);
            if (request.type === 'GET') return [task];
        }
    },
    setTimeout: () => 1, clearTimeout() {},
});
vm.runInContext(script, context);
const settle = async () => { for (let i = 0; i < 10; i++) await new Promise(resolve => setImmediate(resolve)); };
const change = id => get(id).listeners.change.call(get(id));
const submit = async () => { handlers['form.trailers4JellyfinConfigPage:submit'](); await settle(); };
handlers['.trailers4JellyfinConfigPage:pageshow']();
await settle();
assert.equal(get('enable-volume-reduction').checked, false);
assert.equal(get('trailer-volume').value, 80);
assert.equal(get('trailer-volume').disabled, true);
assert.equal(get('trailer-target-lufs').disabled, true);
console.log('PASS: Settings default off with 80% retained for enabling');
get('enable-volume-reduction').checked = true;
change('enable-volume-reduction');
assert.equal(get('trailer-volume').disabled, false);
assert.equal(get('trailer-target-lufs').disabled, true);
await submit();
assert.equal(saved.EnableTrailerVolumeReduction, true);
assert.equal(saved.TrailerVolumeMode, 'Percentage');
assert.equal(saved.TrailerVolumePercent, 80);
console.log('PASS: Percentage route saves independently');
get('trailer-volume-unit').value = 'Decibels';
change('trailer-volume-unit');
assert.equal(get('trailer-volume').disabled, true);
assert.equal(get('trailer-volume-db').disabled, false);
await submit();
assert.equal(saved.TrailerVolumeMode, 'Decibels');
assert.equal(saved.TrailerVolumeAdjustmentDb, -2);
console.log('PASS: dB unit replaces percentage instead of stacking');
get('trailer-volume-mode').value = 'Normalize';
get('trailer-target-lufs').value = -29;
change('trailer-volume-mode');
assert.equal(get('trailer-volume').disabled, true);
assert.equal(get('trailer-volume-db').disabled, true);
assert.equal(get('trailer-target-lufs').disabled, false);
await submit();
assert.equal(saved.TrailerVolumeMode, 'Normalize');
assert.equal(saved.TrailerTargetLufs, -29);
console.log('PASS: Common-volume route enables only its LUFS field');
get('reprocess-audio').listeners.click.call(get('reprocess-audio'));
await settle();
assert.ok(requests.some(r => r.type === 'POST' && r.url === 'ScheduledTasks/Running/audio-task'));
assert.match(get('audio-task-status').textContent, /started/);
console.log('PASS: Reprocess button saves settings before starting the task');
task = { ...task, State: 'Running', CurrentProgressPercentage: 42.4 };
await context.refreshAudioTask();
assert.equal(get('reprocess-audio').disabled, true);
assert.match(get('audio-task-status').textContent, /42%/);
task = { ...task, State: 'Idle', LastExecutionResult: { StartTimeUtc: new Date().toISOString(), Status: 'Completed' } };
await context.refreshAudioTask();
assert.equal(get('reprocess-audio').disabled, false);
assert.match(get('audio-task-status').textContent, /completed/);
console.log('PASS: Task progress and completion are displayed');
get('enable-volume-reduction').checked = false;
change('enable-volume-reduction');
assert.equal(get('trailer-target-lufs').disabled, true);
const posts = requests.filter(r => r.type === 'POST').length;
failSave = true;
get('reprocess-audio').listeners.click.call(get('reprocess-audio'));
await settle();
assert.equal(requests.filter(r => r.type === 'POST').length, posts);
assert.match(get('audio-task-status').textContent, /Could not save/);
console.log('PASS: Save failure prevents starting a task with stale settings');
handlers['.trailers4JellyfinConfigPage:pagehide']();
assert.equal(context.audioTaskVisible, false);
assert.match(html, /id="trailer-volume"[^>]*min="50" max="90"/);
assert.match(html, /Set all trailers to the same volume/);
assert.match(html, /Both methods use server CPU and extra disk space/);
console.log('PASS: Percentage bounds and plain-language guidance are present');
