/* Shared workspace shell, project dashboard and resource browser. No AI calls on navigation. */
const Shell = (() => {
  const media = matchMedia('(max-width: 767px)'), rail = matchMedia('(max-width: 1279px)');
  let compact = null, lastFocus = null;
  try { const saved = localStorage.getItem('kairos.sidebar'); if (saved != null) compact = saved === 'compact'; } catch {}
  function sync() {
    const mobile = media.matches, collapsed = !mobile && (compact ?? rail.matches);
    document.body.classList.toggle('sidebar-compact', collapsed);
    if (!mobile) close();
    const toggle = document.getElementById('sidebarToggle');
    toggle.innerHTML = icon(mobile ? 'x' : 'panel');
    toggle.setAttribute('aria-label', mobile ? 'Cerrar navegación' : collapsed ? 'Expandir navegación' : 'Contraer navegación');
    toggle.setAttribute('aria-expanded', String(!collapsed));
    $('#mobileProject').innerHTML = `<span data-ui-content>${esc(projectName())}</span>`;
    $('#mobileProject').title = 'Proyecto actual: ' + projectName();
    $('#cfg').title = 'Consultar crédito y consumo en Ajustes';
    $('#sidebar').inert = mobile && !document.body.classList.contains('nav-open');
  }
  function close() {
    const wasOpen = document.body.classList.contains('nav-open');
    document.body.classList.remove('nav-open');
    $('#navBackdrop').hidden = true;
    $('#mobileMenu').setAttribute('aria-expanded', 'false');
    $('#sidebar').inert = media.matches;
    if (wasOpen && lastFocus?.isConnected) lastFocus.focus();
  }
  function open() {
    if (!media.matches) return;
    lastFocus = document.activeElement;
    document.body.classList.add('nav-open'); $('#navBackdrop').hidden = false;
    $('#sidebar').inert = false; $('#mobileMenu').setAttribute('aria-expanded', 'true');
    $('#sidebarToggle').focus();
  }
  function init() {
    Object.assign(P, {
      home:'<path d="M3 10l9-7 9 7v10H3z"/><path d="M9 20v-8h6v8"/>',
      folder:'<path d="M3 5h6l2 3h10v12H3z"/>',
      panel:'<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M9 4v16"/>',
      menu:'<path d="M4 6h16M4 12h16M4 18h16"/>',
      search:'<circle cx="10.5" cy="10.5" r="6.5"/><path d="M16 16l5 5"/>',
    });
    $('#mobileMenu').innerHTML = icon('menu'); $('#mobileMenu').onclick = open;
    $('#navBackdrop').onclick = close;
    $('#mobileProject').onclick = () => chooseProject();
    $('#sidebarToggle').onclick = () => {
      if (media.matches) { close(); return; }
      compact = !document.body.classList.contains('sidebar-compact');
      try { localStorage.setItem('kairos.sidebar', compact ? 'compact' : 'expanded'); } catch {}
      sync();
    };
    media.addEventListener('change', sync); rail.addEventListener('change', sync);
    document.addEventListener('keydown', e => {
      if (!document.body.classList.contains('nav-open')) return;
      if (e.key === 'Escape') { e.preventDefault(); close(); }
      if (e.key === 'Tab') {
        const all = $$('button,select,a[href],[tabindex="0"]', $('#sidebar')).filter(el => !el.disabled && el.getClientRects().length);
        if (e.shiftKey && document.activeElement === all[0]) { e.preventDefault(); all.at(-1).focus(); }
        else if (!e.shiftKey && document.activeElement === all.at(-1)) { e.preventDefault(); all[0].focus(); }
      }
    });
    sync();
  }
  return { init, sync, open, close };
})();
const nameOfSource = s => String(s.path || s.id).split(/[\\/]/).pop();
const searchText = s => String(s || '').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLocaleLowerCase();
const resourceUrl = (project, id) => `/api/projects/${encodeURIComponent(project)}/resources/${encodeURIComponent(id)}/file`;
const fileSize = n => n >= 1048576 ? (n/1048576).toFixed(1)+' MB' : Math.max(1,Math.round(n/1024))+' KB';

