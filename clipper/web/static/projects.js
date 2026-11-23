/* Shared project navigation. The API remains backwards compatible when no scope is supplied. */
let projects = [], currentProject = 'project_default', projectLoad = 0;
try { currentProject = localStorage.getItem('clipper.project') || currentProject; } catch {}
const projectName = () => projects.find(p => p.id === currentProject)?.name || 'General';
function projectUrl(url, opt) {
  const [path] = url.split('?');
  if (!opt?.allProjects && (!opt?.method || opt.method === 'GET') && ['/api/sources', '/api/jobs', '/api/clips', '/api/queue', '/api/runs'].includes(path))
    return url + (url.includes('?') ? '&' : '?') + 'project_id=' + encodeURIComponent(currentProject);
  return url;
}
async function loadProjects() {
  const ticket = ++projectLoad, result = await api('/api/projects');
  if (ticket !== projectLoad) return;
  projects = result;
  if (!projects.some(p => p.id === currentProject)) currentProject = projects[0]?.id || 'project_default';
  paintProjects();
}
function paintProjects() {
  $('#projectNav').innerHTML = `<div class="project-label"><label for="projectSelect">Proyecto actual</label><button class="b g ic" onclick="editProject()" aria-label="Nuevo proyecto" title="Nuevo proyecto">${icon('plus')}</button></div><select id="projectSelect" aria-label="Proyecto actual">${projects.map(p => `<option value="${esc(p.id)}" ${p.id === currentProject ? 'selected' : ''}>${esc(p.name)}</option>`).join('')}</select><button class="b g ic" id="projectQuick" title="Proyecto: ${esc(projectName())}" aria-label="Cambiar proyecto: ${esc(projectName())}" onclick="chooseProject()">${icon('folder')}</button>`;
  $('#projectSelect').onchange = e => switchProject(e.target.value);
  Shell.sync();
}
function switchProject(id, destination) {
  if (!projects.some(p => p.id === id)) return;
  viewEpoch++;
  currentProject = id;
  try { localStorage.setItem('clipper.project', id); } catch {}
  clearInterval(timer); closeModal(); paintProjects();
  loadingView();
  if (destination || ['settings', 'projects', 'editor', 'video-editor'].includes(tab) || location.hash.includes('/select/')) go(destination || 'dashboard');
  else render().catch(showViewError);
}
async function vProjects() {
  const epoch = viewEpoch;
  await loadProjects();
  if (epoch !== viewEpoch || tab !== 'projects') return;
  $('#main').innerHTML = `<div class="page">${head('Proyectos', 'Cada proyecto reúne sus videos, generaciones y clips.', `<button class="b" onclick="editProject()">${icon('plus')}Nuevo proyecto</button>`)}<div class="project-grid">${projects.map(p => `<article class="card project-card ${p.id === currentProject ? 'selected' : ''}"><div class="project-title">${icon('film')}<h2 data-ui-content>${esc(p.name)}</h2>${p.id === currentProject ? '<span class="pill ok">Actual</span>' : ''}</div><p class="mut" ${p.description?'data-ui-content':''}>${esc(p.description || 'Sin descripción')}</p><div class="project-counts"><span><b>${p.source_count || 0}</b> videos</span><span><b>${p.job_count || 0}</b> generaciones</span><span><b>${p.clip_count || 0}</b> clips</span><span><b>${p.resource_count || 0}</b> recursos</span></div><div class="project-actions">${cfg.desktop ? `<button class="b g" onclick="switchProject('${esc(p.id)}','video-projects')">Montajes de video</button>` : ''}<button class="b" onclick="switchProject('${esc(p.id)}')">Abrir proyecto</button><button class="b g" onclick="editProject('${esc(p.id)}')">Editar</button>${p.id!=='project_default'?`<button class="b g er" onclick="deleteLibrary('projects','${p.id}')" aria-label="Delete project">${icon('trash')}Delete</button>`:''}</div></article>`).join('')}</div></div>`;
}
function editProject(id) {
  const p = projects.find(p => p.id === id);
  modal(`<h2>${p ? 'Editar' : 'Nuevo'} proyecto</h2><form id="projectForm"><label for="projectName">Nombre</label><input id="projectName" required maxlength="80" value="${esc(p?.name || '')}" autofocus><label for="projectDescription">Descripción</label><textarea id="projectDescription" maxlength="600" rows="3">${esc(p?.description || '')}</textarea><p class="mut sm">Los videos y sus generaciones se organizan juntos.</p><button class="b">Guardar proyecto</button><p id="projectError" class="mut sm" role="alert"></p></form>`);
  $('#projectForm').onsubmit = async e => {
    e.preventDefault(); const form = e.target, button = $('button', form), error = $('#projectError'); button.disabled = true;
    try {
      const r = await send(p ? 'PUT' : 'POST', p ? '/api/projects/' + p.id : '/api/projects', { name: $('#projectName').value.trim(), description: $('#projectDescription').value.trim() });
      await loadProjects();
      if (!form.isConnected) { toast('Proyecto guardado', 'ok'); return; }
      closeModal();
      if (!p) { currentProject = r.id; try { localStorage.setItem('clipper.project', r.id); } catch {} paintProjects(); loadingView(); }
      await render(); toast('Proyecto guardado', 'ok');
    } catch (x) { if (error.isConnected) { error.textContent = x.message; button.disabled = false; } }
  };
}
async function moveSource(id) {
  await loadProjects();
  modal(`<h2>Mover video</h2><p class="mut sm">También se moverán sus generaciones y clips. Espera a que terminen los trabajos activos antes de moverlo.</p><label for="moveProject">Proyecto de destino</label><select id="moveProject">${projects.map(p => `<option value="${esc(p.id)}" ${p.id === currentProject ? 'selected' : ''}>${esc(p.name)}</option>`).join('')}</select><button class="b" id="moveSource" style="margin-top:16px">Mover video y generaciones</button><p id="moveError" class="mut sm" role="alert"></p>`);
  $('#moveSource').onclick = async e => {
    e.target.disabled = true;
    try { await send('PUT', `/api/sources/${id}/project`, { project_id: $('#moveProject').value }); closeModal(); await loadProjects(); await render(); toast('Video y generaciones movidos', 'ok'); }
    catch (x) { $('#moveError').textContent = x.message; e.target.disabled = false; }
  };
}
