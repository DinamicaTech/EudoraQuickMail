'use strict';
const el = id => document.getElementById(id);
let accounts = [];
let offset = 0;
let selected = null;
let composeKind = 'new';
const onPages = location.hostname === 'dinamicatech.github.io';
let apiBase = '';
let bearerToken = null; // Never persisted in browser storage.

function status(message) { el('status').textContent = message || ''; }

async function api(path, options = {}) {
  const response = await fetch(`${apiBase}${path}`, {
    credentials: 'same-origin', cache: 'no-store',
    headers: { 'X-QuickMail-Request': '1',
      ...(bearerToken ? { Authorization: `Bearer ${bearerToken}` } : {}),
      ...(options.body ? { 'Content-Type': 'application/json' } : {}) },
    ...options
  });
  if (response.status === 401 && path !== '/api/login') {
    bearerToken = null;
    el('mail').hidden = true;
    el('login').hidden = false;
    throw new Error('Session expired. Sign in again.');
  }
  if (!response.ok) throw new Error(await response.text() || `Request failed (${response.status}).`);
  return response.status === 204 || !response.headers.get('content-type')?.includes('json')
    ? null : response.json();
}

function params(message) {
  return new URLSearchParams({ accountId: message.accountId, folder: message.folder, id: message.id });
}

async function signedIn() {
  el('login').hidden = true;
  el('mail').hidden = false;
  accounts = await api('/api/accounts');
  for (const id of ['account-filter', 'from-account']) {
    const select = el(id);
    select.replaceChildren();
    if (id === 'account-filter') select.add(new Option('All accounts', ''));
    for (const account of accounts) select.add(new Option(`${account.label} <${account.address}>`, account.id));
  }
  await loadMessages();
}

async function loadMessages() {
  status('Loading messages…');
  const query = new URLSearchParams({ offset: String(offset), limit: '40' });
  if (el('account-filter').value) query.set('accountId', el('account-filter').value);
  const data = await api(`/api/messages?${query}`);
  el('messages').replaceChildren();
  for (const message of data.messages) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = `message${message.unread ? ' unread' : ''}`;
    const from = document.createElement('span');
    from.textContent = message.from || '(unknown sender)';
    const subject = document.createElement('strong');
    subject.textContent = message.subject || '(no subject)';
    const date = document.createElement('small');
    date.textContent = new Date(message.date).toLocaleString();
    button.append(from, document.createElement('br'), subject, date);
    button.addEventListener('click', () => openMessage(message).catch(error => status(error.message)));
    el('messages').append(button);
  }
  el('newer').disabled = offset === 0;
  el('older').disabled = !data.hasMore;
  status(`${data.messages.length} messages shown${offset ? `, starting at ${offset + 1}` : ''}.`);
}

async function openMessage(message) {
  selected = message;
  el('compose').hidden = true;
  el('detail').hidden = false;
  status('Loading message…');
  const data = await api(`/api/message?${params(message)}`);
  const panel = el('detail');
  panel.replaceChildren();
  const title = document.createElement('h2'); title.textContent = data.subject || '(no subject)';
  const meta = document.createElement('p');
  meta.textContent = `From: ${data.from}\nTo: ${data.to}\nDate: ${new Date(data.date).toLocaleString()}`;
  meta.className = 'body muted';
  const actions = document.createElement('div'); actions.className = 'actions';
  for (const kind of ['reply', 'forward']) {
    const button = document.createElement('button');
    button.textContent = kind === 'reply' ? 'Reply' : 'Forward';
    button.addEventListener('click', () => prepareCompose(kind).catch(error => status(error.message)));
    actions.append(button);
  }
  const body = document.createElement('p'); body.className = 'body'; body.textContent = data.body || '(No cached body available.)';
  panel.append(title, meta, actions, body);
  if (data.hasAttachments) {
    const note = document.createElement('p'); note.className = 'muted';
    note.textContent = `Attachments: ${data.attachments.join(', ')} (open in desktop EQM)`;
    panel.append(note);
  }
  status('Message loaded.');
}

