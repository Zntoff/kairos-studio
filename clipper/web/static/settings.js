/* Kairos Studio: Ajustes. Proveedor de IA (Claude, OpenAI, Gemini…), la API key de cada persona, consumo y herramientas. */
const usd = n => n == null ? '—' : '$' + n.toLocaleString(I18n.locale, { minimumFractionDigits: n < 100 ? 2 : 0, maximumFractionDigits: n < 1 ? 4 : 2 });
const num = n => (n || 0).toLocaleString(I18n.locale);
const short = n => n >= 1e6 ? (n / 1e6).toFixed(2) + ' M' : n >= 1e3 ? (n / 1e3).toFixed(1) + ' k' : String(n || 0);
let usageDays = 30;

let usageRequest = 0;
async function paintCfg() {
  const request = ++usageRequest;
  const el = $('#cfg'); el.style.cursor = 'pointer'; el.onclick = () => go('settings');
  el.setAttribute('role', 'button'); el.tabIndex = 0;
  el.onkeydown = e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); aiPick=provider;go('apis'); } };
  try {
    const u = await api('/api/usage?days=30'); if (request !== usageRequest) return;
    const b = u.budget, hasBudget = b.usd != null;
    el.innerHTML = `<span class="usage-label">${hasBudget ? 'Crédito restante · estimado' : 'Consumo · últimos 30 días'}</span><strong class="usage-value">${usd(hasBudget ? b.remaining : u.totals.cost)}</strong><span class="usage-note">${hasBudget ? `${usd(b.spent)} usados de ${usd(b.usd)}` : 'Configura tu crédito en Ajustes'}${!cfg.api_key_set ? ' · IA sin configurar' : ''}</span>`;
  } catch { if (request === usageRequest) el.innerHTML = '<b>Consumo no disponible</b>Consultar en Ajustes'; }
}

