const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const source = fs.readFileSync(path.join(__dirname, '../QuickMail/Assets/HugeRte/editor.html'), 'utf8');
const script = source.slice(source.indexOf('let snapshotVersion ='), source.indexOf('const requestCatalanProofing'));
function image() {
  const attrs = { src: 'blob:https://quickmail.local/id', 'data-mce-src': 'blob:stale', srcset: 'blob:stale 2x' };
  return { get attributes() { return Object.keys(attrs).map(name => ({ name })); },
    getAttribute: name => attrs[name], setAttribute: (name, value) => attrs[name] = value,
    removeAttribute: name => delete attrs[name], attrs };
}
async function capture(cached, fails = false) {
  const copied = image(); const live = image(); const messages = [];
  const clone = { querySelectorAll: selector => selector === 'img[src]' ? [copied] : [],
    get innerHTML() { return `<img src="${copied.attrs.src}">`; } };
  const context = { window: {}, preservedHeadStyles: '',
    quickMailEditor: { getBody: () => ({ cloneNode: () => clone }), getContent: () => 'Hello',
      editorUpload: { blobCache: { getByUri: () => cached ? { blob: () => ({ type: 'image/png' }), base64: () => 'AQID' } : null } } },
    fetch: async () => ({ ok: !fails, blob: async () => ({}) }),
    FileReader: class { readAsDataURL() { this.result = 'data:image/png;base64,AQID'; this.onload(); } },
    chrome: { webview: { postMessage: message => messages.push(JSON.parse(message)) } } };
  vm.createContext(context); vm.runInContext(script, context);
  await vm.runInContext("emitContent('request-1')", context);
  assert.equal(messages.length, 1);
  assert.equal(messages[0].requestId, 'request-1');
  assert.equal(live.attrs.src, 'blob:https://quickmail.local/id');
  if (fails) assert.equal(messages[0].type, 'snapshot-error');
  else {
    assert.equal(messages[0].html.includes('data:image/png;base64,AQID'), true);
    assert.equal(messages[0].html.includes('blob:'), false);
    assert.equal(copied.attrs.srcset, undefined);
    assert.equal(copied.attrs['data-mce-src'], undefined);
  }
}
(async () => {
  await capture(true); await capture(false); await capture(false, true);
  console.log('PASS: embedded snapshot blob cache, fallback, failure, request correlation and live DOM preservation.');
})().catch(error => { console.error(error); process.exitCode = 1; });
