'use strict';
const el = id => document.getElementById(id);
const onPages = location.hostname === 'dinamicatech.github.io';
const pageSize = 40;
let accounts = [];
let offset = 0;
let query = '';
let view = 'all';
let selected = null;
let composeKind = 'new';
let apiBase = '';
let bearerToken = null; // Session credentials stay in memory, never browser storage.
let listRequest = 0;

function status(message) { el('status').textContent = message || ''; }
function showView(view) {
  // Compose is a dialog over the current view; listing and reader replace each other.
  if (view === 'compose') { el('compose').hidden = false; return; }
  for (const id of ['listing', 'reader']) el(id).hidden = id !== view;
  el('compose').hidden = true;
}
function splitAddress(value) {
  const match = /^\s*"?([^"<]*?)"?\s*<([^<>]+)>\s*$/.exec(value || '');
  return match ? { name: match[1].trim(), email: match[2].trim() } : { name: (value || '').trim(), email: '' };
}
function clipIcon() { return el('clip-icon').content.firstElementChild.cloneNode(true); }
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
  if (!response.ok) {
    const error = new Error(await response.text() || `Error de conexión (${response.status}).`);
    error.status = response.status;
    throw error;
  }
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
  await loadTabs();
  await loadMessages();
}

async function loadTabs() {
  const params = new URLSearchParams();
  if (el('account-filter').value) params.set('accountId', el('account-filter').value);
  let tabs;
  try { tabs = await api(`/api/folder-tabs?${params}`); }
  catch (error) {
    if (error.status !== 404) throw error;
    // Existing desktop builds can use the redesigned list until the new folder-tab API is installed.
    tabs = [{ id: 'all', label: 'Todos', unread: 0 }];
  }
  if (!tabs.some(tab => tab.id === view)) view = 'all';
  const nav = el('folder-tabs');
  nav.replaceChildren();
  for (const tab of tabs) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = `folder-tab${tab.id === view ? ' active' : ''}`;
    button.textContent = tab.label;
    if (tab.unread > 0) {
      const count = document.createElement('span');
      count.className = 'folder-count';
      count.textContent = tab.unread.toLocaleString('es-ES');
      button.append(count);
    }
    if (tab.id === view) button.setAttribute('aria-current', 'page');
    button.addEventListener('click', () => {
      if (view === tab.id) return;
      view = tab.id; offset = 0;
      for (const item of nav.children) {
        item.classList.toggle('active', item === button);
        if (item === button) item.setAttribute('aria-current', 'page');
        else item.removeAttribute('aria-current');
      }
      el('view-title').textContent = tab.label;
      showView('listing');
      loadMessages().catch(error => status(error.message));
    });
    nav.append(button);
  }
  el('view-title').textContent = tabs.find(tab => tab.id === view)?.label || 'Todos';
}

function messageRow(message) {
  const item = document.createElement('li');
  const button = document.createElement('button');
  button.type = 'button';
  button.className = `message-row${message.unread ? ' unread' : ''}`;
  const dot = document.createElement('span'); dot.className = 'unread-dot'; dot.setAttribute('aria-hidden', 'true');
  const correspondent = document.createElement('span'); correspondent.className = 'correspondent';
  const sender = splitAddress(message.from);
  correspondent.textContent = message.outgoing ? `Para: ${message.to || '(sin destinatario)'}`
    : sender.name && sender.email ? `${sender.name} (${sender.email})` : sender.name || sender.email || '(sin remitente)';
  const summary = document.createElement('span'); summary.className = 'summary';
  const subject = document.createElement('span'); subject.className = 'subject-line';
  subject.textContent = message.subject || '(sin asunto)';
  if (message.hasAttachments) subject.append(clipIcon());
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
  button.addEventListener('click', () => openMessage(message)
    .then(() => button.classList.remove('unread'))
    .catch(error => status(error.message)));
  item.append(button);
  return item;
}

async function loadMessages() {
  const request = ++listRequest;
  status(query ? 'Buscando en todas las carpetas…' : 'Cargando mensajes…');
  const params = new URLSearchParams({ offset: String(offset), limit: String(pageSize) });
  if (el('account-filter').value) params.set('accountId', el('account-filter').value);
  if (query) params.set('q', query);
  if (view !== 'all') params.set('view', view);
  const data = await api(`/api/messages?${params}`);
  if (request !== listRequest) return;
  el('messages').replaceChildren(...data.messages.map(messageRow));
  el('empty').hidden = data.messages.length !== 0;
  el('newer').disabled = offset === 0;
  el('older').disabled = !data.hasMore;
  el('pager').hidden = data.total <= pageSize;
  const start = data.messages.length ? offset + 1 : 0;
  el('page-info').textContent = `${start.toLocaleString('es-ES')}–${(offset + data.messages.length).toLocaleString('es-ES')} de ${data.total.toLocaleString('es-ES')}`;
  status(query ? `Resultados de «${query}» en ${view === 'all' ? 'todas las carpetas' : el('view-title').textContent}.` : '');
}

