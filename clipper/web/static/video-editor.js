/* Standalone compositions: a project can contain several independent videos. */
async function vVideoProjects() {
  const project=currentProject,epoch=viewEpoch;
  const rows=await api(`/api/projects/${project}/videos`);
  if(project!==currentProject || epoch!==viewEpoch)return;
  $('#main').innerHTML=`<div class="page">${head('Montajes de video','Crea y edita videos completos dentro de este proyecto.',`<button class="b" id="newVideo">${icon('plus')}Crear video</button>`)}<div class="project-grid">${rows.map(v=>`<article class="card"><h2 data-ui-content>${esc(v.name)}</h2><p data-ui-content>${v.settings.width} × ${v.settings.height} · ${v.settings.fps} fps · ${v.settings.format.toUpperCase()}</p><p data-ui-content>${fmtDur(v.document.duration)} · ${v.document.items.length} recursos</p><button class="b" data-open-video="${v.id}">Abrir editor</button><button class="b g er" data-delete-video="${v.id}">${icon('trash')}Eliminar</button></article>`).join('')||empty('film','Crea tu primer montaje de video.')}</div></div>`;
  $('#newVideo').onclick=()=>newVideoProject(project);
  $$('[data-open-video]').forEach(b=>b.onclick=()=>go('video-editor/'+b.dataset.openVideo));
  $$('[data-delete-video]').forEach(b=>b.onclick=()=>{
    modal(`<h2>Eliminar montaje</h2><p>El montaje se moverá a la papelera recuperable.</p><button class="b er" id="confirmVideoDelete">Eliminar</button>`);
    $('#confirmVideoDelete').onclick=async()=>{try{await send('DELETE','/api/library/video_projects/'+b.dataset.deleteVideo);closeModal();await vVideoProjects();}catch(e){toast(e.message,'er');}};
  });
}
function newVideoProject(project=currentProject) {
  modal(`<h2>Crear video</h2><p class="hint">Empieza con un editor vacío. Puedes importar contenido después.</p><form id="newVideoForm"><label>Nombre del video</label><input id="videoName" required maxlength="100" autofocus><div class="row c2"><div><label>Formato de archivo</label><select id="videoFormat"><option value="mp4">MP4</option><option value="mov">MOV</option></select></div><div><label>Proporción</label><select id="videoRatio">${['16:9','9:16','1:1','4:5','21:9'].map(x=>`<option>${x}</option>`).join('')}</select></div><div><label>Resolución</label><select id="videoResolution"><option value="720">720p</option><option value="1080" selected>1080p</option><option value="2160">2160p</option></select></div><div><label>FPS</label><select id="videoFps">${[24,25,30,50,60].map(x=>`<option ${x===30?'selected':''}>${x}</option>`).join('')}</select></div></div><label>Calidad</label><select id="videoQuality"><option value="draft">Borrador</option><option value="standard" selected>Estándar</option><option value="high">Alta</option></select><p class="hint">La creación del montaje y la edición manual no usan la API.</p><button class="b" type="submit">Crear y abrir editor</button><p id="videoCreateError" role="alert"></p></form>`);
  $('#newVideoForm').onsubmit=async e=>{
    e.preventDefault();const button=$('button',e.target);button.disabled=true;
    try{
      const video=await post(`/api/projects/${project}/videos`,{name:$('#videoName').value,settings:{format:$('#videoFormat').value,ratio:$('#videoRatio').value,resolution:+$('#videoResolution').value,fps:+$('#videoFps').value,quality:$('#videoQuality').value}});
      closeModal();go('video-editor/'+video.id);
    }catch(x){button.disabled=false;$('#videoCreateError').textContent=x.message;}
  };
}