async function vSettings() {
  const requestedTab = tab, epoch = viewEpoch;
  const [st, u, tools] = await Promise.all([api('/api/settings/ai'), api('/api/usage?days=' + usageDays), cfg.desktop ? api('/api/tools') : null]);
  if (tab !== requestedTab || epoch !== viewEpoch) return;
  paintCfg();
  const b = u.budget, spentPct = b.usd ? Math.min(100, Math.max(0, (b.spent / b.usd) * 100)) : 0, low = b.usd != null && b.remaining < b.usd * .15;
  const maxDay = Math.max(...u.by_day.map(d => d.cost), 1e-9);
  const lim = st.limits, limRows = lim ? [['requests', 'Peticiones por minuto'], ['input_tokens', 'Tokens de entrada por minuto'], ['output_tokens', 'Tokens de salida por minuto']].filter(([k]) => lim[k]) : [];
  $('#main').innerHTML = `<div class="page">
  ${head('Ajustes', 'Tu proveedor de IA, lo que has gastado y las herramientas de la app.')}
  ${cfg.version ? `<p class="mut sm">Kairos Studio ${esc(cfg.version)} · ${cfg.desktop ? 'Escritorio' : 'Contenedor'}</p>` : ''}

  <div class="card"><h2>Idioma de la interfaz</h2><div class="ui-language">${I18n.control()}</div><p class="mut sm">Solo cambia la interfaz; el idioma de los videos se configura por separado.</p></div>
  <div class="card appearance"><div><h2>Apariencia</h2><p class="mut sm">Kairos Studio · El momento exacto.</p></div><div><label for="appearanceTheme">Tema de la interfaz</label><select id="appearanceTheme"><option value="dark">Oscuro</option><option value="light">Claro</option><option value="system">Seguir al sistema</option></select></div></div>

  <div class="card"><h2>Rendimiento y hardware</h2><p class="mut sm">Elige el dispositivo para los nuevos renders. CPU reduce la dependencia de memoria gráfica; 30 fps requiere menos trabajo que 60 fps. Los renders que ya están en curso conservan sus opciones.</p>
   <div class="row c3"><div><label for="renderDevice">Dispositivo</label><select id="renderDevice"><option value="auto">Automático</option><option value="cpu">Procesador · CPU</option>${(cfg.devices || []).map(d => `<option value="${esc(d.id)}" ${d.available ? '' : 'disabled'}>${esc(d.name)} · ${esc(d.kind)}${d.available ? '' : ' · no disponible'}</option>`).join('')}</select></div>
   <div><label for="renderFps">Fotogramas por segundo</label><select id="renderFps"><option value="30">30 fps</option><option value="60">60 fps</option></select></div>
   <div><label for="renderQuality">Calidad (14–30, menor = mejor)</label><input id="renderQuality" type="number" min="14" max="30" step="1" value="${cfg.render?.quality ?? 19}"></div></div>
   ${cfg.desktop ? '<label class="tog"><input type="checkbox" id="gpuDecode">Decodificación por GPU en modo automático, si es compatible</label>' : '<p class="mut sm">Solo aparecen los codificadores que superan una prueba dentro del contenedor. Para utilizar una GPU, debe estar expuesta al contenedor por su configuración de Docker.</p>'}
   <p class="mut sm">Codificador actual: <code>${esc(cfg.render?.resolved_encoder || 'libx264')}</code>. Si una GPU falla durante el render, se reintenta por CPU.${cfg.gpu?.note ? ' ' + esc(cfg.gpu.note) : ''}</p><button class="b" id="saveRender">Guardar rendimiento</button><p id="renderMessage" class="mut sm" role="status"></p></div>

  ${cfg.desktop ? `<div class="card"><h2>Videos y transcripción</h2><p class="mut sm">Carpeta para los nuevos videos generados. Los clips anteriores conservan su ubicación.</p>
   <div style="display:flex;gap:8px"><input id="outputDir" value="${esc(cfg.output_dir)}" style="flex:1"><button class="b g" id="outputBrowse">Elegir carpeta</button></div>
   <label>Transcripción</label><select id="transcriptionMode"><option value="fast" ${cfg.transcription_mode == 'fast' ? 'selected' : ''}>Rápida — una alternativa por palabra</option><option value="accurate" ${cfg.transcription_mode == 'accurate' ? 'selected' : ''}>Precisa — varias alternativas, tarda más</option></select>
   <p class="mut sm">El modo rápido puede perder algo de precisión. Las transcripciones ya terminadas se reutilizan.</p>
   <button class="b" id="saveOutput">Guardar preferencias</button><button class="b g" id="openSetup" style="margin-left:8px">Configuración inicial</button></div>` : ''}

  <div class="card"><h2>APIs y modelos</h2><p class="mut">Proveedor activo: ${esc(st.provider_name)}. Administra tus conexiones y elige los modelos para cada nivel.</p><button class="b" onclick="go('apis')">Administrar APIs y modelos</button><button class="b g" onclick="quickProvider()">Cambiar proveedor</button></div>

  <div class="card"><div style="display:flex;align-items:center;gap:12px;flex-wrap:wrap;margin-bottom:6px"><h2 style="margin:0">Consumo</h2><span style="flex:1"></span>
    <div class="seg" id="range">${[7, 30, 90].map(d => `<button data-d="${d}" class="${d == usageDays ? 'on' : ''}">${d} días</button>`).join('')}</div></div>
   <div class="sub">Calculado con las llamadas que hace esta app. Ningún proveedor entrega el saldo de la cuenta por API: el crédito restante es tu estimación, a partir del importe que indiques.</div>
   <div class="usage-hero">
    <div><small>${b.usd != null ? 'Crédito restante (estimado)' : 'Gastado en este periodo'}</small>
     <div class="big ${low ? 'low' : ''}">${b.usd != null ? usd(b.remaining) : usd(u.totals.cost)}</div>
     ${b.usd != null ? `<div class="meter"><i style="width:${spentPct.toFixed(1)}%"></i></div><div class="mut sm">${usd(b.spent)} gastados de ${usd(b.usd)} · desde <time data-ui-date="${esc(b.since)}" data-ui-date-style="date">${esc(I18n.date(b.since,'date'))}</time></div>${low ? '<div class="msg wa" style="margin-top:10px">Queda menos del 15 % de tu crédito. Añade saldo en la consola de tu proveedor y actualiza el importe aquí.</div>' : ''}` : '<div class="mut sm">Indica cuánto crédito cargaste para ver cuánto te queda.</div>'}</div>
    <form id="budgetForm" class="budget"><label>Crédito cargado (USD)</label>
     <div style="display:flex;gap:8px"><input id="budgetIn" type="number" min="0" step="0.01" placeholder="20.00" value="${b.usd ?? ''}"><button class="b g">${b.usd != null ? 'Actualizar' : 'Fijar'}</button></div>
     <div class="mut sm" style="margin-top:6px">Se cuenta el gasto desde que lo fijas. Hazlo cada vez que recargues.</div></form></div>

   <div class="stats4">${[['Gasto', usd(u.totals.cost)], ['Llamadas', num(u.totals.calls)], ['Tokens de entrada', short(u.totals.input)], ['Tokens de salida', short(u.totals.output)]].map(([l, v]) => `<div><small>${l}</small><b>${v}</b></div>`).join('')}</div>

   <h3 class="sect-h">Gasto por día</h3>
   <div class="chart" style="--n:${u.by_day.length}">${u.by_day.map(d => `<div class="col" title="${d.day}: ${usd(d.cost)} · ${d.calls} llamadas"><i style="height:${Math.max(d.cost > 0 ? 3 : 0, d.cost / maxDay * 100)}%"></i></div>`).join('')}</div>
   <div class="chart-x"><span>${u.by_day[0].day.slice(5)}</span><span>${u.by_day[u.by_day.length - 1].day.slice(5)}</span></div>

   <h3 class="sect-h">Por modelo</h3>
   ${u.by_model.length ? `<table><tr><th>Modelo</th><th>Proveedor</th><th>Llamadas</th><th>Entrada</th><th>Salida</th><th style="text-align:right">Costo</th></tr>${u.by_model.map(m => `<tr><td>${esc(m.model)}${m.priced ? '' : ' <span class="mut sm">· sin precio</span>'}</td><td>${esc((st.providers.find(p => p.id == m.provider) || {}).name || m.provider)}</td><td>${num(m.calls)}</td><td>${short(m.input)}</td><td>${short(m.output)}</td><td style="text-align:right"><b>${usd(m.cost)}</b></td></tr>`).join('')}</table>` : '<div class="mut sm">Todavía no hay llamadas en este periodo.</div>'}

   ${limRows.length ? `<h3 class="sect-h">Margen de la API ahora mismo</h3><div class="stats4" style="grid-template-columns:repeat(${limRows.length},1fr)">${limRows.map(([k, l]) => { const x = lim[k]; return `<div><small>${l}</small><b>${num(x.remaining)} <span class="mut">/ ${num(x.limit)}</span></b><div class="meter"><i style="width:${(x.remaining / x.limit * 100).toFixed(0)}%"></i></div></div>` }).join('')}</div>
    <div class="mut sm" style="margin-top:8px">Límites de velocidad de tu cuenta, medidos <time data-ui-date="${esc(lim.checked_at)}">${esc(I18n.date(lim.checked_at))}</time>. Pulsa «Probar conexión» para actualizarlos.</div>` : ''}
   <p class="mut sm" style="margin:18px 0 0">Precios de ${esc(u.provider.name)} por millón de tokens (entrada / salida): ${Object.values(u.prices).map(p => `${esc(p.name)} ${p.input == null ? 'sin precio' : `$${p.input} / $${p.output}`}`).join(' · ')}. Puedes cambiarlos en «Proveedor de IA». Si tu plan tiene otros precios, el costo mostrado es una aproximación.${u.totals.unpriced_calls ? ` ${num(u.totals.unpriced_calls)} llamadas son de modelos sin precio configurado y no suman al gasto.` : ''}</p>
  </div>

  ${cfg.desktop ? `<div class="card"><h2>Herramientas</h2><div class="sub">FFmpeg hace el render y yt-dlp descarga videos. La app los usa desde su carpeta o desde el sistema.</div>
   <div class="stats4" style="grid-template-columns:repeat(3,1fr)">
    <div><small>FFmpeg</small><b>${tools.ffmpeg.ok ? 'Instalado' : 'Falta'}</b><div class="mut sm">${esc(tools.ffmpeg.version || 'Necesario para renderizar')}</div></div>
    <div><small>yt-dlp</small><b>${tools.ytdlp.ok ? 'Instalado' : 'Falta'}</b><div class="mut sm">${tools.ytdlp.ok ? 'Descarga desde enlaces' : 'Solo para descargar por enlace'}</div></div>
    <div><small>Hardware detectado</small><b>${tools.gpu.hardware ? 'Disponible' : 'CPU'}</b><div class="mut sm">${esc((tools.gpu.adapters || []).map(a => a.name).join(' · ') || 'Se renderiza por CPU')}</div></div></div>
   <div style="margin-top:16px;display:flex;align-items:center;gap:12px;flex-wrap:wrap"><button class="b" id="toolsInstall">${icon('dl')}${tools.ffmpeg.ok && tools.ytdlp.ok ? 'Reinstalar herramientas' : 'Instalar FFmpeg y yt-dlp'}</button><span class="mut sm">Descarga unos 150 MB desde GitHub (BtbN y yt-dlp) a ${esc(tools.dir)}.</span></div>
   <div style="margin-top:16px"><b>Transcripción: ${tools.whisper?.ok ? 'Instalada' : 'Falta'}</b><p class="mut sm">Necesaria para analizar videos con voz. Descarga whisper.cpp para CPU y un modelo multilingüe; solo se descargan los componentes que faltan.</p><button class="b g" id="whisperInstall">${icon('dl')}${tools.whisper?.ok ? 'Comprobar transcripción' : 'Instalar transcripción'}</button></div>
   <pre class="log" id="toolsLog" style="display:none"></pre></div>` : ''}
  </div>`;

  const msg = (html, kind = '') => { const el = $('#keyMsg'); if (el) el.innerHTML = html ? `<div class="msg ${kind}" style="margin-top:12px">${html}</div>` : ''; };
  $('#appearanceTheme').value = KairosTheme.preference;
  $('#appearanceTheme').onchange = e => KairosTheme.set(e.target.value);
  $('#renderDevice').value = cfg.render?.device || 'auto';
  if (!$('#renderDevice').value) $('#renderDevice').value = 'auto';
  $('#renderFps').value = String(cfg.render?.fps || 30);
  if ($('#gpuDecode')) $('#gpuDecode').checked = cfg.render?.gpu_decode !== false;
  $('#saveRender').onclick = async e => {
    const button = e.currentTarget; button.disabled = true;
    try {
      await send('PUT', '/api/settings', { device: $('#renderDevice').value, encoder: 'auto', fps: +$('#renderFps').value, quality: +$('#renderQuality').value, gpu_decode: $('#gpuDecode')?.checked ?? false });
      cfg = await api('/api/config'); toast('Rendimiento guardado', 'ok'); await vSettings();
    } catch (x) { $('#renderMessage').textContent = x.message; button.disabled = false; }
  };
  if ($('#outputBrowse')) $('#outputBrowse').onclick = () => chooseOutputFolder('#outputDir');
  if ($('#openSetup')) $('#openSetup').onclick = () => desktopSetup();
  if ($('#saveOutput')) $('#saveOutput').onclick = async () => {
    try {
      await send('PUT', '/api/setup', { output_dir: $('#outputDir').value, transcription_mode: $('#transcriptionMode').value });
      cfg = await api('/api/config'); toast('Preferencias guardadas', 'ok');
    } catch (x) { toast(x.message, 'er'); }
  };
  const busy = (btn, on, label) => { if (!btn) return; btn.disabled = on; if (label) btn.dataset.label = btn.dataset.label || btn.innerHTML; btn.innerHTML = on ? '<span class="spin"></span>' + label : btn.dataset.label || btn.innerHTML };

  $('#budgetForm').onsubmit = async e => {
    e.preventDefault(); const v = $('#budgetIn').value.trim();
    try { await send('PUT', '/api/settings/ai/budget', { usd: v === '' ? null : +v }); toast(v === '' ? 'Crédito quitado' : 'Crédito fijado', 'ok'); vSettings() } catch (x) { toast(x.message, 'er') }
  };
  $$('#range [data-d]').forEach(bn => bn.onclick = () => { usageDays = +bn.dataset.d; vSettings() });
  const installTools = async (selector, endpoint) => {
    const btn = $(selector), log = $('#toolsLog'); busy(btn, true, 'Instalando…'); log.style.display = 'block'; log.textContent = 'Iniciando…';
    try {
      const { run_id } = await post(endpoint);
      for (; ;) {
        await new Promise(r => setTimeout(r, 1200)); const r = await api('/api/runs/' + run_id);
        log.textContent = r.log.join('\n'); log.scrollTop = 1e9;
        if (r.status == 'failed') throw new Error(r.error || 'La instalación falló.');
        if (r.status == 'done') break;
      }
      toast('Herramientas instaladas', 'ok'); cfg = await api('/api/config'); paintCfg(); vSettings();
    } catch (x) { busy(btn, false); log.textContent += '\n' + x.message; toast(x.message, 'er') }
  };
  if ($('#toolsInstall')) $('#toolsInstall').onclick = () => installTools('#toolsInstall', '/api/tools/install');
  if ($('#whisperInstall')) $('#whisperInstall').onclick = () => installTools('#whisperInstall', '/api/tools/whisper/install');
}

