/* Shorts tool catalog and document fields, presented inside the composition workspace. */
const VideoTools=(()=>{
  let C=null,tab='resources',measure=null;
  const get=()=>C.state(),copy=x=>JSON.parse(JSON.stringify(x)),clamp=(x,a,b)=>Math.max(a,Math.min(b,x));
  const edl=()=>get().doc.tools.edl,track=()=>get().doc.tools.track;
  const height=()=>get().studio.height,catalog=()=>get().studio.catalog;
  const tabs=[['resources','Recursos','folder'],['captions','Subtítulos','cc'],['text','Texto','text'],['fx','Efectos','spark'],['color','Color','drop'],['audio','Audio','music'],['format','Formato','frame']];
  const labels={text:'Texto',note:'Nota',cue:'Subtítulos',fx:'Efectos',sfx:'Sonido',hook:'Gancho'};
  const list=k=>({text:edl().texts,note:edl().commentary.notes,cue:track().cues,fx:edl().effects,sfx:edl().audio.sfx})[k];
  const selected=()=>{const s=get().toolSelection;return s?(s.kind==='hook'?edl().hook:list(s.kind)?.[s.index]):null;};
  const duration=(k,x)=>k==='cue'?x.end-x.start:k==='sfx'?(get().studio.library.find(e=>e.id===x.id)?.duration||.5):x.duration;
  const start=(k,x)=>k==='cue'?x.start:k==='hook'?0:x.t;
  function edit(fn,soft=false){C.change(fn);if(!soft)panel();}
  function attach(context){
    C=context;tab='resources';const V=get();V.snap=true;V.muted=false;V.showSafe=true;
    const root=$('.ve'),work=$('.ve-work'),sidebar=$('.ve-sidebar'),assets=$('.ve-assets'),oldChat=$('#veAiPanel'),inspector=$('#veInspector');
    assets.id='veResources';assets.classList.add('ve-tool-body');
    const controls=document.createElement('div');controls.id='veControls';controls.className='ve-tool-body';controls.hidden=true;sidebar.append(controls);
    const nav=document.createElement('nav');nav.className='ve-toolbar';nav.setAttribute('aria-label','Herramientas del editor');
    nav.innerHTML=`<div class="ve-tool-tabs" role="tablist">${tabs.map(([id,label,i])=>`<button class="b g sm" id="veTool_${id}" role="tab" data-tool-tab="${id}" aria-selected="${id===tab}">${icon(i)}${label}</button>`).join('')}</div><span class="sp"></span><button class="b g sm" id="veSnap" title="Imán">${icon('magnet')}</button><button class="b g sm" id="veGuide">${icon('eye')}Zona segura</button><button class="b g sm" id="vePropsToggle">Propiedades</button><button class="b g sm" id="veChatToggle">${icon('spark')}Chat IA</button>`;
    sidebar.prepend(nav);
    const right=document.createElement('aside');right.className='ve-right';work.append(right);right.append(inspector);root.append(oldChat);
    oldChat.className='ve-chat';oldChat.hidden=true;oldChat.setAttribute('role','region');oldChat.setAttribute('aria-label','Chat de edición con IA');
    oldChat.innerHTML=`<header class="ve-chat-heading"><img src="/static/brand/mark-white.svg" alt=""><h3>Chat de edición con IA</h3></header><p class="hint ve-chat-billing">Cada solicitud usa tu API y puede generar cargos, incluso si descartas la propuesta. Los reintentos del proveedor también consumen tokens.</p><div id="veChatMessages" class="ve-chat-messages" role="log" aria-live="polite"></div><div id="veProposal"></div><div class="ve-chat-composer"><textarea id="vePrompt" rows="2" maxlength="2000" placeholder="Escribe una instrucción para editar el video…"></textarea><div><select id="veTier" aria-label="Modelo"><option value="fast">Rápido</option><option value="standard">Estándar</option><option value="superior">Superior</option></select><button class="b sm" id="veAsk">Enviar</button></div><p role="status" id="veAiStatus"></p></div>`;
    $('#veAsk').onclick=C.ask;
    $('#veTier').innerHTML=(cfg.tiers||[]).map(t=>`<option value="${esc(t.id)}" ${t.model?'':'disabled'}>${esc(I18n.t(t.name))} · ${esc(t.model||I18n.t('Sin configurar'))}</option>`).join('')||'<option value="fast">Sin modelos configurados</option>';
    const remembered=localStorage.getItem('clipper.editor.tier');
    if([...$('#veTier').options].some(o=>o.value===remembered&&!o.disabled))$('#veTier').value=remembered;
    $('#veTier').onchange=()=>localStorage.setItem('clipper.editor.tier',$('#veTier').value);
    $('.ve-chat-heading').insertAdjacentHTML('afterend',`<div class="ve-agent-mode"><span class="ve-agent-dot"></span><span>Agente de edición</span><small data-ui-content>${esc(cfg.provider?.name||'')}</small></div>`);
    $('#veProposal').insertAdjacentHTML('beforebegin','<div id="veAgentActivity" class="ve-agent-activity" aria-live="polite"></div>');
    $('.ve-chat-composer').insertAdjacentHTML('afterbegin','<label class="ve-agent-auto"><input id="veAutoApply" type="checkbox" checked>Aplicar cambios automáticamente</label>');
    $('#vePrompt').placeholder=I18n.t('Escribe una instrucción para editar el video…');
    $('#vePrompt').onkeydown=e=>{if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();C.ask();}};
    root.append($('#veAiButton'));
    $('#veAiButton').setAttribute('aria-controls','veAiPanel');$('#veAiButton').setAttribute('aria-expanded','false');
    const toggleChat=()=>{oldChat.hidden=!oldChat.hidden;$('#veAiButton').setAttribute('aria-expanded',String(!oldChat.hidden));if(!oldChat.hidden)$('#vePrompt').focus();};
    $('#veAiButton').onclick=toggleChat;$('#veChatToggle').onclick=toggleChat;
    $('.ve-chat-heading').insertAdjacentHTML('beforeend',`<button class="b g ic" id="veChatClose" aria-label="Cerrar chat">${icon('x')}</button>`);
    $('#veChatClose').onclick=toggleChat;
    $$('[data-tool-tab]').forEach(b=>b.onclick=()=>openTab(b.dataset.toolTab));
    $('#veSnap').onclick=()=>{V.snap=!V.snap;$('#veSnap').classList.toggle('on',V.snap);};$('#veSnap').classList.add('on');
    $('#veGuide').onclick=()=>{V.showSafe=!V.showSafe;$('#veGuide').classList.toggle('on',V.showSafe);};$('#veGuide').classList.add('on');
    $('#vePropsToggle').onclick=()=>{inspector.hidden=!inspector.hidden;rightLayout();};
    const tools=$('.ve-tltools');tools.insertAdjacentHTML('afterbegin',`<button class="b g sm" id="veAddText">${icon('plus')}Texto</button><button class="b g sm" id="veAddCue">${icon('plus')}Subtítulos</button><button class="b g sm" id="veAddNote">${icon('plus')}Nota</button><button class="b g sm" id="veAddFx">${icon('plus')}Zoom</button><button class="b g sm" id="veFitTimeline" title="Ver todo el clip">${icon('fit')}</button>`);
    $('#veAddText').onclick=()=>add('text');$('#veAddCue').onclick=()=>add('cue');$('#veAddNote').onclick=()=>add('note');$('#veAddFx').onclick=()=>add('fx');
    $('#veFitTimeline').onclick=()=>{V.zoom=clamp(($('#veScroll').clientWidth-40)/V.doc.duration,.02,160);$('#veZoom').value=V.zoom;C.timeline();};
    $('.ve-transport').insertAdjacentHTML('beforeend',`<button class="b g sm" id="veFrameBack" title="Un fotograma atrás">−1f</button><button class="b g sm" id="veFrameNext" title="Un fotograma adelante">+1f</button><button class="b g sm" id="veMute" title="Silenciar">${icon('music')}</button>`);
    $('#veFrameBack').onclick=()=>seek(V.t-1/V.settings.fps);$('#veFrameNext').onclick=()=>seek(V.t+1/V.settings.fps);$('#veMute').onclick=()=>{V.muted=!V.muted;$('#veMute').classList.toggle('on',V.muted);};
    const canvas=$('#veCanvas'),picture=document.createElement('div');picture.id='vePicture';picture.className='ve-picture';canvas.append(picture);
    canvas.insertAdjacentHTML('beforeend',`<svg width="0" height="0" style="position:absolute"><defs><filter id="veColorMatrix" color-interpolation-filters="sRGB"><feColorMatrix id="veColorValues" type="matrix" values="1 0 0 0 0 0 1 0 0 0 0 0 1 0 0 0 0 0 1 0"/></filter><filter id="veGlitch" color-interpolation-filters="sRGB"><feColorMatrix in="SourceGraphic" type="matrix" values="1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 1 0"/><feOffset id="veGlR" dx="-14" result="r"/><feColorMatrix in="SourceGraphic" type="matrix" values="0 0 0 0 0 0 0 0 0 0 0 0 1 0 0 0 0 0 1 0"/><feOffset id="veGlB" dx="14" result="b"/><feColorMatrix in="SourceGraphic" type="matrix" values="0 0 0 0 0 0 1 0 0 0 0 0 0 0 0 0 0 0 1 0" result="g"/><feBlend in="r" in2="g" mode="screen" result="rg"/><feBlend in="rg" in2="b" mode="screen"/></filter></defs></svg>`);
    canvas.insertAdjacentHTML('beforeend','<div id="veOverlays" class="ve-overlays"></div><div id="veSafeGuide" class="ve-safe-guide"></div><div id="veVideoFade" class="ve-video-fade"></div>');
    canvas.addEventListener('pointerdown',dragOverlay);
    canvas.addEventListener('pointerdown',dragFrame);
    measure=document.createElement('canvas').getContext('2d');loadFonts();panel();chat();
  }
  function rightLayout(){$('.ve').classList.toggle('ve-hide-right',$('#veInspector').hidden);C.fit();}
  function seek(t){get().playing=false;get().t=clamp(t,0,get().doc.duration);C.transport();}
  function openTab(id){tab=id;$('#veResources').hidden=id!=='resources';$('#veControls').hidden=id==='resources';$$('[data-tool-tab]').forEach(b=>b.setAttribute('aria-selected',String(b.dataset.toolTab===id)));panel();}
  function loadFonts(){
    let style=$('#veFontStyles');if(!style){style=document.createElement('style');style.id='veFontStyles';document.head.append(style);}
    style.textContent=Object.keys(get().studio.fonts).map(id=>`@font-face{font-family:VE_${id};src:url(/api/fonts/${id});font-weight:100 900;font-display:swap}`).join('');
    for(const id of Object.keys(get().studio.fonts))document.fonts.load(`400 40px VE_${id}`).catch(()=>{});
  }
  function templates(){try{return JSON.parse(localStorage.getItem('clipper.composition.safe.templates')||'[]');}catch{return [];}}
  const field=(path,label,value,min,max,step=.01)=>`<label>${label}<input type="range" data-tool-path="${path}" min="${min}" max="${max}" step="${step}" value="${value}"><output>${Number(value).toFixed(step<1?2:0)}</output></label>`;
  const selectField=(path,label,value,options)=>`<label>${label}<select data-tool-path="${path}">${options.map(([k,l])=>`<option value="${k}" ${k===value?'selected':''}>${esc(l)}</option>`).join('')}</select></label>`;
  function read(path){return path.split('.').reduce((o,k)=>o?.[k],get().doc.tools);}
  function write(path,value){const keys=path.split('.'),key=keys.pop(),target=keys.reduce((o,k)=>o[k],get().doc.tools);target[key]=value;}
  function panel(){
    if(!C||!get()||tab==='resources')return;
    const e=edl(),s=get().studio,c=catalog(),z=e.safe_area,locked=z.locked!==false,max=Math.max(0,Math.floor((height()-300)/2));let html='';
    if(tab==='captions'){
      html=`<h3>Subtítulos</h3>${selectField('edl.captions.style','Estilo',e.captions.style,Object.entries(c.caption_styles).map(([k,v])=>[k,v.label]))}${field('edl.captions.scale','Tamaño',e.captions.scale,.5,1.6,.05)}${field('edl.captions.y','Altura',e.captions.y??.75,.1,.9)}<button class="b g sm" data-add="cue">${icon('plus')}Añadir</button>${entries('cue')}`;
    }else if(tab==='text'){
      html=`<h3>Añadir texto</h3><div class="ve-options">${c.text_presets.map(p=>`<button class="b g sm" data-preset="${p.id}">${esc(p.label)}</button>`).join('')}</div><h4>Textos del clip</h4>${entries('text')}<h4>Gancho</h4><textarea data-tool-path="edl.hook.text.en" maxlength="120">${esc(e.hook.text.en||'')}</textarea>${field('edl.hook.duration','Duración',e.hook.duration,0,Math.min(8,get().doc.duration),.1)}<button class="b g sm" data-select-kind="hook">Propiedades</button><h4>Notas de comentario</h4><button class="b g sm" data-add="note">${icon('plus')}Añadir</button>${entries('note')}`;
    }else if(tab==='fx'){
      html=`<h3>Añadir efecto</h3><div class="ve-options">${c.effects.map(f=>`<button class="b g sm" data-effect="${f.id}" title="${esc(f.desc)}">${esc(f.label)}</button>`).join('')}</div>${entries('fx')}<h4>Transiciones</h4>${selectField('edl.transitions.intro','Entrada',e.transitions.intro,c.transitions.intro)}${field('edl.transitions.intro_d','Duración',e.transitions.intro_d,.1,2,.05)}${selectField('edl.transitions.outro','Salida',e.transitions.outro,c.transitions.outro)}${field('edl.transitions.outro_d','Duración',e.transitions.outro_d,.1,2,.05)}<h4>Barra de progreso</h4><label class="tog"><input type="checkbox" data-tool-path="edl.progress.on" ${e.progress.on?'checked':''}>Mostrar una barra que se llena con el clip</label><input type="color" data-tool-path="edl.progress.color" value="${e.progress.color}">${selectField('edl.progress.position','Posición',e.progress.position,[['top','Arriba'],['bottom','Abajo']])}${field('edl.progress.height','Grosor',e.progress.height,4,40,1)}`;
    }else if(tab==='color'){
      html=`<h3>Color</h3>${selectField('edl.color.look','Filtros',e.color.look,c.looks.map(l=>[l.id,l.label]))}${field('edl.color.look_amount','Intensidad',e.color.look_amount,0,1)}${[['brightness','Brillo',-1],['contrast','Contraste',-1],['saturation','Saturación',-1],['temperature','Temperatura',-1],['tint','Matiz',-1],['fade','Velado',0],['vignette','Viñeta',0]].map(([k,l,min])=>field('edl.color.'+k,l,e.color[k]||0,min,1)).join('')}<button class="b g sm" data-reset-color>Restablecer</button>`;
    }else if(tab==='audio'){
      const a=e.audio,library=s.library;
      html=`<h3>Mezcla</h3>${field('edl.audio.voice_gain','Voz',a.voice_gain,0,3,.05)}<label class="tog"><input type="checkbox" data-tool-path="edl.audio.voice_enhance" ${a.voice_enhance?'checked':''}>Realzar la voz (quita graves y nivela)</label><p class="hint">El realce se aplica en el render, no en la vista en vivo.</p>${selectField('edl.audio.music_id','Música',a.music_id||'',[['','Sin música'],...library.filter(x=>x.kind==='music').map(x=>[x.id,x.description])])}${field('edl.audio.music_gain','Volumen',a.music_gain,0,3,.05)}<label class="tog"><input type="checkbox" data-tool-path="edl.audio.duck_music" ${a.duck_music?'checked':''}>Bajar la música cuando alguien habla</label>${field('edl.audio.normalize_lufs','Volumen final',a.normalize_lufs,-24,-8,1)}<p class="hint">La normalización se aplica en el render.</p><h4>Efectos de sonido</h4>${library.filter(x=>x.kind==='sfx').map(x=>`<div class="ve-sound"><button class="b g sm" data-hear="${x.id}" title="Escuchar">${icon('play')}</button><span data-ui-content>${esc(x.description)}</span><button class="b g sm" data-sound="${x.id}" title="Añadir">${icon('plus')}</button></div>`).join('')}${entries('sfx')}`;
    }else{
      html=`<h3>Formato</h3><p data-ui-content>${get().settings.width} × ${get().settings.height} · ${get().settings.fps} fps</p><h4>Zona segura</h4><button class="b g sm" id="veSafeLock">${icon(locked?'lock':'unlock')}${locked?'Desbloquear':'Bloquear'}</button>${['left','right','top','bottom'].map(k=>`<fieldset ${locked?'disabled':''}>${field('edl.safe_area.'+k,({left:'Izquierda',right:'Derecha',top:'Arriba',bottom:'Abajo'})[k],z[k],0,k==='left'||k==='right'?300:max,1)}</fieldset>`).join('')}<label>Plantillas guardadas<select id="veSafeTemplate" ${locked?'disabled':''}><option value="">Selecciona una plantilla</option>${templates().map((x,i)=>`<option value="${i}">${esc(x.name)}</option>`).join('')}</select></label><input id="veSafeName" maxlength="80" placeholder="Nombre de la plantilla" ${locked?'disabled':''}><button class="b g sm" id="veSaveSafe" ${locked?'disabled':''}>Guardar plantilla</button><button class="b g sm" id="veResetSafe" ${locked?'disabled':''}>Centrar zona</button><p class="hint">Selecciona un recurso en la línea de tiempo para ajustar su encuadre.</p>`;
    }
    $('#veControls').innerHTML=html;
    $$('[data-tool-path]',$('#veControls')).forEach(input=>{
      const handler=()=>{const path=input.dataset.toolPath,value=input.type==='checkbox'?input.checked:input.type==='range'?+input.value:input.value;edit(()=>write(path,value),true);if(input.nextElementSibling?.tagName==='OUTPUT')input.nextElementSibling.textContent=String(value);};
      input.addEventListener(input.type==='range'||input.type==='color'?'input':'change',handler);
    });
    $$('[data-add]',$('#veControls')).forEach(b=>b.onclick=()=>add(b.dataset.add));
    $$('[data-preset]').forEach(b=>b.onclick=()=>add('text',b.dataset.preset));$$('[data-effect]').forEach(b=>b.onclick=()=>add('fx',b.dataset.effect));
    $$('[data-sound]').forEach(b=>b.onclick=()=>add('sfx',b.dataset.sound));$$('[data-hear]').forEach(b=>b.onclick=()=>new Audio('/api/sfx/'+b.dataset.hear).play().catch(()=>{}));
    $$('[data-select-kind]').forEach(b=>b.onclick=()=>select(b.dataset.selectKind,+(b.dataset.index||0)));
    if($('#veSafeLock'))$('#veSafeLock').onclick=()=>edit(()=>{e.safe_area.locked=!locked;});
    if($('#veResetSafe'))$('#veResetSafe').onclick=()=>edit(()=>Object.assign(z,{left:100,right:100,top:Math.min(max,height()*.125),bottom:Math.min(max,height()*.125)}));
    if($('#veSaveSafe'))$('#veSaveSafe').onclick=()=>{const name=$('#veSafeName').value.trim();if(!name)return;const rows=templates().filter(x=>x.name!==name);rows.push({name,safe:copy(z)});try{localStorage.setItem('clipper.composition.safe.templates',JSON.stringify(rows));panel();}catch{toast('No se pudo guardar la plantilla','er');}};
    if($('#veSafeTemplate'))$('#veSafeTemplate').onchange=ev=>{if(ev.target.value==='')return;const template=templates()[+ev.target.value];if(template)edit(()=>{e.safe_area={...copy(template.safe),top:Math.min(max,template.safe.top),bottom:Math.min(max,template.safe.bottom),locked:false};});};
    if($('[data-reset-color]'))$('[data-reset-color]').onclick=()=>edit(()=>{e.color={look:'none',look_amount:1,brightness:0,contrast:0,saturation:0,temperature:0,tint:0,fade:0,vignette:0};});
  }
  function entries(kind){return list(kind).map((x,i)=>`<button class="ve-entry b g sm" data-select-kind="${kind}" data-index="${i}"><span data-ui-content>${start(kind,x).toFixed(1)}s · ${esc(kind==='cue'?x.words.map(w=>w.w).join(' '):x.text?.en||x.type||x.id||labels[kind])}</span></button>`).join('');}
  function add(kind,preset){
    const V=get(),t=Math.min(V.t,Math.max(0,V.doc.duration-.3)),length=Math.min(3,V.doc.duration-t);
    if(kind==='text'){
      const p=copy(catalog().text_presets.find(x=>x.id===preset)||catalog().text_presets[0]);delete p.id;delete p.label;delete p.sample;
      edit(()=>{edl().texts.push({opacity:1,glow:0,rotate:0,...p,t,duration:length,text:{en:'Text'},x:.5,y:.5});V.toolSelection={kind,index:edl().texts.length-1};V.selected=null;});
    }else if(kind==='cue'){
      edit(()=>{track().cues.push({start:t,end:t+length,words:[{w:'Caption',start:t,end:t+length,hl:false}]});V.toolSelection={kind,index:track().cues.length-1};V.selected=null;});
    }else if(kind==='note'){
      edit(()=>{edl().commentary.notes.push({t,duration:length,text:{en:'Note'},x:.5,y:.3});V.toolSelection={kind,index:edl().commentary.notes.length-1};V.selected=null;});
    }else if(kind==='fx'){
      const spec=catalog().effects.find(x=>x.id===preset)||catalog().effects[0];
      edit(()=>{edl().effects.push({t,duration:Math.min(spec.d,V.doc.duration-t),type:spec.id,intensity:1,...(spec.id==='face_focus'?{region:{x:.25,y:.25,w:.5,h:.5}}:{})});V.toolSelection={kind,index:edl().effects.length-1};V.selected=null;});
    }else if(kind==='sfx')edit(()=>{edl().audio.sfx.push({t,id:preset,gain:1});V.toolSelection={kind,index:edl().audio.sfx.length-1};V.selected=null;});
  }
  function select(kind,index){get().selected=null;get().toolSelection={kind,index};$('#veInspector').hidden=false;rightLayout();C.inspector();C.timeline();}
  function inspector(){
    const V=get(),s=V.toolSelection,x=selected();if(!s||!x)return false;
    const kind=s.kind;
    const number=(key,label,value,min,max,step=.01)=>`<label>${label}<input type="number" data-selected="${key}" step="${step}" min="${min}" max="${max}" value="${value}"></label>`;
    let html=`<h3>${labels[kind]}</h3><button class="b g sm" id="veToolDelete">Eliminar</button>`;
    if(['text','note','hook','cue'].includes(kind))html+=`<label>Texto<textarea id="veSelectedText" rows="3" maxlength="${kind==='hook'?120:200}">${esc(kind==='cue'?x.words.map(w=>w.w).join(' '):x.text.en||'')}</textarea></label>`;
    if(kind!=='hook')html+=number('start','Inicio en el montaje (s)',start(kind,x),0,V.doc.duration-.1);
    if(kind!=='sfx')html+=number('duration','Duración (s)',duration(kind,x),.1,V.doc.duration-start(kind,x));
    if(['text','note','hook','cue'].includes(kind)){const position=kind==='cue'?edl().captions:x;html+=number('x','Horizontal',position.x??.5,0,1,.005)+number('y','Vertical',position.y??.5,0,1,.005);}
    if(kind==='text'){
      const options=(key,values)=>`<select data-selected="${key}">${values.map(([id,label])=>`<option value="${id}" ${x[key]===id?'selected':''}>${esc(label)}</option>`).join('')}</select>`;
      html+=`<label>Fuente${options('font',Object.entries(V.studio.fonts).map(([k,f])=>[k,f.label]))}</label>${number('size','Tamaño',x.size,16,300,1)}<label>Color<input type="color" data-selected="color" value="${x.color}"></label><label>Borde<input type="color" data-selected="stroke" value="${x.stroke}"></label>${number('stroke_w','Grosor borde',x.stroke_w,0,24,.5)}<label class="tog"><input type="checkbox" id="veTextBox" ${x.box?'checked':''}>Caja</label>${x.box?`<input type="color" data-selected="box" value="${x.box}">${number('box_opacity','Opacidad caja',x.box_opacity,.1,1)}`:''}${number('opacity','Opacidad',x.opacity,.1,1)}${number('glow','Brillo',x.glow,0,12,.5)}${number('rotate','Giro',x.rotate,-45,45,1)}<label class="tog"><input type="checkbox" data-selected="upper" ${x.upper?'checked':''}>MAYÚSCULAS</label><label>Entrada${options('anim_in',catalog().anim_in)}</label><label>Salida${options('anim_out',catalog().anim_out)}</label>`;
    }
    if(kind==='fx')html+=number('intensity','Fuerza',x.intensity,.2,2,.05)+(x.type==='face_focus'?`<p class="hint">Selecciona la región de la cara en el lienzo compuesto.</p>${['x','y','w','h'].map(k=>number('region.'+k,k,x.region[k],k==='w'||k==='h'?.04:0,1)).join('')}`:'');
    if(kind==='sfx')html+=number('gain','Volumen',x.gain,0,3,.05);
    if(kind==='cue')html+=`<label>Palabras resaltadas</label><div class="ve-options">${x.words.map((w,i)=>`<button class="b g sm ${w.hl?'on':''}" data-highlight="${i}" data-ui-content>${esc(w.w)}</button>`).join('')}</div>`;
    $('#veInspector').innerHTML=html;
    $('#veToolDelete').onclick=remove;
    if($('#veSelectedText'))$('#veSelectedText').onchange=ev=>edit(()=>{if(kind==='cue'){const words=ev.target.value.trim().split(/\s+/).filter(Boolean),len=x.end-x.start;x.words=words.map((w,i)=>({w,start:x.start+len*i/words.length,end:x.start+len*(i+1)/words.length,hl:false}));}else x.text.en=ev.target.value;});
    $$('[data-selected]').forEach(input=>input.onchange=()=>{
      const key=input.dataset.selected,value=input.type==='checkbox'?input.checked:input.type==='number'?+input.value:input.value;
      if(input.type==='number'&&(!Number.isFinite(value)||value<+input.min||value>+input.max)){C.inspector();return;}
      edit(()=>{
        if(key==='start'||key==='duration'){setSpan(kind,x,key==='start'?value:start(kind,x),key==='duration'?value:duration(kind,x));return;}
        if(key.startsWith('region.')){const k=key.split('.')[1];x.region[k]=value;x.region.w=Math.min(x.region.w,1-x.region.x);x.region.h=Math.min(x.region.h,1-x.region.y);}
        else if(kind==='cue'&&(key==='x'||key==='y'))edl().captions[key]=value;
        else x[key]=value;
      });
    });
    if($('#veTextBox'))$('#veTextBox').onchange=ev=>edit(()=>{x.box=ev.target.checked?'#FFE500':'';});
    $$('[data-highlight]').forEach(b=>b.onclick=()=>edit(()=>{x.words[+b.dataset.highlight].hl=!x.words[+b.dataset.highlight].hl;}));
    return true;
  }
  function remove(){const s=get().toolSelection;if(!s)return false;edit(()=>{if(s.kind==='hook'){edl().hook.duration=0;edl().hook.text.en='';}else list(s.kind).splice(s.index,1);get().toolSelection=null;});return true;}
  function clipboardInfo(){const s=get().toolSelection,x=selected();return !x||s.kind==='hook'?null:{kind:s.kind,value:x,start:start(s.kind,x),duration:duration(s.kind,x)};}
  function insertCopied(kind,value){const items=list(kind);if(!items)return;items.push(value);get().selected=null;get().toolSelection={kind,index:items.length-1};}
  function splitCue(){
    const V=get(),s=V.toolSelection,x=selected();if(!s||!x)return false;
    if(['text','note','fx'].includes(s.kind)){
      const at=V.t-x.t,min=s.kind==='fx'?(catalog().effects.find(f=>f.id===x.type)?.min||.1):s.kind==='note'?.5:.2;
      if(at<min||x.duration-at<min){toast('Sitúa el cursor dentro del recurso.','er');return true;}
      edit(()=>{const right=copy(x);right.t=V.t;right.duration=x.duration-at;x.duration=at;list(s.kind).splice(s.index+1,0,right);});return true;
    }
    if(s.kind!=='cue')return false;
    const at=V.t;if(at<x.start+.08||at>x.end-.08)return true;
    edit(()=>{
      const right={start:at,end:x.end,words:x.words.filter(w=>w.end>at).map(w=>({...w,start:Math.max(at,w.start)}))};
      x.end=at;x.words=x.words.filter(w=>
        w.start<at).map(w=>({...w,end:Math.min(at,w.end)}));track().cues.splice(s.index+1,0,right);
    });return true;
  }
  function setSpan(k,x,t,len){
    len=Math.max(.1,Math.min(len,get().doc.duration-t));
    if(k==='fx'){const spec=catalog().effects.find(e=>e.id===x.type);if(spec)len=clamp(len,spec.min,Math.min(spec.max,Math.max(spec.min,get().doc.duration-t)));}
    if(k==='cue'){const before=x.start,scale=len/Math.max(.001,x.end-x.start);for(const w of x.words){w.start=t+(w.start-before)*scale;w.end=t+(w.end-before)*scale;}x.start=t;x.end=t+len;}
    else if(k==='hook')x.duration=Math.min(8,len);else{x.t=t;if(k!=='sfx')x.duration=len;}
  }
  function timeline(){
    let html='',heads='';const V=get();
    const rows=[['hook',[edl().hook]],...['text','note','cue','fx','sfx'].map(k=>[k,list(k)])];
    for(const [kind,items] of rows)if((items.length||['text','cue','fx'].includes(kind))&&(kind!=='hook'||edl().hook.duration>0)){
      const packed=EditorCore.lanes(items,x=>[start(kind,x),start(kind,x)+duration(kind,x)]),h=packed.count*38;
      heads+=`<div style="height:${h}px">${esc(labels[kind])}</div>`;
      html+=`<div class="ve-lane ve-tool-lane" data-lane="${kind}" style="height:${h}px">${items.map((x,i)=>`<button class="ve-item ve-${kind} ${V.toolSelection?.kind===kind&&V.toolSelection.index===i?'selected':''}" data-tool-kind="${kind}" data-tool-index="${i}" style="top:${packed.positions[i]*38+4}px;left:${start(kind,x)*V.zoom}px;width:${Math.max(14,duration(kind,x)*V.zoom)}px"><i class="ve-handle" data-edge="l"></i><span data-ui-content>${esc(kind==='cue'?x.words.map(w=>w.w).join(' '):x.text?.en||x.type||x.id||labels[kind])}</span><i class="ve-handle" data-edge="r"></i></button>`).join('')}</div>`;
    }
    return {html,heads};
  }
  function pointer(e){
    const button=e.target.closest('[data-tool-kind]');if(!button)return false;
    const V=get(),kind=button.dataset.toolKind,index=+button.dataset.toolIndex;V.selected=null;V.toolSelection={kind,index};C.inspector();
    const x=selected(),a=start(kind,x),len=duration(kind,x),x0=e.clientX,edge=e.target.dataset.edge;let moved=false;
    button.setPointerCapture(e.pointerId);
    button.onpointermove=ev=>{
      const delta=(ev.clientX-x0)/V.zoom;if(Math.abs(ev.clientX-x0)<3&&!moved)return;
      if(!moved){V.undo.push(copy(V.doc));V.redo=[];moved=true;}
      const begin=kind==='hook'?0:clamp(a+delta,0,V.doc.duration-.1);
      if(edge==='l'&&kind!=='hook')setSpan(kind,x,Math.min(begin,a+len-.1),a+len-Math.min(begin,a+len-.1));
      else if(edge==='r')setSpan(kind,x,a,clamp(len+delta,.1,V.doc.duration-a));
      else setSpan(kind,x,kind==='hook'?0:snap(clamp(a+delta,0,V.doc.duration-len)),len);
      button.style.left=start(kind,x)*V.zoom+'px';button.style.width=Math.max(14,duration(kind,x)*V.zoom)+'px';
    };
    button.onpointerup=()=>{button.onpointermove=null;button.onpointerup=null;if(moved)C.commit();C.timeline();C.inspector();C.transport();};return true;
  }
  function snap(t){const V=get();if(!V.snap)return t;const points=[0,V.t,V.doc.duration,...V.doc.items.flatMap(x=>[x.start,x.start+x.duration])];const point=points.find(p=>
      Math.abs(p-t)<7/V.zoom);return point??t;}
  function dragOverlay(e){
    const el=e.target.closest('[data-overlay-kind]');if(!el)return;
    const V=get(),kind=el.dataset.overlayKind,index=+el.dataset.overlayIndex;V.toolSelection={kind,index};V.selected=null;C.inspector();
    const x=kind==='cue'?edl().captions:selected(),rect=$('#veCanvas').getBoundingClientRect(),a=parseFloat(el.style.left)/1080,b=parseFloat(el.style.top)/height(),x0=e.clientX,y0=e.clientY;let moved=false;
    el.setPointerCapture(e.pointerId);
    el.onpointermove=ev=>{if(!moved&&Math.hypot(ev.clientX-x0,ev.clientY-y0)<3)return;if(!moved){V.undo.push(copy(V.doc));V.redo=[];moved=true;}x.x=clamp(a+(ev.clientX-x0)/rect.width,0,1);x.y=clamp(b+(ev.clientY-y0)/rect.height,0,1);};
    el.onpointerup=()=>{el.onpointermove=null;el.onpointerup=null;if(moved)C.commit();C.inspector();C.timeline();};
  }
  function dragFrame(e){
    if(!e.target.closest('#vePicture'))return;
    const V=get(),item=V.doc.items.filter(x=>
      x.start<=V.t&&V.t<x.start+x.duration&&V.assets.find(a=>a.id===x.asset_id)?.kind!=='audio').at(-1);
    if(!item)return;V.toolSelection=null;V.selected=item.id;C.inspector();
    if(item.fit!=='cover')return;
    const el=$('#vePicture').querySelector(`[data-id="${item.id}"]`),w=el?.videoWidth||el?.naturalWidth,h=el?.videoHeight||el?.naturalHeight;if(!w||!h)return;
    const target=$('#veCanvas'),rect=target.getBoundingClientRect(),k=Math.max(rect.width/w,rect.height/h),freeX=w*k-rect.width,freeY=h*k-rect.height,x0=e.clientX,y0=e.clientY,x=item.crop_x,y=item.crop_y;let moved=false;
    target.setPointerCapture(e.pointerId);
    target.onpointermove=ev=>{if(!moved&&Math.hypot(ev.clientX-x0,ev.clientY-y0)<3)return;if(!moved){V.undo.push(copy(V.doc));V.redo=[];moved=true;}if(freeX>1)item.crop_x=clamp(x-(ev.clientX-x0)/freeX,0,1);if(freeY>1)item.crop_y=clamp(y-(ev.clientY-y0)/freeY,0,1);};
    target.onpointerup=()=>{target.onpointermove=null;target.onpointerup=null;if(moved)C.commit();C.inspector();};
  }
  function summary(){const V=get();return `<h3>Propiedades</h3><div class="ve-stats"><div><small>Duración</small><b data-ui-content>${V.doc.duration.toFixed(1)} s</b></div><div><small>Recursos</small><b data-ui-content>${V.doc.items.length}</b></div><div><small>Subtítulos</small><b data-ui-content>${track().cues.length}</b></div><div><small>Notas</small><b data-ui-content>${edl().commentary.notes.length}</b></div></div><p class="hint">Selecciona un recurso en la línea de tiempo.</p><h4>Atajos</h4><p class="hint">${['Espacio','← →','T','N','Z','S','Supr','Ctrl+Z','Ctrl+Y'].map(k=>`<kbd>${esc(k)}</kbd>`).join(' ')}</p>`;}
  function chat(){
    if(!$('#veChatMessages'))return;
    const box=$('#veChatMessages');box.innerHTML=get().doc.chat.map(m=>`<article class="ve-message ${m.role}"><small>${m.role==='user'?I18n.t('Tú'):'Kairos AI'}</small><p data-ui-content>${esc(m.content)}</p>${m.usage?`<small data-ui-content>${esc(m.usage.model)} · ${Number(m.usage.input_tokens)} + ${Number(m.usage.output_tokens)} tokens${m.usage.estimated_usd==null?'':` · ~$${Number(m.usage.estimated_usd).toFixed(5)}`}</small>`:''}</article>`).join('')||`<div class="ve-agent-welcome"><span>${icon('spark')}</span><h3>Imagina. Pídelo. Míralo.</h3><p>Describe tu idea. Los cambios aparecerán en el lienzo y la línea de tiempo; puedes deshacerlos.</p><button class="b g sm" data-agent-prompt="Añade un título animado al inicio">Añadir un título animado</button><button class="b g sm" data-agent-prompt="Aplica un color cálido y una entrada suave">Dar estilo al video</button></div>`;
    $$('[data-agent-prompt]').forEach(b=>b.onclick=()=>{$('#vePrompt').value=b.dataset.agentPrompt;$('#vePrompt').focus();});box.scrollTop=box.scrollHeight;
  }
  function wordWidth(text,font,size){measure.font=`${size}px VE_${font}`;return measure.measureText(text).width;}
  function wrap(text,font,size,width){const lines=[];let line='';for(const word of text.split(/\s+/)){const next=(line+' '+word).trim();if(line&&wordWidth(next,font,size)>width){lines.push(line);line=word;}else line=next;}if(line)lines.push(line);return lines;}
  function preview(){
    if(!C||!get()||!$('#veOverlays'))return;
    const V=get(),e=edl(),t=V.t,H=height(),z=e.safe_area,scale=$('#veCanvas').clientWidth/1080,safe={...z,canvas_width:1080,canvas_height:H};
    const root=$('#veOverlays');root.style.cssText=`width:1080px;height:${H}px;transform:scale(${scale});transform-origin:0 0;clip-path:inset(${z.top}px ${z.right}px ${z.bottom}px ${z.left}px)`;
    const guide=$('#veSafeGuide');guide.hidden=!V.showSafe;guide.style.cssText=`left:${z.left/1080*100}%;right:${z.right/1080*100}%;top:${z.top/H*100}%;bottom:${z.bottom/H*100}%`;
    const safeW=1080-z.left-z.right,top=z.top,bottom=H-z.bottom;
    const blocks=[];
    function block(kind,index,text,x,y,font,size,style='',lineHeight=null){const b={kind,index,text,x,y,font,size,style,lineHeight:lineHeight??size/(V.studio.fonts[font]?.scale||.83)};blocks.push(b);return b;}
    const fscale=font=>V.studio.fonts[font]?.scale||.83;
    if(e.hook.duration>t&&e.hook.text.en){const text=e.hook.text.en.toUpperCase(),lines=wrap(text,'caption',86*fscale('caption'),safeW-40);block('hook',0,lines.join('\n'),e.hook.x??.5,clamp((e.hook.y??((top+150)/H))*H,top+60,bottom-60)/H,'caption',86*fscale('caption'),'background:#FFE500;color:#000;padding:16px',86);}
    e.commentary.notes.forEach((x,i)=>{if(t>=x.t&&t<x.t+x.duration)block('note',i,wrap(x.text.en,'text',46*fscale('text'),safeW-40).join('\n'),x.x??.5,x.y??((top+150)/H),'text',46*fscale('text'),'background:#181818c8;color:#fff;padding:14px',46);});
    track().cues.forEach((x,i)=>{if(t>=x.start&&t<x.end){const spec=catalog().caption_styles[e.captions.style],text=x.words.map(w=>spec.upper?w.w.toUpperCase():w.w).join(' '),base=spec.size*e.captions.scale,width=wordWidth(text,spec.font,base*fscale(spec.font))*1.2+spec.outline*2,size=width<=safeW?base:Math.max(12,Math.floor(base*safeW/width));const b=block('cue',i,text,e.captions.x??.5,clamp((e.captions.y??((bottom-140)/H))*H,top+80,bottom-80)/H,spec.font,size*fscale(spec.font),`color:${spec.color};-webkit-text-stroke:${spec.outline*2}px ${spec.outline_color};paint-order:stroke fill;text-shadow:0 0 ${spec.glow*3}px ${spec.outline_color}`,size);b.cue=x;b.spec=spec;}});
    e.texts.forEach((x,i)=>{
      if(t<x.t||t>=x.t+x.duration)return;
      try{
        const f=V.studio.fonts[x.font]?.scale||.83,p=SafeLayout.text(x,'en',safe,f,(s,fs)=>wordWidth(s,x.font,fs*f),(s,fs,w)=>wrap(s,x.font,fs*f,w));
        const elapsed=t-x.t,remaining=x.t+x.duration-t;let opacity=x.opacity,zoom=1,dx=0,dy=0;
        if(x.anim_in!=='none')opacity*=clamp(elapsed/.12,0,1);if(x.anim_out==='fade')opacity*=clamp(remaining/.18,0,1);
        if(x.anim_in==='pop')zoom=elapsed<.18?.4+.72*elapsed/.18:elapsed<.28?1.12-.12*(elapsed-.18)/.1:1;
        if(x.anim_in==='zoom')zoom=elapsed<.25?1.6-.6*elapsed/.25:1;
        if(x.anim_in.startsWith('slide_')&&elapsed<.3){const shift=140*(1-elapsed/.3);if(x.anim_in==='slide_left')dx=shift;if(x.anim_in==='slide_right')dx=-shift;if(x.anim_in==='slide_up')dy=shift;if(x.anim_in==='slide_down')dy=-shift;}
        if(x.anim_out==='shrink')zoom*=clamp(remaining/.2,0,1);
        block('text',i,p.lines.join('\n'),(p.x+dx)/1080,(p.y+dy)/H,x.font,p.em,`opacity:${opacity};color:${x.color};${x.box?`background:color-mix(in srgb,${x.box} ${x.box_opacity*100}%,transparent);padding:14px;`:`-webkit-text-stroke:${x.stroke_w}px ${x.stroke};paint-order:stroke fill;`}text-shadow:0 0 ${x.glow}px ${x.stroke};--ve-rotate:${x.rotate}deg;--ve-scale:${zoom}`,p.fs);
      }catch{}
    });
    const ids=new Set(blocks.map(b=>b.kind+'-'+b.index));for(const child of [...root.children])if(!ids.has(child.dataset.overlayKind+'-'+child.dataset.overlayIndex)&&child.id!=='veProgress')child.remove();
    for(const b of blocks){let el=root.querySelector(`[data-overlay-kind="${b.kind}"][data-overlay-index="${b.index}"]`);if(!el){el=document.createElement('div');el.className='ve-overlay';el.dataset.overlayKind=b.kind;el.dataset.overlayIndex=b.index;el.setAttribute('data-ui-content','');root.append(el);}if(b.kind==='cue'){let current=0;b.cue.words.forEach((w,i)=>{if(w.start<=t)current=i;});const html=b.cue.words.map((w,i)=>`<span style="${i===current?`color:${b.spec.active};font-size:1.06em`:w.hl?`color:${b.spec.keyword}`:''}">${esc(b.spec.upper?w.w.toUpperCase():w.w)}</span>`).join(' ');if(el.innerHTML!==html)el.innerHTML=html;}else if(el.textContent!==b.text)el.textContent=b.text;el.style.cssText=`left:${b.x*1080}px;top:${b.y*H}px;font-family:VE_${b.font};font-size:${b.size}px;line-height:${b.lineHeight}px;max-width:${safeW}px;${b.style}`;}
    let progress=$('#veProgress');if(!progress){progress=document.createElement('div');progress.id='veProgress';root.append(progress);}progress.hidden=!e.progress.on;progress.style.cssText=`position:absolute;left:${z.left}px;top:${e.progress.position==='top'?top:bottom-e.progress.height}px;width:${safeW*clamp(t/V.doc.duration,0,1)}px;height:${e.progress.height}px;background:${e.progress.color}`;
    const active=e.effects.filter(x=>t>=x.t&&t<x.t+x.duration);let zoom=1,dx=0,dy=0;
    for(const x of active){const dt=t-x.t,k=x.intensity,fx=catalog().fx;if(x.type==='punch_in')zoom+=fx.zoom*k*Math.min(1,dt/fx.ramp,(x.duration-dt)/fx.ramp);if(x.type==='zoom_slow')zoom+=fx.slow*k*dt/x.duration;if(x.type==='shake'){zoom+=fx.shake_zoom*k;dx=fx.shake_px*k*Math.sin(2*Math.PI*fx.shake_hz*t)*scale;dy=fx.shake_px*k*Math.cos(2*Math.PI*fx.shake_hz*1.3*t)*scale;}}
    $('#vePicture').style.transform=`translate(${-dx}px,${-dy}px) scale(${zoom})`;
    const cp=colors(e.color);let filters='url(#veColorMatrix)';$('#veColorValues').setAttribute('values',matrix(cp));
    if(active.some(x=>x.type==='bw'))filters+=' grayscale(1)';const blur=active.find(x=>x.type==='blur');if(blur)filters+=` blur(${catalog().fx.blur_sigma*blur.intensity*scale}px)`;const glitch=active.find(x=>x.type==='glitch');if(glitch){const shift=catalog().fx.glitch_px*glitch.intensity*scale;$('#veGlR').setAttribute('dx',-shift);$('#veGlB').setAttribute('dx',shift);filters+=' url(#veGlitch)';}$('#vePicture').style.filter=filters;
    const tr=e.transitions;let fade=0,flash=0;if(tr.intro==='fade'&&t<tr.intro_d)fade=1-t/tr.intro_d;if(tr.outro==='fade'&&t>V.doc.duration-tr.outro_d)fade=(t-(V.doc.duration-tr.outro_d))/tr.outro_d;
    for(const x of active)if(x.type==='flash')flash+=catalog().fx.flash*x.intensity*(1-(t-x.t)/x.duration);if(tr.intro==='flash'&&t<tr.intro_d)flash+=catalog().fx.flash*1.2*(1-t/tr.intro_d);
    $('#veVideoFade').style.cssText=`opacity:${Math.max(fade,flash)};background:${flash>fade?'#fff':'#000'}`;
    let vig=$('#veVignette');if(!vig){vig=document.createElement('img');vig.id='veVignette';vig.src='/api/studio/vignette.png';$('#veCanvas').append(vig);}vig.style.opacity=cp.vignette;
    facePreview(active.find(x=>x.type==='face_focus'),scale,z);
    previewAudio();
  }
  function colors(c){const base=catalog().looks.find(x=>x.id===c.look)?.p||{},out={};for(const k of catalog().color_params)out[k]=clamp((base[k]||0)*(c.look_amount??1)+(c[k]||0),['fade','sepia','vignette'].includes(k)?0:-1,1);return out;}
  function matrix(p){
    const luma=[.2126,.7152,.0722],sepia=[[.393,.769,.189],[.349,.686,.168],[.272,.534,.131]],ids=[0,1,2],sat=1+p.saturation;
    let m=ids.map(i=>ids.map(j=>(1-sat)*luma[j]+(i===j?sat:0)));
    if(p.sepia){const mix=ids.map(i=>ids.map(j=>(1-p.sepia)*(i===j?1:0)+p.sepia*sepia[i][j]));m=ids.map(i=>ids.map(j=>mix[i][0]*m[0][j]+mix[i][1]*m[1][j]+mix[i][2]*m[2][j]));}
    const gain=[1+.18*p.temperature+.05*p.tint,1-.14*p.tint,1-.18*p.temperature+.05*p.tint],slope=1+p.contrast*(p.contrast>=0?.6:.5),k=slope*(1-.2*p.fade),off=(.5-.5*slope+.25*p.brightness)*(1-.2*p.fade)+.1*p.fade;
    return ids.map(i=>[...ids.map(j=>clamp(k*gain[i]*m[i][j],-2,2)),0,off].join(' ')).join(' ')+' 0 0 0 1 0';
  }
  function facePreview(effect,scale,safe){
    let canvas=$('#veFaceCanvas');if(!canvas){canvas=document.createElement('canvas');canvas.id='veFaceCanvas';$('#veCanvas').append(canvas);}canvas.hidden=!effect;if(!effect)return;
    const V=get(),H=height(),r=effect.region,source=document.createElement('canvas');source.width=1080;source.height=H;const ctx=source.getContext('2d');ctx.fillStyle='#000';ctx.fillRect(0,0,1080,H);
    for(const item of V.doc.items){if(V.t<item.start||V.t>=item.start+item.duration)continue;const el=$('#vePicture').querySelector(`[data-id="${item.id}"]`);if(!el||el.tagName==='AUDIO')continue;const w=el.videoWidth||el.naturalWidth,h=el.videoHeight||el.naturalHeight;if(!w||!h)continue;const k=item.fit==='cover'?Math.max(1080/w,H/h):Math.min(1080/w,H/h),x=item.fit==='cover'?-(w*k-1080)*item.crop_x:(1080-w*k)/2,y=item.fit==='cover'?-(h*k-H)*item.crop_y:(H-h*k)/2;ctx.fillRect(0,0,1080,H);try{ctx.drawImage(el,x,y,w*k,h*k);}catch{}}
    const factor=Math.min(360/(r.w*1080),200/(r.h*H)),iw=Math.max(2,Math.floor(r.w*1080*factor/2)*2),ih=Math.max(2,Math.floor(r.h*H*factor/2)*2);
    if(canvas.width!==iw+16)canvas.width=iw+16;if(canvas.height!==ih+16)canvas.height=ih+16;const c=canvas.getContext('2d');c.fillStyle='#FFE500';c.fillRect(0,0,canvas.width,canvas.height);c.drawImage(source,r.x*1080,r.y*H,r.w*1080,r.h*H,8,8,iw,ih);
    const opacity=Math.min(1,(V.t-effect.t)/.12,(effect.t+effect.duration-V.t)/.12);canvas.style.cssText=`left:${(1080-safe.right-iw-16)*scale}px;top:${safe.top*scale}px;width:${canvas.width*scale}px;height:${canvas.height*scale}px;opacity:${Math.max(0,opacity)}`;
  }
  function previewAudio(){
    const V=get(),a=edl().audio;
    if(V.playing&&!V.audioWasPlaying)V.lastSoundTime=Math.max(-.0001,V.t-1/V.settings.fps);
    if(!V.music||V.music.dataset.id!==(a.music_id||'')){V.music?.pause();V.music=a.music_id?new Audio('/api/sfx/'+a.music_id):null;if(V.music){V.music.dataset.id=a.music_id;V.music.loop=true;}}
    if(V.music){const entry=V.studio.library.find(x=>x.id===a.music_id),target=V.t%(entry?.duration||10);if(V.music.readyState>=1&&Math.abs(V.music.currentTime-target)>.25)V.music.currentTime=target;V.music.volume=V.muted?0:clamp((entry?.gain||.22)*a.music_gain,0,1);if(V.playing&&V.music.paused)V.music.play().catch(()=>{});if(!V.playing)V.music.pause();}
    if(V.playing&&!V.muted&&V.lastSoundTime!=null&&V.t>=V.lastSoundTime)for(const sound of a.sfx)if(sound.t>V.lastSoundTime&&sound.t<=V.t){const audio=new Audio('/api/sfx/'+sound.id),entry=V.studio.library.find(x=>x.id===sound.id);audio.volume=clamp((entry?.gain||.6)*sound.gain,0,1);audio.play().catch(()=>{});(V.sounds??=[]).push(audio);}
    V.lastSoundTime=V.t;V.audioWasPlaying=V.playing;V.sounds=V.sounds?.filter(x=>!x.ended);
  }
  function key(e){
    if(e.target.closest('input,textarea,select'))return false;
    const k=e.key.toLowerCase();if(e.ctrlKey||e.metaKey)return false;if(k==='t'){add('text');return true;}if(k==='n'){add('note');return true;}if(k==='z'){add('fx');return true;}if(k==='s')return splitCue();if(k==='delete'||k==='backspace')return remove();
    if(k==='arrowleft'||k==='arrowright'){seek(get().t+(k==='arrowleft'?-1:1)*(e.shiftKey?1:1/get().settings.fps));return true;}return false;
  }
  return {attach,panel,inspector,timeline,pointer,snap,preview,chat,key,remove,splitCue,summary,openTab,selected,colors,clipboardInfo,insertCopied};
})();
