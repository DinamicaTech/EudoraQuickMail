'use strict';
const el = id => document.getElementById(id);
const onPages = location.hostname === 'dinamicatech.github.io';
const pageSize = 40;
let accounts = [];
let offset = 0;
let query = '';
let selected = null;
let composeKind = 'new';
let apiBase = '';
let bearerToken = null; // Session credentials stay in memory, never browser storage.
let listRequest = 0;

function status(message) { el('status').textContent = message || ''; }
function showView(view) {
  for (const id of ['listing', 'reader', 'compose']) el(id).hidden = id !== view;
}
function formatDate(value, options) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '' : new Intl.DateTimeFormat('es-ES', options).format(date);
}

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
    throw new Error('La sesión ha caducado. Entra de nuevo.');
  }
  if (!response.ok) throw new Error(await response.text() || `Error de conexión (${response.status}).`);
  return response.status === 204 || !response.headers.get('content-type')?.includes('json')
    ? null : response.json();
}

function params(message) {
  return new URLSearchParams({ accountId: message.accountId, folder: message.folder, id: message.id });
}

async function signedIn() {
  accounts = await api('/api/accounts');
  for (const id of ['account-filter', 'from-account']) {
    const select = el(id);
    select.replaceChildren();
    if (id === 'account-filter') select.add(new Option('Todas las cuentas', ''));
    for (const account of accounts) select.add(new Option(`${account.label} <${account.address}>`, account.id));
  }
  el('login').hidden = true;
  el('mail').hidden = false;
  showView('listing');
  await loadMessages();
}

function messageRow(message) {
  const item = document.createElement('li');
  const button = document.createElement('button');
  button.type = 'button';
  button.className = `message-row${message.unread ? ' unread' : ''}`;
  const dot = document.createElement('span'); dot.className = 'unread-dot'; dot.setAttribute('aria-hidden', 'true');
  const correspondent = document.createElement('span'); correspondent.className = 'correspondent';
  correspondent.textContent = message.outgoing ? `Para: ${message.to || '(sin destinatario)'}` : message.from || '(sin remitente)';
  const summary = document.createElement('span'); summary.className = 'summary';
  const subject = document.createElement('span'); subject.className = 'subject-line';
  subject.textContent = message.subject || '(sin asunto)';
  if (message.hasAttachments) subject.textContent += ' 📎';
  const snippet = document.createElement('span'); snippet.className = 'snippet';
  snippet.textContent = message.preview || '';
  summary.append(subject, snippet);
  const folderCell = document.createElement('span'); folderCell.className = 'folder-cell';
  const folder = document.createElement('span'); folder.className = 'folder-pill';
  folder.textContent = message.folderLabel || message.folder || '';
  folder.title = message.folder || '';
  folderCell.append(folder);
  const date = document.createElement('time'); date.className = 'message-date';
  date.dateTime = message.date; date.textContent = formatDate(message.date, { day: '2-digit', month: '2-digit', year: '2-digit' });
  const time = document.createElement('time'); time.className = 'message-time';
  time.dateTime = message.date; time.textContent = formatDate(message.date, { hour: '2-digit', minute: '2-digit' });
  button.append(dot, correspondent, summary, folderCell, date, time);
  button.addEventListener('click', () => openMessage(message).catch(error => status(error.message)));
  item.append(button);
  return item;
}

async function loadMessages() {
  const request = ++listRequest;
  status(query ? 'Buscando en todas las carpetas…' : 'Cargando mensajes…');
  const params = new URLSearchParams({ offset: String(offset), limit: String(pageSize) });
  if (el('account-filter').value) params.set('accountId', el('account-filter').value);
  if (query) params.set('q', query);
  const data = await api(`/api/messages?${params}`);
  if (request !== listRequest) return;
  el('messages').replaceChildren(...data.messages.map(messageRow));
  el('empty').hidden = data.messages.length !== 0;
  el('newer').disabled = offset === 0;
  el('older').disabled = !data.hasMore;
  const start = data.messages.length ? offset + 1 : 0;
  el('page-info').textContent = `${start.toLocaleString('es-ES')}–${(offset + data.messages.length).toLocaleString('es-ES')} de ${data.total.toLocaleString('es-ES')}`;
  status(query ? `Resultados de «${query}» en todas las carpetas.` : '');
}