const VideoEditor=(()=>{
  let V=null,epoch=0,raf=0,timer=0,observer=null;
  const clone=x=>JSON.parse(JSON.stringify(x)),clamp=(x,a,b)=>Math.max(a,Math.min(b,x));
  function time(seconds,forInput=false) {
    const precision=forInput?4:2,scale=10**precision,ticks=Math.round(seconds*scale);
    const hours=Math.floor(ticks/(3600*scale)),minutes=Math.floor(ticks/(60*scale))%60;
    let tail=(ticks%(60*scale)/scale).toFixed(precision).padStart(3+precision,'0');
    if(forInput)tail=tail.replace(/\.?0+$/,'');
    return forInput||hours?`${hours}:${String(minutes).padStart(2,'0')}:${tail}`:`${minutes}:${tail}`;
  }
  function durationSeconds(value) {
    const parts=value.trim().split(':');
    if(parts.length===1)return /^\d+(?:\.\d+)?$/.test(parts[0])?Number(parts[0]):NaN;
    if(parts.length>3||!parts.slice(0,-1).every(part=>/^\d+$/.test(part))||
      !/^\d{1,2}(?:\.\d+)?$/.test(parts.at(-1))||Number(parts.at(-1))>=60||
      (parts.length===3&&Number(parts[1])>=60))return NaN;
    return parts.reduce((seconds,part)=>seconds*60+Number(part),0);
  }
  const durationText=seconds=>time(seconds,true);
  const asset=id=>V.assets.find(a=>a.id===id);
  const selected=()=>V.doc.items.find(x=>x.id===V.selected);
  function changed(fn) {
    V.undo.push(clone(V.doc));if(V.undo.length>100)V.undo.shift();V.redo=[];fn(V.doc);V.change++;V.proposal=null;
    schedule();drawTimeline();inspector();transport();
  }
  function schedule(){clearTimeout(timer);status('Cambios sin guardar');timer=setTimeout(()=>save().catch(x=>status(x.message)),900);}
  function status(text){if(V && $('#veSave'))$('#veSave').textContent=text;}
  async function save(){
    if(!V)return;
    const me=V;
    if(V.saving){await V.saving;if(V===me && V.saved!==V.change)return save();return;}
    if(V.saved===V.change)return;
    const change=V.change,document=clone(V.doc);status('Guardando…');
    const task=send('PUT','/api/video-projects/'+V.id,{revision:V.revision,document});V.saving=task;
    try{const result=await task;if(V!==me)return;V.revision=result.revision;V.saved=change;status(V.saved===V.change?'Guardado':'Cambios sin guardar');}
    finally{if(V===me)V.saving=null;}
    if(V===me && V.saved!==V.change)return save();
  }
  async function open(id){
    close();const ticket=++epoch;
    try{
      const record=await api('/api/video-projects/'+id);if(ticket!==epoch)return;
      V={...record,doc:clone(record.document),assets:record.assets,revision:record.revision,selected:null,t:0,playing:false,last:performance.now(),zoom:clamp((window.innerWidth-48)/record.document.duration,.02,160),undo:[],redo:[],change:0,saved:0,proposal:null,saving:null,aiBusy:false};
      mount();raf=requestAnimationFrame(loop);
    }catch(e){if(ticket===epoch){toast(e.message,'er');go('video-projects');}}
  }
  function close(){
    epoch++;clearTimeout(timer);cancelAnimationFrame(raf);observer?.disconnect();observer=null;
    if(!V)return;
    if(V.saved!==V.change){
      const me=V,snapshot=clone(V.doc),pending=V.saving;
      Promise.resolve(pending).then(result=>send('PUT','/api/video-projects/'+me.id,{revision:result?.revision??me.revision,document:snapshot})).catch(e=>toast(e.message,'er'));
    }
    $$('#veCanvas video,#veCanvas audio').forEach(el=>el.pause());
    V.music?.pause();V.sounds?.forEach(x=>x.pause());V.audio?.close();document.removeEventListener('keydown',onKey);document.removeEventListener('pointerdown',dismissContext);V=null;$('#editor').innerHTML='';
  }
  function mount(){
    const s=V.settings;
    $('#editor').innerHTML=`<div class="ve"><header class="ve-top"><button class="b g sm" id="veBack">${icon('back')}Montajes</button><strong data-ui-content>${esc(V.name)}</strong><span class="mut sm" data-ui-content>${s.width} × ${s.height} · ${s.fps} fps · ${s.format.toUpperCase()}</span><span class="sp"></span><span class="mut sm" id="veSave">Guardado</span><button class="b g sm" id="veUndo" title="Deshacer">${icon('undo')}</button><button class="b g sm" id="veRedo" title="Rehacer">${icon('redo')}</button><button class="b" id="veExport">Exportar video</button></header><div class="ve-work"><aside class="ve-sidebar"><div class="ve-assets"><h3>Recursos del proyecto</h3><label class="b g sm" for="veImport">${icon('plus')}Importar contenido</label><input class="sr-only" id="veImport" type="file" accept=".mp4,.mov,.webm,.png,.jpg,.jpeg,.webp,.gif,.mp3,.wav,.m4a,.ogg,.flac"><p class="hint">Añade un recurso al cursor para empezar.</p><div id="veAssets"></div></div><button class="ve-ai-button" id="veAiButton" aria-label="Asistente de edición con IA"><img src="/static/brand/mark-white.svg" alt="">Asistente de IA</button></aside><section class="ve-center"><div class="ve-preview" id="vePreview"><div class="ve-canvas" id="veCanvas"></div><p class="ve-empty" id="veEmpty">Tu video empieza aquí</p></div><div class="ve-transport"><button class="b g ic" id="vePlay" aria-label="Reproducir o pausar">${icon('play')}</button><span id="veTime" data-ui-content></span><input id="veSeek" type="range" min="0" max="${V.doc.duration}" step="${1/s.fps}" value="0" aria-label="Posición del cursor"></div></section><aside class="ve-inspector" id="veInspector"></aside></div><section class="ve-timeline"><div class="ve-tltools"><b>Línea de tiempo</b><button class="b g sm" id="veSplit">Dividir</button><button class="b g sm" id="veRemove">Eliminar</button><span class="sp"></span><label>Duración<input id="veDuration" type="text" autocomplete="off" spellcheck="false" title="Segundos, M:SS o H:MM:SS" value="${durationText(V.doc.duration)}"></label><label>Zoom<input id="veZoom" type="range" min="0.02" max="160" step="0.02" value="${V.zoom}"></label></div><div class="ve-scroll" id="veScroll"><div id="veTracks"></div></div></section><section class="ve-ai-panel" id="veAiPanel" hidden><h3>Asistente de IA</h3><p class="hint">Cada solicitud usa tu API y puede generar cargos, incluso si descartas la propuesta. Los reintentos del proveedor también consumen tokens.</p><label>Instrucción</label><textarea id="vePrompt" rows="3" maxlength="2000" placeholder="Recorta el inicio o baja el volumen del recurso seleccionado…"></textarea><label>Modelo</label><select id="veTier"><option value="fast">Rápido</option><option value="standard">Estándar</option><option value="superior">Superior</option></select><button class="b" id="veAsk">Generar propuesta con API</button><p role="status" id="veAiStatus"></p><div id="veProposal"></div></section></div>`;
    $('#veBack').onclick=async()=>{try{await save();go('video-projects');}catch(e){toast(e.message,'er');}};
    $('#veUndo').onclick=()=>history(false);$('#veRedo').onclick=()=>history(true);
    $('#vePlay').onclick=()=>{V.playing=!V.playing;if(V.t>=V.doc.duration)V.t=0;V.last=performance.now();transport();};
    $('#veSeek').oninput=e=>{V.t=+e.target.value;transport();};
    $('#veZoom').oninput=e=>{V.zoom=+e.target.value;drawTimeline();};
    const durationInput=$('#veDuration');
    durationInput.onchange=()=>{
      const n=durationSeconds(durationInput.value);
      const minimum=Math.max(.1,...V.doc.items.map(x=>x.start+x.duration));
      if(!Number.isFinite(n)||n>86400){
        toast('Usa segundos, M:SS o H:MM:SS (hasta 24 horas).','er');
      }else if(minimum>n+1e-7){
        toast('La duración debe incluir todos los recursos.','er');
      }else if(n!==V.doc.duration){
        changed(d=>{d.duration=n;V.t=Math.min(V.t,n);});
      }
      durationInput.value=durationText(V.doc.duration);
    };
    durationInput.onkeydown=e=>{if(e.key==='Enter')durationInput.blur();};
    $('#veRemove').onclick=remove;$('#veSplit').onclick=split;
    $('#veImport').onchange=e=>importFile(e.target.files[0]);$('#veExport').onclick=exportVideo;
    $('#veAiButton').onclick=()=>{$('#veAiPanel').hidden=!$('#veAiPanel').hidden;};$('#veAsk').onclick=ask;
    const scroll=$('#veScroll'),body=document.createElement('div');body.className='ve-tlbody';scroll.before(body);
    body.innerHTML='<div id="veLaneHeads"><div id="veHeads"></div></div>';body.append(scroll);
    scroll.onscroll=()=>{$('#veHeads').style.transform=`translateY(${-scroll.scrollTop}px)`;};
    $('.ve-tltools').insertAdjacentHTML('afterbegin','<button class="b g sm" id="veCopy" title="Ctrl+C">Copiar</button><button class="b g sm" id="veCut" title="Ctrl+X">Cortar</button><button class="b g sm" id="vePaste" title="Ctrl+V">Pegar</button><button class="b g sm" id="veDuplicate" title="Ctrl+D">Duplicar</button>');
    $('#veCopy').onclick=()=>clipboard('c');$('#veCut').onclick=()=>clipboard('x');$('#vePaste').onclick=()=>clipboard('v');$('#veDuplicate').onclick=()=>clipboard('d');
    $('.ve').oncontextmenu=contextMenu;
    scroll.ondragover=e=>{if(e.dataTransfer.types.includes('application/x-kairos-asset')){e.preventDefault();e.dataTransfer.dropEffect='copy';}};
    scroll.ondrop=e=>{const id=e.dataTransfer.getData('application/x-kairos-asset');if(!asset(id))return;e.preventDefault();const rect=$('#veTracks').getBoundingClientRect();addAsset(id,clamp((e.clientX-rect.left)/V.zoom,0,86400-.05));};
    document.addEventListener('keydown',onKey);
    document.addEventListener('pointerdown',dismissContext);
    observer=new ResizeObserver(fit);observer.observe($('#vePreview'));
    VideoTools.attach({state:()=>V,change:changed,timeline:drawTimeline,inspector,transport,fit,ask,
      commit:()=>{V.change++;V.proposal=null;schedule();}});
    drawAssets();drawTimeline();inspector();transport();fit();
  }
  function fit(){if(!V)return;const p=$('#vePreview'),ratio=V.settings.width/V.settings.height,h=Math.max(40,Math.min(p.clientHeight-32,(p.clientWidth-32)/ratio));Object.assign($('#veCanvas').style,{width:h*ratio+'px',height:h+'px'});}
  function drawAssets(){
    $('#veAssets').innerHTML=V.assets.map(a=>`<button class="ve-asset" data-asset="${esc(a.id)}">${icon(a.kind==='audio'?'music':a.kind==='image'?'frame':'film')}<span data-ui-content>${esc(a.name)}</span>${icon('plus')}</button>`).join('');
    $$('[data-asset]').forEach(b=>{b.onclick=()=>addAsset(b.dataset.asset);b.draggable=true;b.ondragstart=e=>{e.dataTransfer.setData('application/x-kairos-asset',b.dataset.asset);e.dataTransfer.effectAllowed='copy';};});
  }
  function addAsset(id,at=null){
    const a=asset(id);if(!a)return;
    const current=selected(),start=at??(current&&V.t>=current.start&&V.t<current.start+current.duration?current.start+current.duration:V.t),duration=Math.min(a.duration||5,86400-start);
    if(duration<.05)return;
    changed(d=>{const item={id:'item_'+crypto.randomUUID(),asset_id:a.id,start,offset:0,duration,gain:1,fit:'contain',crop_x:.5,crop_y:.5};d.items.push(item);V.toolSelection=null;d.duration=Math.max(d.duration,start+duration);V.selected=item.id;V.t=start;});
  }
  async function importFile(file){
    if(!file)return;const me=V,body=new FormData();body.append('file',file);
    try{status('Importando…');await api(`/api/projects/${V.project_id}/resources`,{method:'POST',body});if(V!==me)return;const result=await api('/api/video-projects/'+V.id);if(V!==me)return;V.assets=result.assets;drawAssets();status('Guardado');}catch(e){if(V===me)toast(e.message,'er');}
  }
  function drawTimeline(){
    const width=Math.max(500,V.doc.duration*V.zoom),step=[1,5,10,30,60,300,600,3600].find(n=>n*V.zoom>=70)||3600;
    let ruler='';for(let t=0;t<=V.doc.duration;t+=step)ruler+=`<span style="left:${t*V.zoom}px" data-ui-content>${time(t)}</span>`;
    $('#veTracks').style.width=width+'px';
    const tools=VideoTools.timeline();let media='',heads=tools.heads;
    for(const [kind,label] of [['video','Video'],['audio','Audio']]){
      const items=V.doc.items.filter(x=>(asset(x.asset_id)?.kind==='audio'?'audio':'video')===kind),packed=EditorCore.lanes(items,x=>[x.start,x.start+x.duration]),h=packed.count*38;
      heads+=`<div style="height:${h}px">${icon(kind==='audio'?'music':'film')}${label}${packed.count>1?` · ${packed.count}`:''}</div>`;
      media+=`<div class="ve-lane ve-media-lane" data-media-kind="${kind}" style="height:${h}px">${items.map((x,i)=>`<button class="ve-item ${V.selected===x.id?'selected':''}" data-item="${x.id}" style="top:${packed.positions[i]*38+4}px;left:${x.start*V.zoom}px;width:${Math.max(14,x.duration*V.zoom)}px" title="${esc(asset(x.asset_id)?.name||x.asset_id)}"><i class="ve-handle" data-edge="l"></i><span data-ui-content>${esc(asset(x.asset_id)?.name||x.asset_id)}</span><i class="ve-handle" data-edge="r"></i></button>`).join('')}</div>`;
    }
    $('#veHeads').innerHTML='<div class="ve-lane-ruler">Pistas</div>'+heads;
    $('#veTracks').innerHTML=`<div class="ve-ruler">${ruler}</div>${tools.html}${media}<div class="ve-playhead" style="left:${V.t*V.zoom}px"></div>`;
    $('#veTracks').onpointerdown=e=>{
      if(VideoTools.pointer(e))return;
      const b=e.target.closest('[data-item]');
      if(!b){const rect=$('#veTracks').getBoundingClientRect();V.t=clamp((e.clientX-rect.left)/V.zoom,0,V.doc.duration);transport();return;}
      V.toolSelection=null;V.selected=b.dataset.item;inspector();const x=selected(),start=x.start,offset=x.offset,length=x.duration,x0=e.clientX,edge=e.target.dataset.edge;let moved=false;
      b.setPointerCapture(e.pointerId);
      b.onpointermove=ev=>{
        if(Math.abs(ev.clientX-x0)<3&&!moved)return;
        if(!moved){V.undo.push(clone(V.doc));V.redo=[];moved=true;}
        const delta=(ev.clientX-x0)/V.zoom;
        if(edge==='l'){
          const next=clamp(VideoTools.snap(start+delta),Math.max(0,start-offset),start+length-.05);
          x.start=next;x.offset=offset+next-start;x.duration=length-(next-start);
        }else if(edge==='r')x.duration=clamp(VideoTools.snap(start+length+delta)-start,.05,Math.min(V.doc.duration-start,(asset(x.asset_id)?.duration||86400)-offset));
        else x.start=clamp(VideoTools.snap(start+delta),0,V.doc.duration-x.duration);
        b.style.left=x.start*V.zoom+'px';b.style.width=Math.max(14,x.duration*V.zoom)+'px';
      };
      b.onpointerup=()=>{b.onpointermove=null;b.onpointerup=null;if(moved){V.change++;V.proposal=null;schedule();}drawTimeline();inspector();transport();};
    };
  }
  function inspector(){
    transport();
    if(VideoTools.inspector())return;
    const x=selected();
    if(!x){$('#veInspector').innerHTML=VideoTools.summary();return;}
    const a=asset(x.asset_id);
    $('#veInspector').innerHTML=`<h3>Propiedades</h3><p data-ui-content>${esc(a?.name||x.asset_id)}</p>${[['start','Inicio en el montaje (s)',0,V.doc.duration-x.duration],['offset','Inicio en el recurso (s)',0,Math.max(0,(a?.duration||86400)-x.duration)],['duration','Duración (s)',.05,Math.min(V.doc.duration-x.start,(a?.duration||86400)-x.offset)],['gain','Volumen',0,3]].map(([k,l,min,max])=>`<label>${l}</label><input data-prop="${k}" type="number" min="${min}" max="${max}" step="${k==='gain'?.05:1/V.settings.fps}" value="${x[k]}">`).join('')}${a?.kind==='audio'?'':`<label>Encuadre</label><select data-prop="fit"><option value="contain" ${x.fit==='contain'?'selected':''}>Completo</option><option value="cover" ${x.fit==='cover'?'selected':''}>Rellenar y recortar</option><option value="blur" ${x.fit==='blur'?'selected':''}>Completo + fondo</option></select>${['crop_x','crop_y'].map(k=>`<label>${k==='crop_x'?'Horizontal':'Vertical'}</label><input type="range" data-prop="${k}" min="0" max="1" step="0.01" value="${x[k]}">`).join('')}`}<p class="hint">Volumen: 1 = 100 %, 0 = silencio.</p>`;
    $$('[data-prop]').forEach(input=>input.onchange=()=>{
      const k=input.dataset.prop,v=k==='fit'?input.value:+input.value;
      if(typeof v==='number' && (!Number.isFinite(v)||v<+input.min||v>+input.max)){toast('Valor fuera de rango.','er');inspector();return;}
      changed(()=>x[k]=v);
    });
  }
  function transport(){
    $('#vePlay').innerHTML=icon(V.playing?'pause':'play');$('#veTime').textContent=time(V.t)+' / '+time(V.doc.duration);
    $('#veSeek').max=V.doc.duration;$('#veSeek').value=V.t;
    const durationInput=$('#veDuration');
    if(document.activeElement!==durationInput)durationInput.value=durationText(V.doc.duration);
    $('#veUndo').disabled=!V.undo.length;$('#veRedo').disabled=!V.redo.length;
    const selection=!!selected()||!!VideoTools.selected();
    for(const id of ['veCopy','veCut','veDuplicate'])$('#'+id).disabled=!(selected()||VideoTools.clipboardInfo());
    for(const id of ['veRemove','veSplit'])$('#'+id).disabled=!selection;
    $('#vePaste').disabled=!EditorCore.has(V.project_id,'composition');
    const head=$('.ve-playhead');if(head)head.style.left=V.t*V.zoom+'px';
  }
  function preview(){
    const container=$('#vePicture');
    // Match export's tolerance for draft times rounded to four decimal places.
    const cutTolerance=.00011,t=V.t+cutTolerance;
    const active=V.doc.items.filter(x=>t>=x.start && x.start+x.duration>t);
    const upcoming=V.doc.items.filter(x=>x.start>t && V.t+.75>=x.start && asset(x.asset_id)?.kind!=='audio')
      .sort((a,b)=>a.start-b.start).slice(0,3);
    const ids=new Set(),pending=[];
    for(const [i,x] of [...active,...upcoming].entries()){
      const a=asset(x.asset_id);if(!a)continue;
      const onTimeline=i<active.length;
      let el=[...container.children].find(el=>el.dataset.id===x.id);
      if(!el){
        el=document.createElement(a.kind==='image'?'img':a.kind==='audio'?'audio':'video');
        el.dataset.id=x.id;el.src=a.url;el.preload='auto';
        if(a.kind!=='image')el.currentTime=x.offset;
        if(a.kind==='video')el.playsInline=true;
        container.append(el);
      }
      ids.add(x.id);
      el._end=x.start+x.duration;
      if(a.kind!=='image'){
        const target=Math.max(0,V.t-x.start)+x.offset;
        const tolerance=onTimeline?.12:1/(2*V.settings.fps);
        if(el.readyState>=1 && !el.seeking && Math.abs(el.currentTime-target)>tolerance)el.currentTime=target;
        if(!onTimeline || !V.playing)el.pause();
      }
      const ready=a.kind==='image'?el.complete && !!el.naturalWidth:el.readyState>=2 && !el.seeking;
      el._visible=onTimeline && a.kind!=='audio' && (ready||el._visible);
      el._holding=false;
      if(onTimeline && a.kind!=='audio' && !el._visible)pending.push(x);
      el.style.cssText=`position:absolute;inset:0;width:100%;height:100%;background:${x.fit==='blur'?'transparent':'#000'};object-fit:${x.fit==='cover'?'cover':'contain'};object-position:${x.crop_x*100}% ${x.crop_y*100}%;z-index:${i*2+1};visibility:${el._visible?'visible':'hidden'};${a.kind==='audio'?'display:none':''}`;
      if(x.fit==='blur' && el._visible){
        let bg=[...container.children].find(n=>n.dataset.id===x.id+'__bg');
        if(!bg){bg=document.createElement('canvas');bg.dataset.id=x.id+'__bg';container.insertBefore(bg,el);}
        ids.add(bg.dataset.id);
        const w=el.videoWidth||el.naturalWidth,h=el.videoHeight||el.naturalHeight;
        if(ready&&w&&h){const rect=container.getBoundingClientRect(),bw=Math.max(2,Math.round(rect.width/4)),bh=Math.max(2,Math.round(rect.height/4));if(bg.width!==bw||bg.height!==bh){bg.width=bw;bg.height=bh;}const k=Math.max(bw/w,bh/h);try{bg.getContext('2d').drawImage(el,(bw-w*k)/2,(bh-h*k)/2,w*k,h*k);}catch{}}
        bg.style.cssText=`position:absolute;inset:0;width:100%;height:100%;z-index:${i*2};filter:blur(9px) brightness(.7) saturate(.85);transform:scale(1.05)`;
      }
      if(onTimeline && a.kind!=='image'){
        // WebAudio supplies gains above 1; HTML volume alone is limited to 100%.
        if(!el._gain){try{V.audio??=new AudioContext();const src=V.audio.createMediaElementSource(el),gain=V.audio.createGain();src.connect(gain).connect(V.audio.destination);el._gain=gain;}catch{}}
        if(el._gain)el._gain.gain.value=V.muted?0:x.gain*V.doc.tools.edl.audio.voice_gain;else el.volume=V.muted?0:clamp(x.gain*V.doc.tools.edl.audio.voice_gain,0,1);
        if(V.playing){V.audio?.resume();if(el.paused)el.play().catch(()=>{});}else if(!el.paused)el.pause();
      }
    }
    // Keep the outgoing picture until its adjacent successor has a decoded frame.
    // A real gap still clears the preview immediately.
    for(const el of [...container.children]){
      if(ids.has(el.dataset.id)||!el._visible)continue;
      if(pending.some(x=>cutTolerance>=Math.abs(x.start-el._end) && (el._holding||.5>=V.t-x.start))){
        el.pause?.();el._holding=true;
        el.style.zIndex=0;
        ids.add(el.dataset.id);
        const bg=[...container.children].find(n=>n.dataset.id===el.dataset.id+'__bg');
        if(bg){bg.style.zIndex=0;ids.add(bg.dataset.id);}
      }
    }
    for(const el of [...container.children])if(!ids.has(el.dataset.id)){el.pause?.();el.remove();}
    $('#veEmpty').hidden=V.doc.items.length>0||V.doc.tools.edl.texts.length>0||V.doc.tools.track.cues.length>0||!!V.doc.tools.edl.hook.text.en;
    VideoTools.preview();
  }
  function loop(now){if(!V)return;raf=requestAnimationFrame(loop);if(V.playing){V.t=Math.min(V.doc.duration,V.t+(now-V.last)/1000);if(V.t>=V.doc.duration)V.playing=false;transport();}V.last=now;preview();}
  function history(redo){const from=redo?V.redo:V.undo,to=redo?V.undo:V.redo;if(!from.length)return;to.push(clone(V.doc));const chat=V.doc.chat;V.doc=from.pop();V.doc.chat=chat;V.toolSelection=null;V.change++;V.proposal=null;V.t=Math.min(V.t,V.doc.duration);schedule();drawTimeline();inspector();transport();VideoTools.panel();}
  function remove(){if(VideoTools.remove())return;if(selected())changed(d=>{d.items=d.items.filter(x=>x.id!==V.selected);V.selected=null;});}
  function split(){
    if(VideoTools.splitCue())return;
    const x=selected();if(!x)return;const at=V.t-x.start;
    if(at<.05||at>x.duration-.05){toast('Sitúa el cursor dentro del recurso.','er');return;}
    changed(d=>{const right={...x,id:'item_'+crypto.randomUUID(),start:V.t,offset:x.offset+at,duration:x.duration-at};x.duration=at;d.items.splice(d.items.indexOf(x)+1,0,right);});
  }
  function clipboard(command){
    const media=selected(),tool=VideoTools.clipboardInfo(),info=media?{kind:'media',value:media,start:media.start,duration:media.duration}:tool;
    if(command!=='v'){
      if(!info)return;
      EditorCore.copy(info.kind,info.value,info.start,V.project_id,'composition');
      if(command==='x'){remove();transport();return;}
      if(command==='c'){transport();return;}
    }
    const at=command==='d'?info.start+info.duration:V.t,entry=EditorCore.read(V.project_id,'composition',at);
    if(!entry)return;
    const x=entry.value,len=entry.kind==='cue'?x.end-x.start:entry.kind==='sfx'?(V.studio.library.find(s=>s.id===x.id)?.duration||.5):x.duration;
    if(at+len>86400||(entry.kind==='media'&&V.doc.items.length>=200)){toast('El elemento no cabe en el rango actual.','er');return;}
    changed(d=>{
      d.duration=Math.max(d.duration,at+len);
      if(entry.kind==='media'){x.id='item_'+crypto.randomUUID();d.items.push(x);V.selected=x.id;V.toolSelection=null;}
      else VideoTools.insertCopied(entry.kind,x);
      V.t=at;
    });
    VideoTools.panel();transport();
  }
  function dismissContext(e){if(!e.target.closest('.ve-menu'))$('.ve-menu')?.remove();}
  function contextMenu(e){
    const item=e.target.closest('[data-item]'),tool=e.target.closest('[data-tool-kind]');if(!item&&!tool&&!e.target.closest('.ve-timeline'))return;
    e.preventDefault();$('.ve-menu')?.remove();
    if(item){V.selected=item.dataset.item;V.toolSelection=null;}
    else if(tool){V.selected=null;V.toolSelection={kind:tool.dataset.toolKind,index:+tool.dataset.toolIndex};}
    else{const rect=$('#veTracks').getBoundingClientRect();V.t=clamp((e.clientX-rect.left)/V.zoom,0,V.doc.duration);}
    drawTimeline();inspector();transport();
    const menu=document.createElement('div');menu.className='ve-menu';menu.setAttribute('role','menu');
    const active=!!selected()||!!VideoTools.selected();
    menu.innerHTML=[['c','Copiar','Ctrl+C',active],['x','Cortar','Ctrl+X',active],['v','Pegar','Ctrl+V',EditorCore.has(V.project_id,'composition')],['d','Duplicar','Ctrl+D',active],['split','Dividir','S',active],['delete','Eliminar','Supr',active]].map(([key,label,shortcut,enabled])=>`<button role="menuitem" data-edit-command="${key}" ${enabled?'':'disabled'}><span>${label}</span><kbd>${shortcut}</kbd></button>`).join('');
    $('.ve').append(menu);menu.style.left=Math.min(e.clientX,window.innerWidth-230)+'px';menu.style.top=Math.min(e.clientY,window.innerHeight-menu.offsetHeight-12)+'px';
    menu.onclick=ev=>{const b=ev.target.closest('[data-edit-command]');if(!b)return;const command=b.dataset.editCommand;menu.remove();if(command==='split')split();else if(command==='delete')remove();else clipboard(command);};
    menu.querySelector('button:not(:disabled)')?.focus();
  }
  function onKey(e){
    if(!V)return;
    if(e.key==='Escape'){$('.ve-menu')?.remove();if(!$('#veAiPanel').hidden)$('#veChatClose').click();return;}
    if(EditorCore.editing(e.target))return;
    const k=e.key.toLowerCase(),mod=e.ctrlKey||e.metaKey;
    if(mod){
      if(['c','x','v','d'].includes(k)){e.preventDefault();clipboard(k);return;}
      if(k==='z'||k==='y'){e.preventDefault();history(k==='y'||e.shiftKey);return;}
      if(k==='s'){e.preventDefault();save().catch(x=>toast(x.message,'er'));return;}
    }
    if(VideoTools.key(e)){e.preventDefault();return;}
    if(k==='delete'||k==='backspace'){e.preventDefault();remove();return;}
    if(k==='s'&&!mod){e.preventDefault();split();return;}
    if(e.code==='Space'){e.preventDefault();$('#vePlay').click();}
    if(k==='home'||k==='end'){e.preventDefault();V.playing=false;V.t=k==='home'?0:Math.max(0,V.doc.duration-1/V.settings.fps);transport();}
    if(['+','=','-'].includes(k)){e.preventDefault();V.zoom=clamp(V.zoom*(k==='-'?.8:1.25),.02,160);$('#veZoom').value=V.zoom;drawTimeline();}
  }
  function contentKey(){return JSON.stringify({duration:V.doc.duration,items:V.doc.items,tools:V.doc.tools});}
  function addMessage(role,content,usage=null){V.doc.chat.push({role,content,usage});if(V.doc.chat.length>40)V.doc.chat.shift();V.change++;schedule();VideoTools.chat();}
  const actionNames={trim:'Recortar recurso',volume:'Ajustar volumen',move:'Mover recurso',delete:'Eliminar recurso',crop:'Cambiar encuadre',split:'Dividir recurso',duration:'Ajustar duración',add_media:'Añadir recurso',add_text:'Crear texto',add_effect:'Añadir efecto',add_sfx:'Añadir sonido',set_tool:'Ajustar herramientas',remove_tool:'Eliminar elemento',update_layer:'Actualizar texto'};
  const toolTabs={captions:'captions',color:'color',audio:'audio',sfx:'audio',transitions:'fx',progress:'fx',effects:'fx',texts:'text',hook:'text'};
  function showAction(op,index,operations){
    if(!V)return;V.playing=false;V.toolSelection=null;
    const item=V.doc.items.find(x=>x.id===op.item_id);
    if(item){V.selected=item.id;V.t=item.start;VideoTools.openTab('resources');}
    else{
      V.selected=null;const kind={add_text:'text',add_effect:'fx',add_sfx:'sfx',update_layer:'text'}[op.action];
      const section=op.section||({text:'texts',fx:'effects',sfx:'sfx'})[kind];
      if(section)VideoTools.openTab(toolTabs[section]||'resources');
      V.t=clamp(op.payload?.t??op.payload?.start??V.t,0,V.doc.duration);
      if(kind){const list=kind==='sfx'?V.doc.tools.edl.audio.sfx:V.doc.tools.edl[section];
        // Sanitizers sort by time; find the inserted object using its actual time.
        const found=list.findIndex(x=>Math.abs(x.t-(op.payload?.t??0))<.001 && (kind!=='text'||x.text?.en===op.payload?.text?.en));
        if(found>=0)V.toolSelection={kind,index:found};
      }
      if(op.action==='add_media'){const added=V.doc.items.find(x=>x.asset_id===op.payload?.asset_id&&Math.abs(x.start-op.payload.start)<.001);if(added){V.selected=added.id;V.t=added.start;}}
    }
    V.t=clamp(V.t,0,Math.max(0,V.doc.duration-1/V.settings.fps));drawTimeline();inspector();transport();preview();VideoTools.preview();
    const target=V.selected?$$('[data-item]').find(x=>x.dataset.item===V.selected):null;
    target?.scrollIntoView({block:'nearest',inline:'center',behavior:'smooth'});
    $('.ve').classList.remove('ve-agent-reveal');void $('.ve').offsetWidth;$('.ve').classList.add('ve-agent-reveal');
    $$('[data-agent-action]').forEach(b=>b.classList.toggle('active',+b.dataset.agentAction===index));
  }
  function actionList(result){
    const root=$('#veAgentActivity');root.innerHTML=`<strong>✓ ${result.operations.length} cambios aplicados</strong><div>${result.operations.map((op,i)=>`<button class="ve-agent-step" data-agent-action="${i}"><span>${i+1}</span><span>${esc(actionNames[op.action]||op.action)}${op.section?' · '+esc(op.section):''}</span>${icon('eye')}</button>`).join('')}</div><small>Selecciona un cambio para verlo. Ctrl+Z deshace la edición completa.</small>`;
    const key=contentKey();$$('[data-agent-action]').forEach(b=>b.onclick=()=>{if(contentKey()!==key){root.textContent='El montaje cambió. El recorrido corresponde a una edición anterior.';return;}showAction(result.operations[+b.dataset.agentAction],+b.dataset.agentAction,result.operations);});
    root.insertAdjacentHTML('beforeend',toolTrace(result));
  }
  function toolTrace(result){return !result.tool_calls?.length?'':`<details class="ve-tool-trace"><summary>Ver herramientas utilizadas</summary><ol>${result.tool_calls.map(call=>`<li><code data-ui-content>${call.is_error?'!':'✓'} ${esc(call.name)}</code>${call.is_error?`<p data-ui-content>${esc(call.result?.error||'')}</p>`:''}</li>`).join('')}</ol></details>`;}
  async function ask(){
    if(V.aiBusy)return;const prompt=$('#vePrompt').value.trim();if(!prompt)return;
    const me=V,baseline=contentKey(),autoApply=$('#veAutoApply').checked;V.aiBusy=true;V.proposal=null;addMessage('user',prompt);$('#vePrompt').value='';$('#veAsk').disabled=true;$('#veTier').disabled=true;$('#veAiStatus').textContent='El agente está preparando tu edición…';$('#veProposal').innerHTML='';$('#veAgentActivity').textContent='Interpretando tu instrucción con el montaje, la selección y las herramientas disponibles…';$('.ve-chat').setAttribute('aria-busy','true');
    try{
      const result=await post('/api/video-projects/'+V.id+'/ai',{request_id:crypto.randomUUID(),prompt,tier:$('#veTier').value,selected_item_id:V.selected,selected_tool:V.toolSelection,cursor:V.t,document:clone(V.doc)});
      if(V!==me)return;
      if(contentKey()!==baseline){addMessage('assistant','El montaje cambió durante la consulta. No se aplicaron los cambios. Puedes repetir la instrucción sobre la edición actual.',result.usage);$('#veAiStatus').textContent='Edición descartada por cambios simultáneos.';$('#veAgentActivity').textContent='Tu edición manual se conservó.';return;}
      V.proposal=result;
      const u=result.usage;$('#veAiStatus').textContent=`${u.model} \u00b7 ${u.input_tokens} + ${u.output_tokens} tokens${u.estimated_usd==null?'':` \u00b7 ~$${u.estimated_usd.toFixed(5)}`}`;
      const apply=()=>{
        if(!V.proposal||contentKey()!==baseline){toast('La propuesta ya no corresponde al montaje actual.','er');return;}
        const next=clone(V.proposal.document);next.chat=V.doc.chat;changed(()=>{V.doc=next;});V.t=clamp(V.t,0,V.doc.duration);VideoTools.panel();$('#veProposal').innerHTML='';actionList(result);showAction(result.operations[0],0,result.operations);addMessage('assistant',result.summary,result.usage);
      };
      if(!result.operations.length){addMessage('assistant',result.summary,result.usage);$('#veAgentActivity').textContent='El agente respondió sin modificar el montaje.';$('#veAgentActivity').insertAdjacentHTML('beforeend',toolTrace(result));V.proposal=null;}
      else if(autoApply)apply();
      else{
        $('#veAgentActivity').textContent='Edición validada. Lista para aplicar.';
        $('#veProposal').innerHTML=`<p data-ui-content>${esc(result.summary)}</p><p>${result.operations.length} cambios</p><button class="b" id="veApply">Aplicar cambios</button><button class="b g" id="veDiscard">Descartar</button>`;
        $('#veApply').onclick=apply;
        $('#veDiscard').onclick=()=>{V.proposal=null;$('#veProposal').innerHTML='';$('#veAgentActivity').textContent='Propuesta descartada.';addMessage('assistant','Propuesta descartada sin cambios en el montaje.',result.usage);};
      }
    }catch(e){if(V===me){addMessage('assistant','No se pudo completar la edición: '+e.message);$('#veAiStatus').textContent=e.message;$('#veAgentActivity').textContent='No se aplicaron cambios del agente.';}}
    finally{if(V===me){V.aiBusy=false;$('#veAsk').disabled=false;$('#veTier').disabled=false;$('.ve-chat').setAttribute('aria-busy','false');}}
  }
  async function exportVideo(){
    if(!V.doc.items.length&&!V.doc.tools.edl.texts.length&&!V.doc.tools.track.cues.length&&!V.doc.tools.edl.hook.text.en&&!V.doc.tools.edl.commentary.notes.length){toast('Añade recursos antes de exportar.','er');return;}
    const me=V;$('#veExport').disabled=true;
    try{
      await save();if(V!==me)return;
      const run=await post('/api/video-projects/'+V.id+'/render');
      while(V===me){await new Promise(r=>setTimeout(r,1200));const state=await api('/api/runs/'+run.run_id);if(V!==me)return;status(state.status==='queued'?'Exportación en cola':'Exportando…');if(state.status==='failed')throw Error(state.error);if(state.status==='done')break;}
      if(V!==me)return;status('Exportación lista');
      modal(`<h2>Video exportado</h2><a class="b" href="/api/video-projects/${V.id}/export" download>Descargar video</a>`);
    }catch(e){if(V===me)toast(e.message,'er');}finally{if(V===me)$('#veExport').disabled=false;}
  }
  return {open,close,save};
})();