function chooseProject() {
  Shell.close();
  modal(`<h2>Cambiar proyecto</h2><p class="mut sm">Cada proyecto mantiene sus videos, recursos y generaciones.</p><div class="project-chooser">${projects.map(p => `<button class="b g" data-project="${esc(p.id)}">${icon('folder')}<span>${esc(p.name)}</span>${p.id === currentProject ? icon('check') : ''}</button>`).join('')}</div><button class="b" id="chooserNew">${icon('plus')}Definir un proyecto</button>`);
  $$('[data-project]', $('#modal')).forEach(button => button.onclick = () => { switchProject(button.dataset.project); });
  $('#chooserNew').onclick = () => editProject();
}

async function vDashboard() {
  const epoch = viewEpoch, project = currentProject;
  const [sources, jobs, clips, resources] = await Promise.all([api('/api/sources'),api('/api/jobs'),api('/api/clips'),api('/api/projects/'+project+'/resources')]);
  if (epoch !== viewEpoch || project !== currentProject || tab !== 'dashboard') return;
  const pending = clips.filter(c => ['needs_human','pending_approval'].includes(c.status));
  $('#main').innerHTML = `<div class="page dashboard"><section class="dashboard-hero" aria-labelledby="dashboardTitle"><div class="dashboard-logo"><img class="brand-dark" src="/static/brand/logo-white.svg" alt="Kairos Studio"><img class="brand-light" src="/static/brand/logo-black.svg" alt="Kairos Studio"></div><p class="dashboard-eyebrow">El momento exacto.</p><h1 id="dashboardTitle">¿Qué quieres hacer hoy?</h1><form id="dashboardSearchForm" role="search"><label class="sr-only" for="dashboardSearch">Buscar proyectos o contenido del proyecto actual</label><div class="dashboard-search">${icon('search')}<input id="dashboardSearch" type="search" autocomplete="off" placeholder="Busca proyectos, videos, shorts o recursos…" aria-controls="dashboardResults"><button class="b g ic" type="button" id="dashboardAdd" aria-label="Add files or video links" title="Add files or video links">${icon('plus')}</button><button class="b g ic" type="submit" aria-label="Buscar">${icon('search')}</button></div><p class="search-scope">Contenido de <button type="button" class="text-button" id="dashboardProject">${esc(projectName())}</button> · Los proyectos se buscan por nombre.</p></form><div id="dashboardResults" role="region" aria-label="Resultados de búsqueda" hidden></div><div class="dashboard-actions">${cfg.desktop ? `<button class="action-card" data-action="compose">${icon('cut')}<strong>Crear video</strong><span>Abre un editor vacío con el formato que elijas.</span></button>` : ''}<button class="action-card" data-action="project">${icon('folder')}<strong>Definir un proyecto</strong><span>Organiza una nueva idea y sus recursos.</span></button><button class="action-card" data-action="video">${icon('plus')}<strong>Añadir un nuevo video</strong><span>Sube un archivo o pega un enlace.</span></button><button class="action-card" data-action="short">${icon('spark')}<strong>Generar un short</strong><span>Elige un video de este proyecto.</span></button></div></section><section class="dashboard-project" aria-labelledby="projectOverview"><div class="section-heading"><div><p class="project-context">Proyecto actual</p><h2 id="projectOverview">${esc(projectName())}</h2></div><button class="b g sm" id="dashboardChangeProject">Cambiar proyecto</button></div><div class="dashboard-stats">${[['Videos',sources.length,'videos'],['Shorts',clips.length,'clips'],['Por revisar',pending.length,'queue'],['Recursos',resources.length,'resources']].map(([label,count,route])=>`<button onclick="go('${route}')"><b>${count}</b><span>${label}</span></button>`).join('')}</div><div class="dashboard-columns"><section><div class="section-heading"><h2>Videos del proyecto</h2><button class="text-button" onclick="go('videos')">Ver todos</button></div>${sources.length ? sources.slice(-4).reverse().map(s=>`<button class="dashboard-row" data-source="${esc(s.id)}">${icon('film')}<span><strong>${esc(nameOfSource(s))}</strong><small data-ui-content>${fmtDur(s.duration)} · ${s.width} × ${s.height}</small></span>${icon('spark')}</button>`).join('') : '<div class="dashboard-empty">Añade un video para empezar a crear shorts en este proyecto.</div>'}</section><section><div class="section-heading"><h2>Generaciones</h2><button class="text-button" onclick="go('jobs')">Ver todas</button></div>${jobs.length ? jobs.slice(-4).reverse().map(j=>`<button class="dashboard-row" data-job="${esc(j.id)}">${icon('pulse')}<span><strong>${esc(nameOfSource(sources.find(s=>s.id===j.source_id)||{id:j.id}))}</strong><small data-ui-content>${esc(j.id)}</small></span>${pill(j.status)}</button>`).join('') : '<div class="dashboard-empty">Las generaciones de este proyecto aparecerán aquí.</div>'}</section></div></section></div>`;
  $('.dashboard-actions').insertAdjacentHTML('afterend', '<p class="dashboard-demo"><a class="text-button" href="/static/guided-demo.html">Demo guiada</a></p>');
  const action = value => value === 'compose' ? newVideoProject(project) : value === 'project' ? editProject() : value === 'video' ? go('videos') : generateShort(sources,jobs);
  $$('[data-action]').forEach(b=>b.onclick=()=>action(b.dataset.action));
  $('#dashboardAdd').onclick=()=>importResources();
  $('#dashboardProject').onclick = $('#dashboardChangeProject').onclick = chooseProject;
  $$('[data-source]').forEach(b=>b.onclick=()=>generateShort(sources,jobs,b.dataset.source));
  $$('[data-job]').forEach(b=>b.onclick=()=>job(b.dataset.job));
  const results = $('#dashboardResults'), search = $('#dashboardSearch');
  function find() {
    const q = searchText(search.value.trim()); results.hidden = !q;
    if (!q) { results.innerHTML=''; return; }
    const matches = [];
    for (const p of projects) if (searchText(p.name+' '+p.description).includes(q)) matches.push({type:'Proyecto',name:p.name,run:()=>{switchProject(p.id,'dashboard');}});
    for (const s of sources) if (searchText(nameOfSource(s)+' '+s.credit).includes(q)) matches.push({type:'Video',name:nameOfSource(s),run:()=>generateShort(sources,jobs,s.id)});
    for (const c of clips) if (searchText(hookOf(c)+' '+c.id).includes(q)) matches.push({type:'Short',name:hookOf(c)||c.id,run:()=>clip(c.id)});
    for (const r of resources) if (searchText(r.name+' '+r.description).includes(q)) matches.push({type:'Recurso',name:r.name,run:()=>go('resources')});
    results.innerHTML = `<p class="mut sm" role="status">${matches.length ? matches.length+' resultados' : 'Sin resultados en este proyecto.'}</p>${matches.slice(0,20).map((r,i)=>`<button class="search-result" data-result="${i}"><span class="pill">${r.type}</span><span>${esc(r.name)}</span>${icon('back')}</button>`).join('')}`;
    $$('[data-result]',results).forEach(b=>b.onclick=()=>matches[+b.dataset.result].run());
  }
  search.oninput=find; $('#dashboardSearchForm').onsubmit=e=>{e.preventDefault();find();$('button[data-result]',results)?.focus();};
}