async function openMessage(message) {
  status('Cargando mensaje…');
  const data = await api(`/api/message?${params(message)}`);
  selected = message;
  el('reader-subject').textContent = data.subject || '(sin asunto)';
  el('reader-from').textContent = data.from || '(sin remitente)';
  el('reader-to').textContent = `Para: ${data.to || ''}`;
  el('reader-date').textContent = formatDate(data.date, { dateStyle: 'full', timeStyle: 'short' });
  const sender = data.from || '';
  el('reader-avatar').textContent = (sender.trim().match(/[\p{L}\p{N}]/u)?.[0] || '?').toUpperCase();
  el('reader-body').textContent = data.body || '(El cuerpo no está disponible en la caché.)';
  const attachments = el('reader-attachments');
  attachments.replaceChildren();
  attachments.hidden = !data.hasAttachments;
  if (data.hasAttachments) {
    for (const name of data.attachments) {
      const chip = document.createElement('span'); chip.className = 'attachment';
      chip.textContent = `📎 ${name}`;
      attachments.append(chip);
    }
    const note = document.createElement('p'); note.className = 'muted';
    note.textContent = 'Abre EQM de escritorio para acceder a los adjuntos.';
    attachments.append(note);
  }
  showView('reader');
  status('');
  el('back').focus();
}

async function prepareCompose(kind) {
  composeKind = kind;
  el('compose-title').textContent = kind === 'new' ? 'Nuevo mensaje' : kind === 'reply' ? 'Responder' : 'Reenviar';
  el('compose-form').reset();
  el('attachment-warning').hidden = true;
  if (kind === 'new') {
    el('from-account').value = el('account-filter').value || accounts[0]?.id || '';
  } else {
    if (!selected) throw new Error('Selecciona primero un mensaje.');
    const draft = await api(`/api/compose?kind=${kind}&${params(selected)}`);
    el('from-account').value = selected.accountId;
    el('to').value = draft.to;
    el('subject').value = draft.subject;
    el('body').value = draft.body;
    el('attachment-warning').hidden = kind !== 'forward' || !draft.hasAttachments;
  }
  showView('compose');
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
        throw new Error('Introduce la dirección HTTPS privada que muestra EQM.');
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
el('account-filter').addEventListener('change', () => {
  offset = 0; showView('listing'); loadMessages().catch(error => status(error.message));
});
el('search-form').addEventListener('submit', event => {
  event.preventDefault(); query = el('search').value.trim(); offset = 0;
  loadMessages().catch(error => status(error.message));
});
el('search').addEventListener('search', () => {
  if (el('search').value) return;
  query = ''; offset = 0; loadMessages().catch(error => status(error.message));
});
el('density').addEventListener('click', () => {
  const compact = el('messages').classList.toggle('compact');
  el('messages').classList.toggle('comfortable', !compact);
  el('density').classList.toggle('active', compact);
  el('density').setAttribute('aria-pressed', String(compact));
  el('density').textContent = compact ? '≡ Compacta' : '☷ Cómoda';
});
el('newer').addEventListener('click', () => {
  offset = Math.max(0, offset - pageSize); loadMessages().catch(error => status(error.message));
});
el('older').addEventListener('click', () => {
  offset += pageSize; loadMessages().catch(error => status(error.message));
});
el('refresh').addEventListener('click', () => loadMessages().catch(error => status(error.message)));
el('new').addEventListener('click', () => prepareCompose('new').catch(error => status(error.message)));
el('back').addEventListener('click', () => { showView('listing'); el('search').focus(); });
el('reply').addEventListener('click', () => prepareCompose('reply').catch(error => status(error.message)));
el('forward').addEventListener('click', () => prepareCompose('forward').catch(error => status(error.message)));
el('cancel-compose').addEventListener('click', () => showView(selected && composeKind !== 'new' ? 'reader' : 'listing'));
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
    showView('listing');
    status(`Mensaje puesto en cola en EQM (${result.queueId}). Consulta Scheduled o Sent para confirmar el envío.`);
  } catch (error) {
    status(`El envío no se ha confirmado. Consulta Scheduled y Sent en EQM antes de reintentarlo. ${error.message}`);
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