/* ---------- Proveedor de IA ---------- */
const TIER_IDS = ['fast', 'standard', 'superior'];
const TIER_USE = { fast: 'encuentra los momentos', standard: 'edita, subtitula y escribe títulos', superior: 'para los clips más importantes' };
const discoveredModels = new Map();
let aiPick = null;   // provider shown in the card; the active one by default

function aiCard(st) {
  const p = st.providers.find(x => x.id == (aiPick || st.provider)) || st.providers[0], active = p.id == st.provider;
  const status = active ? (st.ready ? '<span class="pill ok">En uso · listo</span>' : '<span class="pill wa">En uso · incompleto</span>') : '<span class="pill">No está en uso</span>';
  const keyLabel = p.needs_key ? 'API key' : 'API key (opcional)';
  const price = (t, i) => (p.prices[t] || [])[i] ?? '';
  return `<div class="card" id="aiCard" data-provider="${esc(p.id)}"><h2>Proveedor de IA</h2>
   <div class="sub">Kairos funciona con Claude, OpenAI, Gemini y otros proveedores. Cada persona usa su propia API key: lo que se genera se cobra a su cuenta. Las keys se guardan solo en este equipo y nunca se muestran completas.</div>
   <div class="ai-head"><div><label for="aiProvider">Proveedor</label><select id="aiProvider">${st.providers.map(x => `<option value="${x.id}" ${x.id == p.id ? 'selected' : ''}>${esc(x.name)}${x.id == st.provider ? ' · en uso' : ''}</option>`).join('')}</select></div><div class="ai-status">${status}</div></div>
   ${p.note ? `<p class="mut sm">${esc(p.note)}</p>` : ''}
   ${active && !st.ready ? `<div class="msg wa">${esc(st.missing)}</div>` : ''}

   <h3 class="sect-h">${keyLabel}</h3>
   <div style="display:flex;align-items:center;gap:10px;margin-bottom:12px;flex-wrap:wrap">${p.key_set ? `<span class="pill ok">Guardada</span><code>${esc(p.key_hint)}</code><span class="mut sm">${p.source == 'env' ? 'viene del entorno' : 'guardada en este equipo'}</span>` : `<span class="pill ${p.needs_key ? 'wa' : ''}">Sin key</span>`}</div>
   <form id="keyForm" style="display:flex;gap:10px;flex-wrap:wrap"><input id="keyIn" type="password" autocomplete="off" spellcheck="false" aria-label="${keyLabel}" placeholder="${p.key_set ? 'Pega otra key para reemplazarla' : p.id == 'anthropic' ? 'sk-ant-…' : 'Pega tu key'}" style="flex:1;min-width:240px">
    <button class="b" id="keySave">${icon('check')}Guardar y probar</button>
    ${p.key_set || !p.needs_key ? '<button class="b g" type="button" id="keyTest">Probar conexión</button>' : ''}${p.key_set && p.source == 'panel' ? '<button class="b er" type="button" id="keyDel">Quitar</button>' : ''}</form>
   ${p.key_url ? `<p class="hint mut sm" style="margin:10px 0 0">Créala en <a href="${esc(p.key_url)}" target="_blank" rel="noopener">${esc(new URL(p.key_url).host)}</a>. ${p.kind == 'anthropic' ? 'Al guardar se hace una llamada mínima de prueba (una fracción de centavo).' : 'Al guardar se consulta la lista de modelos, sin costo.'}</p>` : ''}
   <div id="keyMsg"></div>

   ${p.editable_url ? `<h3 class="sect-h">Dirección del servidor</h3><input id="aiUrl" value="${esc(p.base_url || '')}" placeholder="http://localhost:11434/v1" spellcheck="false">` : ''}

   <h3 class="sect-h">Modelos y precios</h3>
   <p class="mut sm" style="margin-top:0">Elige qué modelo usa cada nivel. Pueden ser el mismo. Los precios (USD por millón de tokens) solo sirven para calcular tu consumo.</p>
   <datalist id="aiModelList">${(discoveredModels.get(p.id)||[]).map(m=>`<option value="${esc(m)}">`).join('')}</datalist><div id="modelCatalog"></div>
   <div class="ai-models">${TIER_IDS.map(t => `<div class="ai-tier"><div><b>${esc(p.tier_names[t])}</b><span class="mut sm">${TIER_USE[t]}</span></div>
     <input data-model="${t}" list="aiModelList" value="${esc(p.models[t] || '')}" placeholder="Nombre del modelo" spellcheck="false" aria-label="Modelo ${esc(p.tier_names[t])}">
     <input data-price="${t}:0" type="number" min="0" step="0.01" value="${price(t, 0)}" placeholder="Entrada" aria-label="Precio de entrada ${esc(p.tier_names[t])}">
     <input data-price="${t}:1" type="number" min="0" step="0.01" value="${price(t, 1)}" placeholder="Salida" aria-label="Precio de salida ${esc(p.tier_names[t])}"></div>`).join('')}</div>
   <div style="display:flex;gap:10px;flex-wrap:wrap;margin-top:14px"><button class="b" id="aiSave">${active ? 'Guardar cambios' : 'Guardar y usar este proveedor'}</button>${true ? `<button class="b g" type="button" id="aiLoad">${icon('dl')}Cargar modelos de ${esc(p.name)}</button>` : ''}</div>
   <div id="aiMsg"></div>
  </div>`;
}