function generateShort(sources,jobs,selected) {
  const project = currentProject;
  const choices = selected ? sources.filter(s=>s.id===selected) : sources;
  modal(`<h2>Generar un short</h2><p class="mut sm">Proyecto: <b>${esc(projectName())}</b>. Elige el video que quieres convertir en shorts. Se usará el proveedor configurado en Ajustes y su consumo se registrará en la app.</p>${choices.length ? `<div class="short-source-list">${choices.map(s=>{const ready=jobs.find(j=>j.source_id===s.id && j.status==='awaiting_selection');return `<article class="short-source"><div><b>${esc(nameOfSource(s))}</b><span class="mut sm">${fmtDur(s.duration)} · ${s.width} × ${s.height}</span></div><div class="project-actions">${ready ? `<button class="b" data-select-job="${esc(ready.id)}">Elegir momentos</button>` : `<button class="b" data-analyze="${esc(s.id)}">Analizar momentos</button>`}${cfg.desktop ? `<button class="b g" data-manual="${esc(s.id)}">Crear manualmente</button>` : ''}</div></article>`;}).join('')}</div>` : '<div class="dashboard-empty">Este proyecto aún no tiene videos.</div>'}<button class="b g" id="shortAddVideo">${icon('plus')}Añadir un nuevo video</button>`);
  $('#shortAddVideo').onclick=()=>{closeModal();go('videos');};
  $$('[data-analyze]').forEach(b=>b.onclick=()=>{if(project!==currentProject)return;closeModal();proc(b.dataset.analyze);});
  $$('[data-select-job]').forEach(b=>b.onclick=()=>{closeModal();go('select/'+b.dataset.selectJob);});
  $$('[data-manual]').forEach(b=>b.onclick=()=>{const s=sources.find(s=>s.id===b.dataset.manual);newClip(s.id,s.duration);});
}