async function prepareCompose(kind) {
  composeKind = kind;
  el('detail').hidden = true;
  el('compose').hidden = false;
  el('compose-title').textContent = kind === 'new' ? 'New message' : kind === 'reply' ? 'Reply' : 'Forward';
  el('compose-form').reset();
  el('attachment-warning').hidden = true;
  if (kind === 'new') {
    el('from-account').value = el('account-filter').value || accounts[0]?.id || '';
  } else {
    const draft = await api(`/api/compose?kind=${kind}&${params(selected)}`);
    el('from-account').value = selected.accountId;
    el('to').value = draft.to;
    el('subject').value = draft.subject;
    el('body').value = draft.body;
    el('attachment-warning').hidden = kind !== 'forward' || !draft.hasAttachments;
  }
  el('to').focus();
  status('');
}

el('login-form').addEventListener('submit', async event => {
  event.preventDefault();
  try {
    if (onPages) {
      const entered = new URL(el('server').value.trim());
      if (entered.protocol !== 'https:' || !entered.hostname.endsWith('.ts.net') ||
          entered.username || entered.password || entered.pathname !== '/' || entered.search || entered.hash)
        throw new Error('Enter the private Tailscale HTTPS address shown by EQM.');
      apiBase = entered.origin;
      const login = await api('/api/token-login', {
        method: 'POST', body: JSON.stringify({ code: el('code').value })
      });
      bearerToken = login.token;
    } else {
      await api('/api/login', { method: 'POST', body: JSON.stringify({ code: el('code').value }) });
    }
    el('code').value = '';
    await signedIn();
  } catch (error) { el('code').value = ''; alert(error.message); }
});
el('account-filter').addEventListener('change', () => { offset = 0; loadMessages().catch(error => status(error.message)); });
el('newer').addEventListener('click', () => { offset = Math.max(0, offset - 40); loadMessages().catch(error => status(error.message)); });
el('older').addEventListener('click', () => { offset += 40; loadMessages().catch(error => status(error.message)); });
el('refresh').addEventListener('click', () => loadMessages().catch(error => status(error.message)));
el('new').addEventListener('click', () => prepareCompose('new').catch(error => status(error.message)));
el('cancel-compose').addEventListener('click', () => { el('compose').hidden = true; el('detail').hidden = false; });
el('logout').addEventListener('click', async () => {
  try { await api('/api/logout', { method: 'POST', body: '{}' }); }
  finally { bearerToken = null; el('mail').hidden = true; el('login').hidden = false; }
});
el('compose-form').addEventListener('submit', async event => {
  event.preventDefault();
  const button = el('send'); button.disabled = true;
  const request = {
    accountId: el('from-account').value, kind: composeKind, to: el('to').value,
    cc: el('cc').value, bcc: el('bcc').value, subject: el('subject').value, body: el('body').value,
    sourceAccountId: composeKind === 'new' ? null : selected.accountId,
    sourceFolder: composeKind === 'new' ? null : selected.folder,
    sourceMessageId: composeKind === 'new' ? null : selected.id
  };
  try {
    const result = await api('/api/send', { method: 'POST', body: JSON.stringify(request) });
    el('compose').hidden = true; el('detail').hidden = false;
    status(`Message queued in EQM (${result.queueId}). Check Scheduled or Sent for the final result.`);
  } catch (error) {
    status(`Send was not confirmed. Check Scheduled and Sent in EQM before trying again. ${error.message}`);
  } finally { button.disabled = false; }
});

if (onPages) {
  el('server-label').hidden = false;
  el('server').required = true;
  el('login').hidden = false;
} else {
  api('/api/session').then(data => data.authenticated ? signedIn() : (el('login').hidden = false))
    .catch(() => { el('login').hidden = false; });
}