function bindAiCard(st, msg, busy) {
  const p = st.providers.find(x => x.id == (aiPick || st.provider)) || st.providers[0];
  const current = () => ['settings','apis'].includes(tab) && $('#aiProvider')?.value === p.id;
  const q = p.id == st.provider ? '' : '?provider=' + encodeURIComponent(p.id);
  const done = async (res, note) => { cfg = await api('/api/config'); paintCfg(); if (res && res.warning) toast(res.warning, ''); if (note) toast(note, 'ok'); if (current()) await (tab==='apis'?vApis():vSettings()); };
  const fillModels = list => {
    if (!current() || !Array.isArray(list)) return;
    discoveredModels.set(p.id,list);
    $('#aiModelList').innerHTML = list.map(m => `<option value="${esc(m)}">`).join('');
    renderModelCatalog(p,list);
  };
  $('#aiProvider').onchange = e => { aiPick = e.target.value; (tab==='apis'?vApis():vSettings()).catch(showViewError); };
  if(discoveredModels.has(p.id)) fillModels(discoveredModels.get(p.id));
  $('#keyForm').onsubmit = async e => {
    e.preventDefault(); const key = $('#keyIn').value.trim(); if (!key) return msg('Pega tu API key.');
    busy($('#keySave'), true, 'Probando…'); msg('');
    try { if($('#aiUrl')) await send('PUT','/api/settings/ai/provider',{provider:p.id,base_url:$('#aiUrl').value.trim(),activate:false}); const r = await send('PUT', '/api/settings/ai/key', { key, provider: p.id }); await done(r, 'API key guardada y verificada'); if (r.models) fillModels(r.models); else if(current()) $('#aiLoad')?.click(); }
    catch (x) { busy($('#keySave'), false); msg(esc(x.message)); }
  };
  if ($('#keyTest')) $('#keyTest').onclick = async () => {
    busy($('#keyTest'), true, 'Probando…'); msg('');
    try { const r = await post('/api/settings/ai/test?provider=' + encodeURIComponent(p.id)); await done(r, 'La conexión funciona'); if (r.models) fillModels(r.models); else if(current()) $('#aiLoad')?.click(); }
    catch (x) { busy($('#keyTest'), false); msg(esc(x.message)); }
  };
  if ($('#keyDel')) $('#keyDel').onclick = async () => {
    if (!I18n.confirm(`¿Quitar la API key de ${p.name} de este equipo?`)) return;
    await done(await send('DELETE', '/api/settings/ai/key?provider=' + encodeURIComponent(p.id)));
  };
  if ($('#aiLoad')) $('#aiLoad').onclick = async () => {
    const btn = $('#aiLoad'); busy(btn, true, 'Cargando…');
    try {
      if ($('#aiUrl')) await send('PUT', '/api/settings/ai/provider', { provider: p.id, base_url: $('#aiUrl').value.trim(), activate: false });
      const r = await api('/api/settings/ai/models?provider=' + encodeURIComponent(p.id)); fillModels(r.models);
      if (current() && !r.models.length) $('#aiMsg').innerHTML = '<div class="msg wa" style="margin-top:12px">El proveedor no devolvió modelos. Escribe el nombre a mano.</div>';
    } catch (x) { if (current()) $('#aiMsg').innerHTML = `<div class="msg er" style="margin-top:12px">${esc(x.message)}</div>`; }
    busy(btn, false);
  };
  $('#aiSave').onclick = async () => {
    const models = Object.fromEntries(TIER_IDS.map(t => [t, $(`[data-model="${t}"]`).value.trim()]));
    const prices = {};
    for (const t of TIER_IDS) {
      const a = $(`[data-price="${t}:0"]`).value, b = $(`[data-price="${t}:1"]`).value;
      if (a !== '' || b !== '') prices[t] = [+a || 0, +b || 0];
    }
    const body = { provider: p.id, models, prices, activate: true };
    if ($('#aiUrl')) body.base_url = $('#aiUrl').value.trim();
    const btn = $('#aiSave'); btn.disabled = true;
    try { const r = await send('PUT', '/api/settings/ai/provider', body); aiPick = null; await done(null, r.ready ? `${p.name} está listo para usarse` : 'Guardado'); }
    catch (x) { btn.disabled = false; $('#aiMsg').innerHTML = `<div class="msg er" style="margin-top:12px">${esc(x.message)}</div>`; }
  };
}