async function vResources() {
  const epoch=viewEpoch, project=currentProject;
  const rows=await api('/api/projects/'+project+'/resources');
  if(epoch!==viewEpoch || project!==currentProject || tab!=='resources')return;
  $('#main').innerHTML=`<div class="page">${head('Recursos','Archivos de apoyo exclusivos de este proyecto.')}<div class="card"><h2>Añadir recursos</h2><p class="mut sm">Guarda imágenes, audio y documentos de referencia. Estos archivos no se añaden automáticamente al video; el catálogo de efectos incorporado sigue disponible en el editor.</p><form id="resourceForm"><label for="resourceFile">Archivo (hasta 100 MB)</label><input id="resourceFile" name="file" type="file" required accept=".png,.jpg,.jpeg,.webp,.gif,.wav,.mp3,.m4a,.ogg,.flac,.pdf,.txt,.md,.json"><label for="resourceDescription">Descripción (opcional)</label><input id="resourceDescription" name="description" maxlength="600" placeholder="Logo del canal, referencia, audio…"><div class="resource-submit"><button class="b">${icon('plus')}Guardar en ${esc(projectName())}</button><span id="resourceMessage" class="mut sm" role="status"></span></div></form></div><div class="resource-grid">${rows.map(r=>`<article class="card resource-card">${icon(r.kind==='audio'?'music':r.kind==='image'?'frame':'folder')}<h2 data-ui-content>${esc(r.name)}</h2><p class="mut sm" ${r.description?'data-ui-content':''}>${esc(r.description||'Sin descripción')}</p><small class="mut">${esc(r.kind)} · ${fileSize(r.size)}</small>${r.kind==='audio'?`<audio controls preload="none" src="${resourceUrl(project,r.id)}"></audio>`:r.kind==='image'?`<img class="resource-image" loading="lazy" src="${resourceUrl(project,r.id)}" alt="${esc(r.name)}">`:''}<a class="b g sm" href="${resourceUrl(project,r.id)}?download=true" download>Descargar</a><button class="b g er sm" onclick="deleteLibrary('resources','${r.id}')" aria-label="Delete resource">${icon('trash')}Delete</button></article>`).join('')||empty('folder','Este proyecto aún no tiene recursos propios.')}</div></div>`;
  $('#resourceForm').onsubmit=async e=>{
    e.preventDefault();const file=$('#resourceFile').files[0],button=$('button',e.target),message=$('#resourceMessage');
    if(!file)return;if(file.size>100*1024*1024){message.textContent='El archivo supera 100 MB.';return;}
    button.disabled=true;message.textContent='Guardando recurso…';
    try { await api('/api/projects/'+project+'/resources',{method:'POST',body:new FormData(e.target)});if(project===currentProject && epoch===viewEpoch){await render();toast('Recurso guardado en el proyecto','ok');} }
    catch(x){if(message.isConnected){message.textContent=x.message;button.disabled=false;}}
  };
}