async function openMessage(message) {
  status('Cargando mensaje…');
  const data = await api(`/api/message?${params(message)}`);
  message.unread = false;
  selected = message;
  el('reader-subject').textContent = data.subject || '(sin asunto)';
  const from = splitAddress(data.from);
  el('reader-from').textContent = from.name || from.email || '(sin remitente)';
  el('reader-email').textContent = from.name && from.email ? `<${from.email}>` : '';
  el('reader-to').textContent = `Para: ${data.to || ''}`;
  el('reader-date').textContent = formatDate(data.date, { dateStyle: 'full', timeStyle: 'short' });
  const sender = (data.from || '').split('<')[0].replace(/["']/g, '').trim();
  const words = sender.split(/\s+/).filter(Boolean);
  el('reader-avatar').textContent = (words.length > 1
    ? words[0][0] + words[1][0] : words[0]?.slice(0, 2) || '?').toUpperCase();
  el('reader-body').textContent = data.body || '(El cuerpo no está disponible en la caché.)';
  const attachments = el('reader-attachments');
  attachments.replaceChildren();
  attachments.hidden = !data.hasAttachments;
  if (data.hasAttachments) {
    for (const [index, name] of data.attachments.entries()) {
      const chip = document.createElement('button'); chip.type = 'button'; chip.className = 'attachment';
      const label = document.createElement('span'); label.textContent = name;
      chip.append(clipIcon(), label);
      chip.title = `Abrir ${name}`;
      chip.addEventListener('click', () => openAttachment(message, index, name));
      attachments.append(chip);
    }
  }
  showView('reader');
  status('');
  el('back').focus();
}

async function openAttachment(message, index, name) {
  const extension = name.split('.').pop()?.toLowerCase();
  const previewTypes = { pdf: 'application/pdf', png: 'image/png', jpg: 'image/jpeg',
    jpeg: 'image/jpeg', gif: 'image/gif', webp: 'image/webp', txt: 'text/plain' };
  const previewType = previewTypes[extension];
  // Open synchronously from the click so browser popup protection allows the preview.
  const preview = previewType ? window.open('', '_blank') : null;
  if (preview) {
    preview.opener = null;
    preview.document.title = 'Cargando adjunto…';
    preview.document.body.textContent = 'Cargando adjunto…';
  }
  status(`Abriendo ${name}…`);
  try {
    const response = await fetch(`${apiBase}/api/attachment?${params(message)}&index=${index}`, {
      credentials: 'same-origin', cache: 'no-store',
      headers: { 'X-QuickMail-Request': '1',
        ...(bearerToken ? { Authorization: `Bearer ${bearerToken}` } : {}) }
    });
    if (!response.ok) throw new Error(response.status === 404
      ? 'El adjunto ya no está disponible.' : `No se ha podido abrir el adjunto (${response.status}).`);
    const downloaded = await response.blob();
    const blob = previewType ? new Blob([downloaded], { type: previewType }) : downloaded;
    const url = URL.createObjectURL(blob);
    if (preview) preview.location.replace(url);
    else {
      const link = document.createElement('a');
      link.href = url; link.download = name || 'adjunto';
      document.body.append(link); link.click(); link.remove();
    }
    setTimeout(() => URL.revokeObjectURL(url), 300000);
    status('');
  } catch (error) {
    if (preview) preview.close();
    status(error.message);
  }
}

async function prepareCompose(kind) {
  composeKind = kind;
  el('compose-title').textContent = kind === 'new' ? 'Nuevo mensaje' : kind === 'reply' ? 'Responder' : 'Reenviar';
  el('compose-form').reset();
  el('attachment-warning').hidden = true;
  el('compose-error').hidden = true;
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
  view = 'all'; offset = 0; showView('listing');
  loadTabs().then(loadMessages).catch(error => status(error.message));
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
  el('density').setAttribute('aria-pressed', String(compact));
  el('density-label').textContent = compact ? 'Compacta' : 'Cómoda';
});
el('newer').addEventListener('click', () => {
  offset = Math.max(0, offset - pageSize); loadMessages().catch(error => status(error.message));
});
el('older').addEventListener('click', () => {
  offset += pageSize; loadMessages().catch(error => status(error.message));
});
el('refresh').addEventListener('click', () => {
  el('more').open = false;
  loadTabs().then(loadMessages).catch(error => status(error.message));
});
el('new').addEventListener('click', () => prepareCompose('new').catch(error => status(error.message)));
el('back').addEventListener('click', () => {
  showView('listing'); el('search').focus();
  loadTabs().then(loadMessages).catch(error => status(error.message));
});
el('reply').addEventListener('click', () => prepareCompose('reply').catch(error => status(error.message)));
el('forward').addEventListener('click', () => prepareCompose('forward').catch(error => status(error.message)));
for (const id of ['cancel-compose', 'close-compose'])
  el(id).addEventListener('click', () => { el('compose').hidden = true; });
el('compose').addEventListener('keydown', event => {
  if (event.key === 'Escape') el('compose').hidden = true;
});
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
    const message = `El envío no se ha confirmado. Consulta Scheduled y Sent en EQM antes de reintentarlo. ${error.message}`;
    status(message);
    el('compose-error').textContent = message;
    el('compose-error').hidden = false;
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