async function chooseOutputFolder(input) {
  try { const r = await post('/api/setup/folder'); if (r.path) $(input).value = r.path; }
  catch (x) { toast(x.message, 'er'); }
}

function desktopSetup(step = 0) {
  const finish = async () => {
    await send('PUT', '/api/setup', { completed: true });
    cfg = await api('/api/config'); closeModal(); paintCfg();
    toast('Puedes cambiar estas opciones en Ajustes cuando quieras.', 'ok');
  };
  if (step == 0) {
    modal(`<div class="setup-brand"><img class="brand-dark" src="/static/brand/logo-white.svg" alt="Kairos Studio"><img class="brand-light" src="/static/brand/logo-black.svg" alt="Kairos Studio"></div><h2 class="brand-heading">El momento exacto.</h2><p class="mut sm">Paso 1 de 2 · Elige dónde guardar tus videos generados.</p>
     <label>Carpeta de salida</label><div style="display:flex;gap:8px"><input id="setupPath" value="${esc(cfg.output_dir)}" style="flex:1;min-width:0"><button class="b g" id="setupBrowse">Elegir carpeta</button></div>
     <p class="mut sm">Los videos que ya tienes conservan su ubicación. Si omites este paso, se usará la carpeta mostrada.</p>
     <div id="setupError" class="mut sm"></div><div style="display:flex;gap:8px;flex-wrap:wrap;margin-top:18px"><button class="b" id="setupNext">Guardar y continuar</button><button class="b g" id="setupSkip">Omitir este paso</button><button class="b g" id="setupLater">Configurar todo después</button></div>`);
    $('#setupBrowse').onclick = () => chooseOutputFolder('#setupPath');
    $('#setupSkip').onclick = () => desktopSetup(1);
    $('#setupLater').onclick = () => finish().catch(x => toast(x.message, 'er'));
    $('#setupNext').onclick = async () => {
      const btn = $('#setupNext'); btn.disabled = true;
      try { await send('PUT', '/api/setup', { output_dir: $('#setupPath').value }); cfg = await api('/api/config'); desktopSetup(1); }
      catch (x) { $('#setupError').textContent = x.message; btn.disabled = false; }
    };
  } else {
    modal(`<h2>Conectar con la IA</h2><p class="mut sm">Paso 2 de 2 · ${cfg.api_key_set ? 'Ya tienes un proveedor configurado. Puedes conservarlo o cambiarlo.' : 'Elige tu proveedor y añade tu API key para analizar y generar clips.'}</p>
     <label for="setupProvider">Proveedor</label><select id="setupProvider"><option value="anthropic">Anthropic · Claude</option><option value="openai">OpenAI</option><option value="gemini">Google Gemini</option><option value="openrouter">OpenRouter</option><option value="mistral">Mistral</option><option value="deepseek">DeepSeek</option><option value="groq">Groq</option></select>
     <p class="mut sm">Ollama, LM Studio y otros servidores locales se configuran en Ajustes. Con un proveedor distinto de Claude, después eliges sus modelos en Ajustes.</p>
     <label for="setupKey">API key</label><input id="setupKey" type="password" autocomplete="off" spellcheck="false" placeholder="Pega tu API key">
     <p class="mut sm">Se guarda cifrada para tu usuario de Windows. Guardar hace una llamada mínima de prueba a tu cuenta. Puedes omitirlo y editar clips manualmente.</p>
     <p class="mut sm">Las herramientas y la transcripción se instalan desde Ajustes cuando las necesites.</p>
     <div id="setupError" class="mut sm"></div><div style="display:flex;gap:8px;flex-wrap:wrap;margin-top:18px"><button class="b" id="setupSaveKey">Guardar y terminar</button><button class="b g" id="setupSkipKey">${cfg.api_key_set ? 'Conservar key y terminar' : 'Omitir y configurar después'}</button><button class="b g" id="setupBack">Volver</button></div>`);
    $('#setupBack').onclick = () => desktopSetup(0);
    $('#setupSkipKey').onclick = () => finish().catch(x => toast(x.message, 'er'));
    $('#setupSaveKey').onclick = async () => {
      const key = $('#setupKey').value.trim();
      if (!key) { $('#setupError').textContent = 'Pega tu API key o pulsa Omitir.'; return; }
      const btn = $('#setupSaveKey'); btn.disabled = true; $('#setupError').textContent = 'Verificando conexión…';
      const provider = $('#setupProvider').value;
      try {
        const r = await send('PUT', '/api/settings/ai/key', { key, provider });
        const st = await send('PUT', '/api/settings/ai/provider', { provider, activate: true });
        if (r.warning) toast(r.warning);
        if(r.models)discoveredModels.set(provider,r.models);
        await finish();
        if (!st.ready) { toast(st.missing, ''); aiPick=provider;go('apis'); }
      }
      catch (x) { $('#setupError').textContent = x.message; btn.disabled = false; }
    };
  }
}
