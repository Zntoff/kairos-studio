/* Clipper: panel. Vistas de videos, trabajos, clips y cola. El editor vive en editor.js. */
const $ = (s, e = document) => e.querySelector(s);
const $$ = (s, e = document) => [...e.querySelectorAll(s)];
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
let sessionToken = document.querySelector('meta[name="kairos-csrf"]')?.content || '';
async function api(url, opt) {
  url = projectUrl(url, opt);
  const options={...opt,headers:{...opt?.headers,'X-Kairos-CSRF':sessionToken,'X-Kairos-Language':I18n.locale}};
  delete options.allProjects;
  let r = await fetch(url, options);
  if(r.status===401){
    const bootstrap=await fetch('/',{cache:'no-store'});
    const html=new DOMParser().parseFromString(await bootstrap.text(),'text/html');
    sessionToken=html.querySelector('meta[name="kairos-csrf"]')?.content||'';
    options.headers['X-Kairos-CSRF']=sessionToken;r=await fetch(url,options);
  }
  let d = null; try { d = await r.json() } catch { }
  if (!r.ok) throw new Error((d && (typeof d.detail == 'string' ? d.detail : JSON.stringify(d.detail))) || ('Error ' + r.status)); return d;
}
const send = (m, u, b) => api(u, { method: m, headers: { 'Content-Type': 'application/json' }, body: b === undefined ? undefined : JSON.stringify(b) });
const post = (u, b) => send('POST', u, b);
const P = {
  film: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M7 4v16M17 4v16M3 9h4M3 15h4M17 9h4M17 15h4"/>',
  pulse: '<path d="M3 12h4l3-8 4 16 3-8h4"/>',
  grid: '<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>',
  inbox: '<path d="M3 13l3-8h12l3 8v5a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z"/><path d="M3 13h5l1 3h6l1-3h5"/>',
  play: '<path d="M7 4l13 8-13 8z" fill="currentColor"/>', pause: '<path d="M7 4v16M17 4v16" stroke-width="3"/>',
  cut: '<circle cx="6" cy="6" r="3"/><circle cx="6" cy="18" r="3"/><path d="M8.5 8l11.5 10M8.5 16L20 6"/>',
  x: '<path d="M6 6l12 12M18 6L6 18"/>', plus: '<path d="M12 5v14M5 12h14"/>', trash: '<path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13"/>',
  back: '<path d="M15 5l-7 7 7 7"/>', undo: '<path d="M8 5L3 10l5 5"/><path d="M3 10h11a6 6 0 0 1 0 12h-3"/>', redo: '<path d="M16 5l5 5-5 5"/><path d="M21 10H10a6 6 0 0 0 0 12h3"/>',
  dl: '<path d="M12 4v12M7 11l5 5 5-5M4 20h16"/>', cc: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M10 10.5a2 2 0 1 0 0 3M17 10.5a2 2 0 1 0 0 3"/>',
  text: '<path d="M5 6V4h14v2M12 4v16M9 20h6"/>', music: '<path d="M9 18V5l11-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="17" cy="16" r="3"/>',
  spark: '<path d="M12 3l2.2 6.8L21 12l-6.8 2.2L12 21l-2.2-6.8L3 12l6.8-2.2z"/>', frame: '<rect x="7" y="3" width="10" height="18" rx="2"/>',
  vol: '<path d="M4 9v6h4l5 4V5L8 9z"/><path d="M16 9a4 4 0 0 1 0 6"/>', mute: '<path d="M4 9v6h4l5 4V5L8 9z"/><path d="M16 9l5 6M21 9l-5 6"/>',
  first: '<path d="M6 5v14M19 5l-9 7 9 7z"/>', last: '<path d="M18 5v14M5 5l9 7-9 7z"/>', split: '<path d="M12 3v18M7 8l-4 4 4 4M17 8l4 4-4 4"/>',
  magnet: '<path d="M6 3v9a6 6 0 0 0 12 0V3h-4v9a2 2 0 0 1-4 0V3zM6 7h4M14 7h4"/>', fit: '<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/>',
  eye: '<path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>', check: '<path d="M4 12l5 5L20 6"/>',
  zoomin: '<circle cx="11" cy="11" r="7"/><path d="M21 21l-5-5M8 11h6M11 8v6"/>', render: '<path d="M4 16v4h16v-4M12 4v12M7 9l5-5 5 5"/>',
  gear: '<circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1"/>',
  link: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>',
};
const icon = n => `<svg class="i" viewBox="0 0 24 24">${P[n] || ''}</svg>`;
const rightsLabel = id => ({own:'Contenido propio',written_permission:'Permiso por escrito',official_press:'Material de prensa oficial',cc_licensed:'Licencia Creative Commons'}[id]||id);
const reviewLabel = id => ({rights:'Derechos',resolution:'Resoluci\u00f3n',duration:'Duraci\u00f3n',loudness:'Normalizaci\u00f3n de audio',caption_timing:'Sincronizaci\u00f3n de subt\u00edtulos',safe_zone:'Zona segura',spoiler_keywords:'T\u00e9rminos de spoiler',value_add:'Comentario original',variety:'Variedad',claude_review:'Revisi\u00f3n con IA',platform_safe_area:'Zona segura de plataformas'}[id]||id);
const STATUS = {
  pending_approval: ['Lista para aprobar', 'ac'], needs_human: ['Revisar', 'wa'], approved: ['Aprobado', 'ok'], rejected: ['Rechazado por la IA', 'er'],
  rejected_by_human: ['Rechazado', 'er'], agent_failed: ['Falló un subagente', 'er'], error: ['Error', 'er'], candidate: ['En proceso', ''], edited: ['En proceso', ''],
  rendered: ['En proceso', ''], done: ['Terminado', 'ok'], running: ['En curso', 'ac'], awaiting_selection: ['Elige los momentos', 'wa'], failed: ['Falló', 'er'], interrupted: ['Interrumpido', 'wa'], queued: ['En cola', '']
};
const EDITABLE = ['pending_approval', 'needs_human', 'approved', 'rejected', 'rejected_by_human'];
const canEdit = c => c.edl && (EDITABLE.includes(c.status) || cfg.desktop && c.status == 'error');
const pill = s => { const [t, c] = STATUS[s] || [s, '']; return `<span class="pill ${c}">${esc(t)}</span>` };
const fmtDur = s => { s = Math.max(0, Math.round(s)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}` };
function toast(text, kind = '') {
  const el = document.createElement('div'); el.className = 'toast ' + kind; el.textContent = text; $('#toasts').append(el);
  setTimeout(() => el.remove(), kind == 'er' ? 7000 : 3200);
}
let tab = 'dashboard', cfg = {}, who = '', timer = null, clipFilter = 'all', addMode = 'url', viewEpoch = 0;
try { who = localStorage.who || '' } catch { }

const TABS = [['dashboard', 'Inicio', 'home'], ['projects', 'Proyectos', 'grid'], ['videos', 'Videos', 'film'], ['video-projects', 'Montajes de video', 'cut'], ['resources', 'Recursos', 'folder'], ['jobs', 'Trabajos', 'pulse'], ['clips', 'Clips', 'grid'], ['queue', 'Cola de aprobación', 'inbox'], ['apis', 'APIs y modelos', 'spark'], ['settings', 'Ajustes', 'gear']];
function nav(counts = {}) {
  const button = ([k,l,i]) => `<button class="${tab == k ? 'on' : ''}" onclick="go('${k}')" title="${l}" aria-label="${l}" ${tab == k ? 'aria-current="page"' : ''}>${icon(i)}<span class="l">${l}</span>${k == 'queue' && counts.q ? `<span class="badge">${counts.q}</span>` : ''}</button>`;
  $('#nav').innerHTML = `<section class="nav-section"><div class="nav-group-label">Espacio de trabajo</div>${TABS.slice(0,2).map(button).join('')}</section><section class="nav-section"><div class="nav-group-label">Contenido del proyecto</div>${TABS.slice(2,-2).filter(([k])=>cfg.desktop || k!=='video-projects').map(button).join('')}</section>`;
  $('#utilityNav').innerHTML = TABS.slice(-2).map(button).join('') + `<button onclick="quickProvider()" title="Cambiar proveedor de IA" aria-label="Cambiar proveedor de IA">${icon('spark')}<span class="l">Cambiar IA</span></button>`;
  if (matchMedia('(min-width: 768px)').matches) $('#nav button.on')?.scrollIntoView({block:'nearest'});
}
function go(k) { Shell.close(); if (location.hash === '#/' + k) route(); else location.hash = '#/' + k }
function route() {
  viewEpoch++;
  Shell.close();
  closeModal();
  VideoEditor.close();
  const [view, arg] = location.hash.replace(/^#\/?/, '').split('/');
  if (view == 'video-editor' && arg && cfg.desktop) { Editor.close();tab='video-editor';clearInterval(timer);VideoEditor.open(arg);return; }
  if (view == 'editor' && arg) { tab = 'editor'; clearInterval(timer); Editor.open(arg); return }
  if (view == 'select' && arg) { Editor.close(); tab = 'jobs'; clearInterval(timer); nav(); loadingView(); vSelect(arg).catch(showViewError); return }
  Editor.close();
  tab = TABS.some(t => t[0] == view) ? view : 'dashboard';
  nav();
  loadingView();
  render().catch(showViewError);
}
window.addEventListener('hashchange', route);
window.addEventListener('kairos:language',()=>{if(cfg.desktop)send('PUT','/api/settings/ui-language',{language:I18n.locale}).catch(()=>{});});
async function render() {
  const epoch = ++viewEpoch;
  clearInterval(timer);
  const requestedProject = currentProject, requestedTab = tab;
  const q = await api('/api/queue').catch(() => []);
  if (epoch !== viewEpoch || currentProject !== requestedProject || tab !== requestedTab) return;
  nav({ q: q.length });
  try { await ({ dashboard: vDashboard, projects: vProjects, resources: vResources, videos: vVideos, 'video-projects': vVideoProjects, jobs: vJobs, clips: vClips, queue: vQueue, apis: vApis, settings: vSettings })[tab](q); }
  catch (error) { if (epoch === viewEpoch) throw error; }
  finally { if (epoch === viewEpoch) $('#main').removeAttribute('aria-busy'); }
}
function loadingView() { $('#main').setAttribute('aria-busy','true'); $('#main').innerHTML = '<div class="page view-loading" role="status"><span class="spin"></span> Cargando el proyecto…</div>'; }
function showViewError(error) { $('#main').removeAttribute('aria-busy'); $('#main').innerHTML = `<div class="page">${head('No se pudo cargar esta vista', 'Tu contenido se conserva.')}<div class="msg" role="alert">${esc(error.message)}</div><button class="b" onclick="render().catch(showViewError)">Reintentar</button></div>`; }
const head = (t, sub, right = '') => `<div class="page-head"><div>${['dashboard','projects','settings','apis'].includes(tab) ? '' : `<div class="project-context" data-ui-content>${esc(projectName())}</div>`}<h1>${t}</h1><p>${sub}</p></div><span class="sp"></span>${right}</div>`;
const empty = (i, t) => `<div class="empty">${icon(i)}<div>${t}</div></div>`;

/* ---------- Videos ---------- */
async function vVideos() {
  const viewProject = currentProject, viewTab = tab, epoch = viewEpoch;
  const [src, inputs] = await Promise.all([api('/api/sources'), api('/api/inputs')]);
  const opts = cfg.rights.map(r => `<option value="${esc(r)}">${esc(rightsLabel(r))}</option>`).join('');
  if (addMode == 'input' && !inputs.length) addMode = 'url';
  const modes = [['url', 'Enlace'], ['file', 'Archivo'], ...(inputs.length ? [['input', 'Carpeta de entrada']] : [])];
  if (epoch !== viewEpoch || currentProject !== viewProject || tab !== viewTab) return;
  $('#main').innerHTML = `<div class="page">
  ${head('Videos', 'Registra un video largo; de ahí salen los clips.')}
  <div class="card"><h2>Añadir un video</h2>
   <div class="sub">Cada video entra con su estado de derechos y la evidencia. Sin eso no se procesa.</div>
   <form id="up">
    <div class="seg addtabs">${modes.map(([k, l]) => `<button type="button" class="${addMode == k ? 'on' : ''}" data-mode="${k}">${l}</button>`).join('')}</div>
    <div>
     ${addMode == 'url' ? `<label>Enlace de YouTube, Twitch o Kick</label><input name="url" id="url" placeholder="https://www.twitch.tv/videos/123456789" autocomplete="off">` : ''}
     ${addMode == 'file' ? `<label>Archivo de video</label><input type="file" name="file" accept="video/*" id="file">` : ''}
     ${addMode == 'input' ? `<label>Archivo de la carpeta de entrada</label><select id="inp">${inputs.map(i => `<option value="${esc(i)}" translate="no">${esc(i)}</option>`).join('')}</select>` : ''}
    </div>
    <div class="row">
     <div><label>Derechos</label><select name="rights">${opts}</select></div>
     <div><label>Evidencia (enlace al permiso o descripción)</label><input name="evidence" placeholder="grabación propia, PS5, 2026-11-19" required minlength="8"></div>
     <div><label>Idioma hablado</label><select name="language"><option value="">Detectar</option><option value="en">Inglés</option><option value="es">Español</option></select></div>
     <div><label>Crédito (opcional)</label><input name="credit" placeholder="@creador"></div>
    </div>
    <div style="margin-top:16px;display:flex;align-items:center;gap:12px"><button class="b" id="upb">${icon('plus')}Registrar video</button><span id="upm" class="mut sm"></span></div>
   </form></div>
  <h2 style="margin:26px 0 12px;font-size:16px">Videos registrados</h2>
  ${src.length ? `<div class="srcs">${src.map(r => `<div class="src">
     <div class="th" style="background-image:url('/api/sources/${r.id}/frame?t=${(r.duration * .2).toFixed(1)}&h=240')"><span class="dur">${fmtDur(r.duration)}</span></div>
     <div class="bd"><div class="nm" title="${esc(r.path)}">${esc(r.path.split(/[\\/]/).pop())}</div>
      <div class="mut sm" ${r.credit?'data-ui-content':''}><span>${r.width}×${r.height}</span> · <span>${esc(r.credit || 'sin crédito')}</span></div>
      <div class="mut sm" style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap" data-ui-content title="${esc(r.evidence)}">${esc(r.evidence)}</div>
      <div class="ft"><span class="pill">${esc(rightsLabel(r.rights_status))}</span><button class="b g sm" onclick="moveSource('${r.id}')" title="Mover a otro proyecto">Mover</button><button class="b g er sm" onclick="deleteLibrary('sources','${r.id}')" aria-label="Delete video">${icon('trash')}</button><span style="flex:1"></span>${cfg.desktop ? `<button class="b ${cfg.ai === false ? '' : 'g'} sm" onclick="newClip('${r.id}',${r.duration})">${icon('cut')}Crear clip</button>` : ''}${cfg.ai === false ? '' : `<button class="b sm" onclick="proc('${r.id}')">${icon('spark')}Procesar</button>`}</div></div></div>`).join('')}</div>`
      : empty('film', 'Aún no hay videos. Pega un enlace o sube un archivo.')}
  </div>`;
  $$('[data-mode]').forEach(b => b.onclick = () => { addMode = b.dataset.mode; vVideos() });
  $('#up').onsubmit = async e => {
    e.preventDefault(); const f = new FormData(e.target), mode = addMode, button = $('#upb'), message = $('#upm');
    const active = () => epoch === viewEpoch && viewProject === currentProject && tab === 'videos';
    f.set('project_id', viewProject);
    button.disabled = true; message.textContent = { url: 'Iniciando descarga…', file: 'Subiendo…', input: 'Registrando…' }[mode];
    try {
      if (mode == 'url') { if (!f.get('url').trim()) throw new Error('Pega un enlace.'); await api('/api/sources/from-url', { method: 'POST', body: f }); toast('Descarga iniciada', 'ok'); if (active()) go('jobs'); return }
      if (mode == 'file') { if (!$('#file').files[0]) throw new Error('Elige un archivo.'); await api('/api/sources/upload', { method: 'POST', body: f }) }
      else { f.set('rel', $('#inp').value); await api('/api/sources/from-input', { method: 'POST', body: f }) }
      toast('Video registrado en su proyecto', 'ok'); if (active()) await vVideos();
    } catch (x) { if (message.isConnected) { message.innerHTML = `<span style="color:var(--er)">${esc(x.message)}</span>`; button.disabled = false; } }
  };
}
const store = { get(k, d) { try { return JSON.parse(localStorage[k]) ?? d } catch { return d } }, set(k, v) { try { localStorage[k] = JSON.stringify(v) } catch { } } };
const tierCards = (name, sel) => cfg.tiers.map(t => `<label class="tier ${t.id == sel ? 'on' : ''}"><input type="radio" name="${name}" value="${t.id}" ${t.id == sel ? 'checked' : ''}>
  <b>${esc(t.name)}</b><span>${esc(t.note)}</span><small>${esc(t.model || 'sin modelo')}${t.price.startsWith('$') ? ' · ' + t.price + ' por millón de tokens' : ''}</small></label>`).join('');
function bindTiers(root) { $$('.tier input', root).forEach(i => i.onchange = () => $$('.tier', root).forEach(l => l.classList.toggle('on', l.querySelector('input').checked))) }
function newClip(id, total) {
  const mmss = t => `${Math.floor(t / 60)}:${String(Math.floor(t % 60)).padStart(2, '0')}`;
  modal(`<h2 style="font-size:18px">Crear un clip</h2><p class="mut sm">Elige de dónde sale. En el editor puedes mover el inicio y el final, y añadir textos, efectos, color y sonido.</p>
   <video id="ncV" controls preload="metadata" src="/api/sources/${id}/video" style="width:100%;max-height:46vh;background:#000;border-radius:10px"></video>
   <div class="row" style="margin-top:10px"><div><label>Empieza en (min:seg)</label><input id="ncS" value="0:00"></div><div><label>Duración (segundos)</label><input id="ncD" type="number" min="3" max="180" value="${Math.min(45, Math.floor(total))}"></div>
   <div><label>&nbsp;</label><button class="b g" id="ncNow" type="button" style="width:100%">Usar el momento del reproductor</button></div></div>
   <div style="margin-top:14px;display:flex;gap:10px;align-items:center"><button class="b" id="ncGo">${icon('cut')}Abrir en el editor</button><span id="ncM" class="mut sm"></span></div>`);
  $('#ncNow').onclick = () => { $('#ncS').value = mmss($('#ncV').currentTime) };
  $('#ncGo').onclick = async () => {
    const parts = $('#ncS').value.trim().split(':').map(Number);
    if (parts.some(isNaN)) { $('#ncM').innerHTML = '<span style="color:var(--er)">Escribe el inicio como min:seg, por ejemplo 12:30.</span>'; return }
    const start = parts.reduce((a, v) => a * 60 + v, 0);
    try { const r = await post(`/api/sources/${id}/clip`, { start, duration: +$('#ncD').value || 45 }); closeModal(); location.hash = '#/editor/' + r.id }
    catch (x) { $('#ncM').innerHTML = `<span style="color:var(--er)">${esc(x.message)}</span>` }
  };
}
async function proc(id) {
  const off = cfg.llm == 'offline' || !cfg.api_key_set;
  if (cfg.desktop && off) { toast(cfg.provider?.missing || 'Configura tu proveedor de IA en Ajustes.', 'er'); go('settings'); return; }
  if (off && !I18n.confirm([(cfg.llm == 'offline' ? 'El panel está en modo sin IA.' : (cfg.provider?.missing || 'Falta configurar el proveedor de IA.')), '¿Continuar con heurísticas de prueba?'])) return;
  try { await post('/api/jobs', { source_id: id, offline: off, analysis_model: 'fast', review: true }); toast(`Buscando momentos con ${cfg.tiers[0].name}…`, 'ok'); go('jobs') } catch (x) { toast(x.message, 'er') }
}
/* ---------- Trabajos ---------- */
async function vJobs() { return JobsView.open(); }
async function resume(id) { const epoch = viewEpoch; try { await api(`/api/jobs/${id}/resume`, { method: 'POST' }); if (epoch === viewEpoch && tab === 'jobs') await vJobs(); } catch (x) { toast(x.message, 'er') } }
async function job(id) {
  const epoch = viewEpoch;
  const d = await api('/api/jobs/' + id);
  if (epoch !== viewEpoch) return;
  const tok = d.steps.reduce((a, s) => ({ i: a.i + ((s.usage || {}).input_tokens || 0), o: a.o + ((s.usage || {}).output_tokens || 0) }), { i: 0, o: 0 });
  modal(`<h2 style="font-size:18px">${d.job.id} ${pill(d.job.status)}</h2>
   ${d.job.summary && d.job.summary.error ? `<div class="msg">${esc(d.job.summary.error)}</div>` : ''}
   <p class="mut sm">Tokens: ${tok.i.toLocaleString()} entrada · ${tok.o.toLocaleString()} salida</p>
   <div class="grid">${d.clips.map(clipCard).join('') || '<div class="mut">Sin clips.</div>'}</div>
   <div class="sect"><h3>Pasos de los subagentes</h3>
   <table class="sm"><tr><th>Agente</th><th>Clip</th><th>Estado</th><th>Intentos</th><th>Modelo</th><th>Segundos</th></tr>${d.steps.map(s => `<tr><td>${s.agent}</td><td>${s.clip_id || '—'}</td><td>${s.status}</td><td>${s.attempts}</td><td>${esc(s.model || '')}</td><td>${(s.seconds || 0).toFixed(1)}</td></tr>`).join('')}</table></div>
   ${d.run ? `<pre class="log">${esc(d.run.log.join('\n'))}</pre>` : ''}`);
}


/* ---------- Elegir momentos, estilos y modelo ---------- */
const LAYOUTS = [['auto', 'Que decida la IA', 'auto'], ['gameplay_blur_bg', 'Completo + fondo', 'blur'], ['center_crop', 'Recorte vertical', 'crop']];
const CAPS = [['auto', 'Que decida la IA', null], ['bold', 'Negrita', 'HOLA<br><b>MUNDO</b>'], ['bounce', 'Rebote', 'HOLA<br><b>MUNDO</b>'], ['clean', 'Limpio', 'Hola<br><b>mundo</b>'],
  ['impact', 'Impacto', 'HOLA<br><b>MUNDO</b>'], ['comic', 'Cómic', 'HOLA<br><b>MUNDO</b>'], ['neon', 'Neón', 'HOLA<br><b>MUNDO</b>'], ['pixel', 'Pixel', 'HOLA<br><b>MUNDO</b>'], ['soft', 'Suave', 'Hola<br><b>mundo</b>']];
async function vSelect(id) {
  const viewProject = currentProject, viewTab = tab, epoch = viewEpoch;
  let d;
  try { d = await api(`/api/jobs/${id}/candidates`) }
  catch (x) {
    const j = await api('/api/jobs/' + id).catch(() => null);
    if (epoch !== viewEpoch || currentProject !== viewProject || tab !== viewTab) return;
  $('#main').innerHTML = `<div class="page">${head('Elegir momentos', '')}<div class="card">${j && j.job.status == 'running' ? '<span class="spin"></span> El análisis sigue en curso. Mira el avance en <a href="#/jobs">Trabajos</a>.' : `<div class="msg">${esc(x.message)}</div>`}</div></div>`;
    return;
  }
  const src = d.source, g = Object.assign({ model: 'standard', layout: 'auto', caption_style: 'auto' }, store.get('gen', {}));
  const LN = { en: 'Inglés', es: 'Español', pt: 'Portugués', fr: 'Francés', de: 'Alemán', it: 'Italiano', ja: 'Japonés', ko: 'Coreano' }, ln = l => LN[l] || l.toUpperCase();
  const det = d.spoken.detected, decl = d.spoken.declared, spoken = det || decl;
  const langs = [...new Set([...(spoken ? [spoken] : []), 'en', 'es', ...(d.languages || [])])];
  // Suggestion: publish in the spoken language and in the other of English / Spanish (the channel's two audiences).
  const suggested = spoken ? [...new Set([spoken, spoken == 'es' ? 'en' : spoken == 'en' ? 'es' : null].filter(Boolean))] : (d.languages || ['en']);
  const chosenLangs = new Set(suggested);
  const picked = new Set(), cands = d.candidates;
  const stamp = t => fmtDur(t);
  if (epoch !== viewEpoch || currentProject !== viewProject || tab !== viewTab) return;
  $('#main').innerHTML = `<div class="page">
   ${head('Elige los momentos', `${esc((src.path || '').split(/[\\/]/).pop() || d.job.source_id)} · ${cands.length} momentos sugeridos · ${cands.some(c => c.clip_id) ? 'Los ya generados no se repiten.' : 'Todavía no se ha generado nada.'}`,
    `<a class="b g sm" href="#/jobs">${icon('back')}Trabajos</a>`)}
   <div class="selgrid">
    <div>
     <div class="selbar"><b id="selCount"></b><span class="sp"></span>
      <button class="b g sm" id="selTop">Marcar los 3 mejores</button><button class="b g sm" id="selAll">Todos</button><button class="b g sm" id="selNone">Ninguno</button>
      <div class="seg"><button class="on" data-sort="score">Por puntaje</button><button data-sort="time">Por tiempo</button></div></div>
     <div id="cands"></div>
    </div>
    <aside class="selside card">
     <h2>Cómo generarlos</h2>
     <label>Encuadre</label><div class="opt c3" id="optLay">${LAYOUTS.map(([k, l, c]) => `<button data-v="${k}" class="${g.layout == k ? 'on' : ''}"><span class="lay ${c}"></span>${l}</button>`).join('')}</div>
     <label>Estilo de subtítulos</label><div class="opt c4" id="optCap">${CAPS.map(([k, l, p]) => `<button data-v="${k}" class="${g.caption_style == k ? 'on' : ''}"><span class="pv ${k}">${p || '✦'}</span>${l}</button>`).join('')}</div>
     <label>Idiomas de salida</label>
     <div class="msg info" style="margin:0 0 8px">${spoken ? `Idioma hablado${det ? ' detectado' : ''}: <b>${ln(spoken)}</b>.${decl && det && decl != det ? ` <b>Ojo:</b> al registrar el video indicaste ${ln(decl)}; se usa el detectado.` : ''}
      Sugerencia: <b>${suggested.map(l=>`<span>${ln(l)}</span>`).join(' + ')}</b>${suggested.length > 1 ? ` (<span>${ln(spoken)}</span> <span>original y traducción</span>)` : ''}.` : 'No se pudo detectar el idioma hablado; elige los de salida.'}</div>
     <div class="chips" id="optLang">${langs.map(l => `<button type="button" class="chip ${chosenLangs.has(l) ? 'on' : ''}" data-l="${l}"><span>${ln(l)}</span>${l == spoken ? ' · original' : ''}</button>`).join('')}</div>
     <label>Modelo de ${esc(cfg.provider?.name || 'IA')}</label><div class="tiers" id="optModel">${tierCards('gm', g.model)}</div>
     <p class="hint mut sm">Se usa para la edición, los subtítulos y los títulos${cfg.claude_review === false ? '. Cada clip queda pendiente de revisión humana' : ' y la revisión de cada clip'}. ${esc(cfg.tiers[0].name)} ya encontró los momentos.</p>
     ${!cfg.api_key_set && cfg.llm != 'offline' ? `<div class="msg wa">${esc(cfg.provider?.missing || 'Falta configurar la IA')} Mientras tanto, solo puedes generar en modo de prueba.</div>` : ''}
     <button class="b" id="goGen" style="width:100%;margin-top:14px"></button><div id="genm"></div>
    </aside>
   </div></div>`;
  bindTiers($('#optModel'));
  let sort = 'score';
  const drawCands = () => {
    const list = cands.map((c, i) => ({ ...c, i })).sort((a, b) => sort == 'time' ? a.start - b.start : b.score - a.score);
    $('#cands').innerHTML = list.map(c => {
      const done = !!c.clip_id, mid = ((c.start + c.end) / 2).toFixed(1);
      return `<div class="cand ${picked.has(c.id) ? 'on' : ''} ${done ? 'done' : ''}" data-id="${c.id}">
      <label class="pk"><input type="checkbox" ${picked.has(c.id) ? 'checked' : ''} ${done ? 'disabled' : ''}></label>
      <div class="pv16" data-play="${c.id}"><img loading="lazy" src="/api/sources/${src.id}/frame?t=${mid}&h=240"><span class="pl">${icon('play')}</span><span class="du">${stamp(c.start)}–${stamp(c.end)} · ${c.duration.toFixed(0)} s</span></div>
      <div class="cb"><div class="ch"><span class="pill ${c.score >= 80 ? 'ok' : c.score >= 60 ? 'ac' : ''}">${c.score.toFixed(0)}</span><span class="pill" data-ui-content>${esc(c.category)}</span><span class="mut sm" data-ui-content>${c.id}</span>
        ${done ? `<span class="pill ${(STATUS[c.clip_status] || ['', ''])[1]}"><span>Ya generado</span> · <span>${esc((STATUS[c.clip_status] || [c.clip_status])[0])}</span></span><a class="b g sm" href="#/editor/${c.clip_id}" onclick="event.stopPropagation()">Abrir clip</a>` : ''}</div>
       <div class="why" data-ui-content>${esc(c.reason)}</div>
       ${c.hook_idea ? `<div class="hk"><span class="mut sm">Idea de gancho:</span> <span data-ui-content>${esc(c.hook_idea)}</span></div>` : ''}
       <div class="tx" data-ui-content>${esc(c.text.length > 260 ? c.text.slice(0, 260) + '…' : c.text) || '<span class="mut">(sin voz)</span>'}</div></div></div>`;
    }).join('');
  };
  const refresh = () => {
    const n = picked.size, doneN = cands.filter(c => c.clip_id).length;
    $('#selCount').textContent = `${n} de ${cands.length} seleccionados${doneN ? ` · ${doneN} ya generados` : ''}`;
    $('#goGen').innerHTML = n ? `${icon('spark')}Generar ${n} clip${n == 1 ? '' : 's'}` : 'Elige al menos un momento';
    $('#goGen').disabled = !n; $$('.cand').forEach(el => el.classList.toggle('on', picked.has(el.dataset.id)));
    $$('.cand input[type=checkbox]').forEach(i => i.checked = picked.has(i.closest('.cand').dataset.id));
  };
  drawCands(); refresh();
  $('#cands').onclick = e => {
    const p = e.target.closest('[data-play]');
    if (p) { playCand(p, src.id, cands.find(c => c.id == p.dataset.play)); return }
    if (e.target.closest('a,video')) return;
    const el = e.target.closest('.cand'); if (!el || el.classList.contains('done')) return;
    const cid = el.dataset.id; picked.has(cid) ? picked.delete(cid) : picked.add(cid); refresh();
  };
  const free = () => cands.filter(c => !c.clip_id);
  $('#selTop').onclick = () => { picked.clear(); free().sort((a, b) => b.score - a.score).slice(0, 3).forEach(c => picked.add(c.id)); refresh() };
  $('#selAll').onclick = () => { free().forEach(c => picked.add(c.id)); refresh() };
  $('#selNone').onclick = () => { picked.clear(); refresh() };
  $$('[data-sort]').forEach(b => b.onclick = () => { sort = b.dataset.sort; $$('[data-sort]').forEach(x => x.classList.toggle('on', x == b)); drawCands(); refresh() });
  const single = (sel, key) => $(sel).onclick = e => { const b = e.target.closest('[data-v]'); if (!b) return; g[key] = b.dataset.v; $$('[data-v]', $(sel)).forEach(x => x.classList.toggle('on', x == b)) };
  single('#optLay', 'layout'); single('#optCap', 'caption_style');
  $('#optLang').onclick = e => { const b = e.target.closest('[data-l]'); if (!b) return; const l = b.dataset.l; if (chosenLangs.has(l)) { if (chosenLangs.size > 1) chosenLangs.delete(l) } else chosenLangs.add(l); b.classList.toggle('on', chosenLangs.has(l)) };
  $('#goGen').onclick = async () => {
    g.model = (document.querySelector('input[name=gm]:checked') || {}).value || 'standard'; store.set('gen', g);
    const t = cfg.tiers.find(t => t.id == g.model), n = picked.size;
    if (g.model == 'superior' && n > 3 && !I18n.confirm(`Vas a generar ${n} clips con ${t.name} (el modelo más caro). ¿Continuar?`)) return;
    $('#goGen').disabled = true;
    try { await post(`/api/jobs/${id}/generate`, { candidates: [...picked], model: g.model, layout: g.layout, caption_style: g.caption_style, languages: [...chosenLangs] }); toast(`Generando ${n} clip${n == 1 ? '' : 's'} con ${t.name}`, 'ok'); go('jobs') }
    catch (x) { $('#genm').innerHTML = `<div class="msg">${esc(x.message)}</div>`; $('#goGen').disabled = false }
  };
}
function playCand(box, sid, c) {
  $$('.pv16 video').forEach(v => { v.pause(); const b = v.parentNode; b.innerHTML = b.dataset.html });
  box.dataset.html = box.innerHTML;
  box.innerHTML = `<video controls autoplay playsinline src="/api/sources/${sid}/video#t=${c.start.toFixed(2)},${c.end.toFixed(2)}"></video>`;
  const v = box.firstChild; v.ontimeupdate = () => { if (v.currentTime >= c.end) { v.pause(); v.currentTime = c.start } };
  v.onerror = () => { box.innerHTML = box.dataset.html; toast('El navegador no puede reproducir este video aquí. Puedes elegir igual por el texto.', 'er') };
}

/* ---------- Clips y cola ---------- */
const hookOf = c => Object.values(c.hook_text || (c.edl || {}).hook?.text || {})[0] || c.id;
const clipDur = c => c.edl ? c.edl.source.end - c.edl.source.start : c.candidate ? c.candidate.end - c.candidate.start : 0;
const clipCard = c => `<div class="clip" onclick="clip('${c.id}')">
  <div class="th">${c.package_dir ? `<img loading="lazy" src="/api/clips/${c.id}/thumb" onerror="this.remove()">` : ''}
   <div class="top">${pill(c.status)}<span class="dur">${fmtDur(clipDur(c))}</span></div>
   ${canEdit(c) ? `<div class="acts"><button class="b sm" onclick="event.stopPropagation();closeModal();location.hash='#/editor/${c.id}'">${icon('cut')}Editar</button></div>` : ''}</div>
  <div class="bd"><button class="b g er sm clip-delete" onclick="event.stopPropagation();deleteLibrary('clips','${c.id}')" aria-label="Delete short">${icon('trash')}</button><div class="ttl">${esc(hookOf(c))}</div><div class="meta"><span data-ui-content>${c.id}</span>${c.has_draft ? ' · <span class="draft-dot">borrador sin renderizar</span>' : ''}</div>
  ${c.decision_note && !c.package_dir ? `<div class="note">${esc(c.decision_note.slice(0, 120))}</div>` : ''}</div></div>`;
function vQueue(q) {
  $('#main').innerHTML = `<div class="page">${head('Cola de aprobación', 'Clips terminados que esperan tu decisión.')}
   ${q.length ? `<div class="grid">${q.map(clipCard).join('')}</div>` : empty('inbox', 'La cola está vacía. Los clips aparecen aquí cuando un trabajo termina.')}</div>`;
}
async function vClips() {
  const viewProject = currentProject, viewTab = tab, epoch = viewEpoch;
  const all = await api('/api/clips');
  const groups = { all: () => true, queue: c => ['pending_approval', 'needs_human'].includes(c.status), approved: c => c.status == 'approved', rejected: c => ['rejected', 'rejected_by_human'].includes(c.status), failed: c => ['agent_failed', 'error'].includes(c.status) };
  const names = { all: 'Todos', queue: 'En cola', approved: 'Aprobados', rejected: 'Rechazados', failed: 'Con error' };
  const list = all.filter(groups[clipFilter]);
  if (epoch !== viewEpoch || currentProject !== viewProject || tab !== viewTab) return;
  $('#main').innerHTML = `<div class="page">${head('Clips', 'Todo lo que ha salido de tus videos. Abre uno para verlo o editarlo.',
    `<div class="chips">${Object.keys(groups).map(k => `<button class="chip ${clipFilter == k ? 'on' : ''}" data-f="${k}">${names[k]} ${all.filter(groups[k]).length}</button>`).join('')}</div>`)}
   ${list.length ? `<div class="grid">${list.map(clipCard).join('')}</div>` : empty('grid', 'No hay clips en esta vista.')}</div>`;
  $$('[data-f]').forEach(b => b.onclick = () => { clipFilter = b.dataset.f; vClips() });
}
async function clip(id) {
  const epoch = viewEpoch;
  const c = await api('/api/clips/' + id), langs = (c.edl || {}).languages || [], review = c.review || {}, can = ['pending_approval', 'needs_human'].includes(c.status);
  if (epoch !== viewEpoch) return;
  const L = c.package_dir ? langs[0] : null;
  modal(`<div class="short-layout"><section class="short-screen"><div class="short-player">${L ? `<video id="vid" controls src="/api/clips/${id}/file/${L}/video"></video>` : '<div class="empty">Sin video</div>'}
   </div><div style="margin-top:10px;display:flex;gap:6px;flex-wrap:wrap">${L ? `<div class="seg">${langs.map((l, i) => `<button class="${i ? '' : 'on'}" onclick="lang(this,'${id}','${l}')">${l.toUpperCase()}</button>`).join('')}</div>
   <a class="b g sm" id="dl" href="/api/clips/${id}/file/${L}/video?download=true">${icon('dl')}mp4</a>` : ''}</div>
   ${canEdit(c) ? `<button class="b" style="width:100%;margin-top:10px" onclick="closeModal();location.hash='#/editor/${id}'">${icon('cut')}Abrir en el editor</button>` : ''}
   ${cfg.desktop && c.status == 'error' && c.has_draft && c.edl ? `<button class="b g" style="width:100%;margin-top:8px" onclick="retryRender('${id}')">Reintentar render del borrador</button>` : ''}
   ${c.edl && (c.job_id || '').startsWith('job_') ? `<button class="b g" style="width:100%;margin-top:8px" onclick="creativeDialog('${id}')">${icon('spark')}Nueva edición con IA</button>` : ''}<button class="b g er" style="width:100%;margin-top:8px" onclick="deleteLibrary('clips','${id}')">${icon('trash')}Delete short</button></section>
   <section class="short-details"><h2 style="font-size:19px;padding-right:34px">${esc(hookOf(c))}</h2><div style="margin:8px 0">${pill(c.status)} <span class="mut sm" data-ui-content>${id} · ${fmtDur(clipDur(c))}</span></div>
    ${c.has_draft ? `<div class="msg wa">Hay un borrador del editor sin renderizar: el video de aquí no incluye esos cambios.</div>` : ''}
    ${c.needs_safe_render ? '<div class="msg wa">Este video tiene mejoras de encuadre o zonas seguras pendientes. Ábrelo en el editor y vuelve a renderizarlo para aplicarlas.</div>' : ''}
    ${c.edl?.editorial ? `<div class="sect"><h3>Intención de la edición</h3><p data-ui-content>${esc(c.edl.editorial.original_angle)}</p><p class="mut sm" data-ui-content>${esc(c.edl.editorial.reason)}</p><p class="mut sm">Las ampliaciones de caras se basan en fotogramas y deben revisarse. Los efectos no garantizan originalidad ni monetización.</p></div>` : ''}
    ${c.status == 'needs_human' ? `<div class="msg info"><b>Revisar:</b> ${esc(review.feedback || '')}</div>` : ''}
    ${c.status == 'approved' && c.schedule ? `<div class="msg info">Publicación sugerida: ${Object.entries(c.schedule).map(([l, t]) => `${l.toUpperCase()} ${esc(t)}`).join(' · ')}<br>La publicación automática no está activa: sube el paquete a mano.</div>` : ''}
    ${!c.package_dir && c.decision_note ? `<div class="msg">${esc(c.decision_note)}</div>` : ''}
    <div class="sect"><h3>Revisión</h3>${(review.checks || []).map(k => `<div class="chk"><span class="pill ${k.status == 'pass' ? 'ok' : k.status == 'warn' ? 'wa' : 'er'}">${esc(({pass:'Correcto',warn:'Advertencia',fail:'Fall\u00f3'})[k.status]||k.status)}</span><div><b>${esc(reviewLabel(k.name))}</b> <span class="mut">${k.name==='rights' && k.detail?.includes(':')?`<span>${esc(rightsLabel(k.detail.split(':')[0]))}</span>: <span data-ui-content>${esc(k.detail.slice(k.detail.indexOf(':')+1))}</span>`:esc(k.detail)}</span></div></div>`).join('') || '<span class="mut">—</span>'}</div>
    ${c.package_dir ? `<p class="mut sm">Paquete: <code>${esc(c.package_dir)}</code>${cfg.desktop ? ` <button class="b g sm" onclick="post('/api/clips/${id}/reveal',{}).catch(x=>toast(x.message,'er'))">Abrir carpeta</button>` : ''}</p>` : ''}
    ${can ? `<div class="sect"><h3>Tu decisión</h3><div class="row"><div><label>Quién decide</label><input id="who" value="${esc(who)}"></div><div><label>Nota (opcional)</label><input id="note"></div></div>
     <div style="margin-top:12px;display:flex;gap:8px"><button class="b ok" onclick="decide('${id}','approve')">${icon('check')}Aprobar</button><button class="b er" onclick="decide('${id}','reject')">Rechazar</button></div><div id="dm"></div></div>` :
      (c.decided_by ? `<p class="mut sm">Decidido por ${esc(c.decided_by)}. ${esc(c.decision_note || '')}</p>` : '')}
   </section><section class="short-publishing" aria-label="Sugerencias de publicación"><h2>Listo para publicar</h2><p class="mut sm">Copia el contenido de cada plataforma.</p><label for="publishLanguage">Idioma de las sugerencias</label><select id="publishLanguage">${[...new Set([...langs,...Object.keys(c.metadata || {})])].map(l=>`<option value="${esc(l)}">${esc(l.toUpperCase())}</option>`).join('')}</select><div id="publishCards"></div></section></div>`);
  $('.modal').classList.add('short-modal');
  const publish = () => publishingCards(c, $('#publishLanguage').value);
  $('#publishLanguage').onchange = publish; publish();
  $$('.short-screen .seg button').forEach((btn,i)=>btn.onclick=()=>{lang(btn,id,langs[i]); $('#publishLanguage').value=langs[i];publish()});
}
function lang(btn, id, l) {
  $$('button', btn.parentNode).forEach(b => b.classList.toggle('on', b == btn));
  $('#vid').src = `/api/clips/${id}/file/${l}/video`; $('#dl').href = `/api/clips/${id}/file/${l}/video?download=true`;
}
async function decide(id, what) {
  who = $('#who').value.trim(); if (!who) { $('#dm').innerHTML = '<div class="msg">Indica quién decide.</div>'; return }
  try { localStorage.who = who } catch { }
  try { await post(`/api/clips/${id}/${what}`, { by: who, note: $('#note').value }); closeModal(); toast(what == 'approve' ? 'Clip aprobado' : 'Clip rechazado', 'ok'); render() }
  catch (x) { $('#dm').innerHTML = `<div class="msg">${esc(x.message)}</div>` }
}
function modal(h) { $('#modal').innerHTML = `<div class="modal" onclick="if(event.target==this)closeModal()"><div class="card"><button class="b g ic x" onclick="closeModal()" title="Cerrar">${icon('x')}</button>${h}</div></div>` }
function closeModal() { $('#modal').innerHTML = '' }
async function retryRender(id) {
  try { await post(`/api/clips/${id}/render`); closeModal(); toast('Render iniciado; se conserva el borrador y no se vuelve a llamar a la IA.', 'ok'); go('jobs'); }
  catch (x) { toast(x.message, 'er'); }
}
function creativeDialog(id) {
  modal(`<h2>Nueva edición con IA</h2><p class="mut sm">La IA volverá a editar este momento con ampliaciones de caras visibles, ritmo, sonidos y rótulos animados. Se crea otro clip y se conserva el actual. Reutiliza el análisis y la transcripción; las nuevas llamadas se cobran a tu cuenta del proveedor de IA.</p>
   <label>Encuadre</label><select id="creativeLayout"><option value="auto">Automático: conservar el contexto y centrar el sujeto</option><option value="gameplay_blur_bg">Completo + fondo: conservar todo el video</option><option value="center_crop">Recorte vertical: puede excluir contenido lateral</option></select>
   <div class="tiers">${tierCards('creativeModel', 'standard')}</div><button class="b" id="creativeGo">Crear nueva versión</button><div id="creativeError" class="mut sm"></div>`);
  bindTiers($('#modal'));
  $('#creativeGo').onclick = async () => {
    const button = $('#creativeGo'); button.disabled = true;
    try { await post(`/api/clips/${id}/creative-version`, { model: $('input[name="creativeModel"]:checked').value, layout: $('#creativeLayout').value }); closeModal(); toast('Creando una nueva edición. Sigue el registro en Trabajos.', 'ok'); go('jobs'); }
    catch (x) { button.disabled = false; $('#creativeError').textContent = x.message; }
  };
}
document.addEventListener('keydown', e => { if (e.key == 'Escape' && $('#modal').innerHTML) closeModal() });

(async () => {
  cfg = await api('/api/config');
  Shell.init();
  await loadProjects();
  document.title = 'Kairos Studio';
  paintCfg();
  setInterval(() => paintCfg(), 30000);
  route();
  if (cfg.desktop && !cfg.setup_completed) desktopSetup();
})();
