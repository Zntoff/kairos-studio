/* Clipper: editor de clips.
   El documento que se edita (E.doc) guarda todos los tiempos en segundos del video FUENTE, así
   que recortar el clip no mueve nada de sitio. Al guardar se convierte a tiempos del clip (EDL).
   La vista previa es en vivo: el video fuente con el encuadre, los textos y el zoom dibujados
   encima con las mismas medidas que usa el render de FFmpeg. */
const Editor = (() => {
  let E = null, raf = 0, saveTimer = 0, uidN = 0, mctx = null, resizeObserver = null, opening = 0;
  const uid = () => 'i' + (++uidN);
  const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
  const r3 = v => Math.round(v * 1000) / 1000;
  const copy = o => JSON.parse(JSON.stringify(o));
  const tc = t => { const n = t < 0; t = Math.abs(t); return `${n ? '-' : ''}${Math.floor(t / 60)}:${String(Math.floor(t % 60)).padStart(2, '0')}.${Math.floor(t * 10 % 10)}` };
  const FONT = { caption: { f: 'ClipCaption, Inter, system-ui, sans-serif', w: 900 }, text: { f: 'ClipText, Inter, system-ui, sans-serif', w: 700 } };
  const ROW = { title: 30, text: 30, cap: 30, video: 58, fx: 26, audio: 46 };
  const TABS = [['resources', 'Recursos', 'folder'], ['captions', 'Subtítulos', 'cc'], ['text', 'Texto', 'text'], ['fx', 'Efectos', 'spark'], ['color', 'Color', 'drop'], ['audio', 'Audio', 'music'], ['format', 'Formato', 'frame']];
  const KNAME = { hook: 'Gancho', note: 'Nota', cue: 'Subtítulo', sfx: 'Sonido', fx: 'Efecto', text: 'Texto', media: 'Recurso', clip: 'Clip' };
  Object.assign(P, {
    drop: '<path d="M12 3s6 6.2 6 10.5a6 6 0 0 1-12 0C6 9.2 12 3 12 3z"/>', layers: '<path d="M12 3l9 5-9 5-9-5 9-5zM3 13l9 5 9-5"/>',
    lock: '<rect x="5" y="10" width="14" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/>', unlock: '<rect x="5" y="10" width="14" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0"/>', reset: '<path d="M4 12a8 8 0 1 0 2.4-5.7L4 8.5M4 4v4.5h4.5"/>',
  });
  const COLOR_UI = [['brightness', 'Brillo', -1], ['contrast', 'Contraste', -1], ['saturation', 'Saturación', -1], ['temperature', 'Temperatura', -1], ['tint', 'Matiz', -1], ['fade', 'Velado', 0], ['vignette', 'Viñeta', 0]];
  const COLOR0 = { look: 'none', look_amount: 1, brightness: 0, contrast: 0, saturation: 0, temperature: 0, tint: 0, fade: 0, vignette: 0 };
  const TEXT0 = { x: .5, y: .3, font: 'anton', size: 80, color: '#FFFFFF', stroke: '#000000', stroke_w: 6, box: '', box_opacity: 1, opacity: 1, glow: 0, upper: false, rotate: 0, anim_in: 'fade', anim_out: 'fade' };
  const LUMA = [.2126, .7152, .0722], SEPIA = [[.393, .769, .189], [.349, .686, .168], [.272, .534, .131]];

  /* ---------- catálogo: fuentes, color y efectos (mismas cuentas que media/studio.py) ---------- */
  const fscale = id => (E.d.fonts[id] || E.d.fonts.caption || { scale: .83 }).scale;
  const font = id => ({ ...(FONT[id] || { f: `"F_${id}", Inter, system-ui, sans-serif`, w: 400 }), s: fscale(id) });
  const fxSpec = type => E.d.catalog.effects.find(e => e.id == type) || E.d.catalog.effects[0];
  function loadFonts() {
    const ids = Object.keys(E.d.fonts).filter(id => !FONT[id]);
    if (!$('#edFonts')) { const st = document.createElement('style'); st.id = 'edFonts'; document.head.append(st) }
    $('#edFonts').textContent = ids.map(id => `@font-face{font-family:"F_${id}";src:url(/api/fonts/${id});font-weight:100 900;font-display:swap}`).join('');
    return Promise.all([document.fonts.load('900 84px ClipCaption'), document.fonts.load('700 46px ClipText'), ...ids.map(id => document.fonts.load(`400 40px "F_${id}"`))]).catch(() => { });
  }
  function colorParams(c) {
    const base = (E.d.catalog.looks.find(l => l.id == c.look) || { p: {} }).p, out = {};
    for (const k of E.d.catalog.color_params) out[k] = clamp((base[k] || 0) * c.look_amount + (c[k] || 0), k == 'fade' || k == 'sepia' || k == 'vignette' ? 0 : -1, 1);
    return out;
  }
  function colorAffine(p) {   // tres filas [r, g, b, desplazamiento]
    const sat = 1 + p.saturation, I3 = [0, 1, 2];
    let m = I3.map(i => I3.map(j => (1 - sat) * LUMA[j] + (i == j ? sat : 0)));
    if (p.sepia) { const mix = I3.map(i => I3.map(j => (1 - p.sepia) * (i == j ? 1 : 0) + p.sepia * SEPIA[i][j])); m = I3.map(i => I3.map(j => mix[i][0] * m[0][j] + mix[i][1] * m[1][j] + mix[i][2] * m[2][j])) }
    const t = p.temperature, ti = p.tint, g = [1 + .18 * t + .05 * ti, 1 - .14 * ti, 1 - .18 * t + .05 * ti];
    const c = p.contrast, slope = 1 + c * (c >= 0 ? .6 : .5), f = p.fade, k = slope * (1 - .2 * f), off = (.5 - .5 * slope + .25 * p.brightness) * (1 - .2 * f) + .1 * f;
    return I3.map(i => [...I3.map(j => clamp(k * g[i] * m[i][j], -2, 2)), off]);
  }
  const matrixValues = rows => rows.map(r => `${r[0].toFixed(5)} ${r[1].toFixed(5)} ${r[2].toFixed(5)} 0 ${r[3].toFixed(5)}`).join(' ') + ' 0 0 0 1 0';
  const rgba = (hex, a) => `rgba(${parseInt(hex.slice(1, 3), 16)},${parseInt(hex.slice(3, 5), 16)},${parseInt(hex.slice(5, 7), 16)},${a})`;

  /* ---------- documento ---------- */
  function fromServer(d) {
    const e = d.edl, s0 = e.source.start, tracks = {}, wordTimed = {};
    for (const lang of e.languages) {
      const tr = d.tracks[lang] || { cues: [] };
      wordTimed[lang] = tr.word_timed ?? lang == e.source_language;
      tracks[lang] = tr.cues.map(c => ({ id: uid(), start: s0 + c.start, end: s0 + c.end, words: c.words.map(w => ({ w: w.w, start: s0 + w.start, end: s0 + w.end, hl: !!w.hl })) }));
    }
    return {
      start: s0, end: e.source.end, layout: e.layout, crop_x: e.crop_x, crop_y: e.crop_y ?? .5, style: e.captions.style,
      lufs: e.audio.normalize_lufs, music_id: e.audio.music_id, duck: e.audio.duck_music,
      hook: { text: { ...e.hook.text }, duration: e.hook.duration, x:e.hook.x??null, y:e.hook.y??null },
      notes: e.commentary.notes.map(n => ({ id: uid(), t: s0 + n.t, duration: n.duration, text: { ...n.text }, x:n.x??null, y:n.y??null })),
      sfx: e.audio.sfx.map(x => ({ id: uid(), t: s0 + x.t, sid: x.id, gain: x.gain ?? 1 })),
      fx: e.effects.map(x => ({ ...x, region: x.region ? { ...x.region } : undefined, id: uid(), t: s0 + x.t, duration: x.duration, type: x.type || 'punch_in', intensity: x.intensity ?? 1 })),
      texts: (e.texts || []).map(x => ({ ...TEXT0, ...x, id: uid(), t: s0 + x.t, text: { ...x.text } })),
      color: { ...COLOR0, ...(e.color || {}) },
      trans: { intro: 'none', intro_d: .4, outro: 'none', outro_d: .4, ...(e.transitions || {}) },
      progress: { on: false, color: '#FFE500', position: 'top', height: 12, ...(e.progress || {}) },
      capX: e.captions.x ?? null, capY: e.captions.y ?? null, capScale: e.captions.scale ?? 1,
      voice: e.audio.voice_gain ?? 1, musicGain: e.audio.music_gain ?? 1, enhance: !!e.audio.voice_enhance,
      tracks, wordTimed, tracking_enabled:e.tracking_enabled!==false, editor_mode: e.editor_mode || 'clip', safe_area: copy(e.safe_area?.profile === d.frame.safe.profile ? {...d.frame.safe,...e.safe_area} : d.frame.safe),
      media: (e.media || []).map(x => ({...x,id:uid(),t:s0+x.t})),
    };
  }
  function toServer() {
    const d = E.doc, s0 = d.start, dur = d.end - d.start, rel = t => r3(clamp(t - s0, 0, dur)), tracks = {};
    for (const [lang, cues] of Object.entries(d.tracks)) {
      tracks[lang] = {
        word_timed: d.wordTimed[lang],
        cues: cues.filter(c => c.end > d.start + .05 && c.start < d.end - .05 && c.words.length)
          .map(c => ({ start: rel(c.start), end: rel(c.end), words: c.words.map(w => ({ w: w.w, start: rel(w.start), end: rel(w.end), hl: w.hl })) })),
      };
    }
    return {
      edl: {
        tracking_enabled:d.tracking_enabled, editor_mode: d.editor_mode, safe_area: d.safe_area,
        media: (d.media || []).filter(x =>
          x.t < d.end && x.t+x.duration > d.start).map(({id,...x}) => ({...x,t:rel(x.t), offset:x.offset+Math.max(0,d.start-x.t),duration:r3(Math.min(x.t+x.duration,d.end)-Math.max(x.t,d.start))})),
        source: { start: r3(d.start), end: r3(d.end) }, layout: d.layout, crop_x: d.crop_x, crop_y: d.crop_y,
        hook: { text: d.hook.text, duration: Math.min(d.hook.duration, dur), x:d.hook.x, y:d.hook.y },
        commentary: { notes: d.notes.filter(n => n.t >= d.start && n.t < d.end - .5).map(n => ({ t: rel(n.t), duration: r3(Math.min(n.duration, d.end - n.t)), text: n.text, x:n.x, y:n.y })) },
        captions: { style: d.style, x:d.capX, y: d.capY, scale: d.capScale },
        audio: {
          normalize_lufs: d.lufs, music_id: d.music_id, duck_music: d.duck, voice_gain: d.voice, music_gain: d.musicGain, voice_enhance: d.enhance,
          sfx: d.sfx.filter(x => x.t >= d.start && x.t < d.end).map(x => ({ t: rel(x.t), id: x.sid, gain: x.gain })),
        },
        effects: d.fx.filter(x => x.t >= d.start && x.t < d.end).map(({ id, ...x }) => ({ ...x, t: rel(x.t), duration: r3(Math.min(x.duration, d.end - x.t)) })),
        texts: d.texts.filter(x => x.t + x.duration > d.start + .2 && x.t < d.end - .2).map(({ id, ...x }) => { const a = Math.max(x.t, d.start); return { ...x, t: rel(a), duration: r3(Math.min(x.t + x.duration, d.end) - a) } }),
        color: d.color, transitions: d.trans, progress: d.progress,
      }, tracks,
    };
  }
  const track = () => E.doc.tracks[E.lang];
  const lib = sid => E.d.library.find(l => l.id == sid) || { id: sid, duration: .5, gain: .6, description: sid };
  const listOf = k => ({ note: E.doc.notes, cue: track(), sfx: E.doc.sfx, fx: E.doc.fx, text: E.doc.texts, media: E.doc.media })[k];
  const minLen = (k, o) => k == 'fx' ? fxSpec(o.type).min : ({ cue: .1, note: .5, text: .3, media:.1 })[k] || 0;
  const maxLen = (k, o) => k == 'media' ? Math.max(.1,(resource(o.resource_id)?.duration || 86400)-(o.offset||0)) : k == 'fx' ? fxSpec(o.type).max : k == 'hook' ? Math.min(8, E.doc.end - E.doc.start) : 1e9;
  const find = (k, id) => k == 'hook' ? E.doc.hook : k == 'clip' ? null : (listOf(k) || []).find(x => x.id == id);
  const selObj = () => E.sel ? find(E.sel.k, E.sel.id) : null;
  function span(k, o) {
    if (k == 'cue') return [o.start, o.end];
    if (k == 'hook') return [E.doc.start, E.doc.start + o.duration];
    if (k == 'sfx') return [o.t, o.t + lib(o.sid).duration];
    return [o.t, o.t + o.duration];
  }
  function setSpan(k, o, a, b) {
    if (k == 'cue') {
      const oa = o.start, ob = o.end, f = (b - a) / Math.max(.001, ob - oa);
      for (const w of o.words) { w.start = r3(a + (w.start - oa) * f); w.end = r3(a + (w.end - oa) * f) }
      o.start = r3(a); o.end = r3(b);
    } else if (k == 'hook') o.duration = r3(b - a);
    else if (k == 'sfx') o.t = r3(a);
    else { o.t = r3(a); o.duration = r3(b - a) }
  }
  const sortDoc = () => { const d = E.doc; d.notes.sort((a, b) => a.t - b.t); d.sfx.sort((a, b) => a.t - b.t); d.fx.sort((a, b) => a.t - b.t); d.texts.sort((a, b) => a.t - b.t); for (const c of Object.values(d.tracks)) c.sort((a, b) => a.start - b.start) };
  const inClip = (a, b) => b > E.doc.start + .01 && a < E.doc.end - .01;

  /* ---------- cambios, deshacer, guardado ---------- */
  function pushUndo() { E.undo.push(JSON.stringify(E.doc)); if (E.undo.length > 120) E.undo.shift(); E.redo = [] }
  function change(fn, { key = null, soft = false } = {}) {
    const now = Date.now();
    if (!(key && key == E.lastKey && now - E.lastAt < 1500)) pushUndo();
    E.lastKey = key; E.lastAt = now;
    fn(E.doc); dirty(); refresh(soft);
  }
  function dirty() { E.rev++; E.save = 'dirty'; clearTimeout(saveTimer); saveTimer = setTimeout(saveDraft, 1200); rTop() }
  function refresh(soft) { layoutStage(); rTimeline(); E.keys = {}; rTop(); if (!soft) { rSide(); rInsp() } }
  function undo(re) {
    const from = re ? E.redo : E.undo, to = re ? E.undo : E.redo;
    if (!from.length) return;
    to.push(JSON.stringify(E.doc)); E.doc = JSON.parse(from.pop()); E.lastKey = null;
    if (E.sel && E.sel.k != 'hook' && E.sel.k != 'clip' && !selObj()) E.sel = null;
    seek(clamp(E.t, E.win.a, E.win.b)); dirty(); refresh();
  }
  async function saveDraft() {
    if (!E) return;
    const me = E, rev = E.rev; E.save = 'saving'; rTop();
    try {
      await send('PUT', `/api/clips/${E.id}/draft`, toServer());
      if (E !== me) return;
      E.hasDraft = true; E.saveErr = ''; if (E.rev == rev) E.save = 'saved';
    } catch (x) { if (E !== me) return; E.save = 'error'; E.saveErr = x.message }
    rTop();
  }

  /* ---------- abrir y cerrar ---------- */
  async function open(id) {
    if (E && E.id == id) return;
    close();
    const ticket = ++opening;
    const root = $('#editor');
    root.innerHTML = `<div class="ed-over"><div class="card" style="display:flex;gap:12px;align-items:center"><span class="spin"></span>Abriendo el editor…</div></div>`;
    let d;
    try { d = await api(`/api/clips/${id}/editor`) } catch (x) { if (ticket !== opening) return; root.innerHTML = ''; toast(x.message, 'er'); location.hash = '#/clips'; return }
    if (ticket !== opening) return;
    E = {
      id, d, lang: d.edl.languages[0], doc: fromServer(d), win: d.window, sel: null, pps: 40, t: d.edl.source.start, playing: false,
      undo: [], redo: [], rev: 0, save: 'saved', saveErr: '', hasDraft: d.has_draft, view: 'live', tab: 'captions', offset: 0,
      resources: [], resourceError: '', safe: !!d.frame.safe.enforced, snap: true, muted: false, keys: {}, wave: null, drag: null, lastT: 0, filmKey: '', music: null,
    };
    if (!cfg.desktop) E.doc.safe_area.enforced = false;
    E.project = d.source.project_id || currentProject;
    mount();
    if(cfg.desktop)loadResources();
  }
  function close() {
    opening++;
    if (!E) { $('#editor').innerHTML = ''; return }
    if (E.save == 'dirty') { clearTimeout(saveTimer); send('PUT', `/api/clips/${E.id}/draft`, toServer()).catch(() => { }) }
    cancelAnimationFrame(raf); clearTimeout(saveTimer);
    document.removeEventListener('keydown', onKey); window.removeEventListener('resize', fit);
    resizeObserver?.disconnect(); resizeObserver = null;
    try { $$('#edMedia video,#edMedia audio').forEach(x=>x.pause()); E.vid.pause(); E.vid.removeAttribute('src'); E.vid.load(); if (E.music) E.music.pause() } catch { }
    E = null; $('#editor').innerHTML = '';
  }

  function mount() {
    const d = E.d, root = $('#editor');
    root.innerHTML = `<div class="ed">
     <div class="ed-top" id="edTop"></div>
     <div class="ed-mobile-nav" role="group" aria-label="Paneles del editor"><button class="b g sm" data-panel="preview">Vista previa</button><button class="b g sm" data-panel="tools">Herramientas</button><button class="b g sm" data-panel="inspector">Propiedades</button></div>
     <div class="ed-work">
      <aside class="ed-side"><div class="ed-tabs" id="edTabs"></div><div class="ed-sidebody" id="edSide"></div></aside>
      <section class="ed-center">
       <div class="ed-stagewrap" id="edWrap">
        <div class="ed-box" id="edBox">
         <div class="ed-stage" id="edStage">
          <svg width="0" height="0" style="position:absolute"><defs>
           <filter id="edColor" color-interpolation-filters="sRGB"><feColorMatrix id="edColorM" type="matrix" values="1 0 0 0 0 0 1 0 0 0 0 0 1 0 0 0 0 0 1 0"/></filter>
           <filter id="edGlitch" color-interpolation-filters="sRGB" x="-4%" width="108%">
            <feColorMatrix in="SourceGraphic" type="matrix" values="1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 1 0"/><feOffset id="edGlR" dx="-14" result="r"/>
            <feColorMatrix in="SourceGraphic" type="matrix" values="0 0 0 0 0 0 0 0 0 0 0 0 1 0 0 0 0 0 1 0"/><feOffset id="edGlB" dx="14" result="b"/>
            <feColorMatrix in="SourceGraphic" type="matrix" values="0 0 0 0 0 0 1 0 0 0 0 0 0 0 0 0 0 0 1 0" result="g"/>
            <feBlend in="r" in2="g" mode="screen" result="rg"/><feBlend in="rg" in2="b" mode="screen"/>
           </filter></defs></svg>
          <div class="ed-pic" id="edPic">
           <canvas class="bg" id="edBg" width="108" height="192"></canvas>
           <div class="ed-fg" id="edFg"><div class="ed-zoom" id="edZoom"><video id="edVid" playsinline preload="auto"></video></div></div>
           <canvas id="edFace" width="496" height="496" style="position:absolute;top:650px;left:464px;pointer-events:none;display:none"></canvas>
          </div>
          <img class="ed-vig" id="edVig" src="/api/studio/vignette.png" alt="" draggable="false">
          <div class="ed-ovl ed-cap" id="edCap" data-k="cue"></div>
          <div class="ed-ovl ed-note" id="edNote" data-k="note"></div>
          <div class="ed-ovl ed-hook" id="edHook" data-k="hook"></div>
          <div id="edMedia" style="position:absolute;inset:0;pointer-events:none"></div><div id="edTexts"></div>
          <div class="ed-prog" id="edProg"></div>
          <div class="ed-full" id="edFlash" style="background:#fff"></div>
          <div class="ed-full" id="edFade" style="background:#000"></div>
          <div class="ed-safe" id="edSafe"><i></i><i></i><i></i><i></i></div>
         </div>
         <video class="ed-final" id="edFinal" controls playsinline hidden></video>
         <div class="ed-stagemsg" id="edMsg"></div>
        </div>
        <div class="ed-banner" id="edBanner"></div>
       </div>
       <div class="ed-transport" id="edTr"></div>
      </section>
      <aside class="ed-insp" id="edInsp"></aside>
     </div>
     <div class="ed-tl">
      <div class="ed-tlbar" id="edBar"></div>
      <div class="ed-tlbody">
       <div class="ed-heads">
        <div id="hdTitle" style="height:${ROW.title}px">${icon('layers')}Títulos</div><div style="height:${ROW.text}px">${icon('text')}Gancho</div><div style="height:${ROW.cap}px">${icon('cc')}Subtítulos</div>
        <div style="height:${ROW.video}px">${icon('film')}Video</div><div id="hdFx" style="height:${ROW.fx}px">${icon('spark')}Efectos</div>
        <div id="hdMedia" style="height:30px">${icon('folder')}Recursos</div><div style="height:${ROW.audio}px">${icon('music')}Audio</div>
       </div>
       <div class="ed-scroll" id="edScroll"><div class="ed-tlc" id="edTl">
        <div class="tl-ruler" id="tlRuler"></div>
        <div class="tl-row" id="tlTitle" style="height:${ROW.title}px"></div>
        <div class="tl-row" id="tlText" style="height:${ROW.text}px"></div>
        <div class="tl-row" id="tlCap" style="height:${ROW.cap}px"></div>
        <div class="tl-row video" id="tlVideo" style="height:${ROW.video}px"><div class="tl-film" id="tlFilm"></div><div id="tlClip"></div></div>
        <div class="tl-row" id="tlFx" style="height:${ROW.fx}px"></div>
        <div class="tl-row" id="tlMedia" style="height:30px"></div><div class="tl-row" id="tlAudio" style="height:${ROW.audio}px"><canvas class="tl-wave" id="tlWave" height="${ROW.audio}"></canvas><div id="tlSfx"></div></div>
        <div class="tl-dim" id="tlDimL" style="left:0"></div><div class="tl-dim" id="tlDimR"></div>
        <div class="tl-snap" id="tlSnap"></div><div class="tl-head" id="tlHead"></div>
       </div></div>
      </div>
     </div></div>`;
    Object.assign(E, { vid: $('#edVid'), bg: $('#edBg'), zoomEl: $('#edZoom'), tl: $('#edTl'), scroll: $('#edScroll'), head: $('#tlHead') });
    mctx = document.createElement('canvas').getContext('2d');
    $('#edTabs').innerHTML = TABS.filter(([k])=>cfg.desktop || k!=='resources').map(([k, l, i]) => `<button data-tab="${k}">${icon(i)}${l}</button>`).join('');
    $('#edTabs').onclick = e => { const b = e.target.closest('[data-tab]'); if (b) { E.tab = b.dataset.tab; rSide() } };

    // video fuente
    const v = E.vid;
    v.addEventListener('loadedmetadata', () => { syncVideo(); stageMsg('') });
    v.addEventListener('seeked', () => { if (E && E.pendingSeek) { E.pendingSeek = false; syncVideo() } drawBg() });
    v.addEventListener('loadeddata', drawBg);
    v.addEventListener('error', useProxy);
    v.addEventListener('waiting', () => E && E.playing && stageMsg('<span class="spin"></span>'));
    v.addEventListener('playing', () => stageMsg(''));
    v.src = d.source.video_url;

    bindTimeline(); bindStage();
    $('.ed').dataset.panel = 'preview';
    $$('.ed-mobile-nav button').forEach(button => button.onclick = () => {
      $('.ed').dataset.panel = button.dataset.panel;
      $$('.ed-mobile-nav button').forEach(b => b.setAttribute('aria-pressed', String(b === button)));
      requestAnimationFrame(fit);
    });
    $('.ed-mobile-nav [data-panel="preview"]').setAttribute('aria-pressed', 'true');
    document.addEventListener('keydown', onKey); window.addEventListener('resize', fit);
    resizeObserver = new ResizeObserver(() => fit()); resizeObserver.observe($('#edWrap'));
    $('#edSide').addEventListener('input', onSideInput); $('#edSide').addEventListener('click', onSideClick);
    $('#edSide').addEventListener('change', onSideChange); $('#edSide').addEventListener('focusin', onSideFocus);
    $('#edInsp').addEventListener('input', onInspInput); $('#edInsp').addEventListener('click', onInspClick); $('#edInsp').addEventListener('change', onInspChange);
    $('#edTop').addEventListener('click', onTopClick); $('#edTr').addEventListener('click', onTransportClick); $('#edBar').addEventListener('click', onBarClick);
    $('#edBar').addEventListener('input', e => { if (e.target.id == 'tlZoom') setPps(+e.target.value, E.t) });

    rTransport(); rBar(); fit(); layoutStage(); refresh(); fitZoom();
    loadFonts().then(() => { if (E) { E.keys = {}; rSide() } });
    api(`/api/clips/${E.id}/waveform?a=${E.win.a}&b=${E.win.b}`).then(w => { if (E && E.id == d.clip.id) { E.wave = w; drawWave() } }).catch(() => { });
    raf = requestAnimationFrame(loop);
  }
  async function useProxy() {
    if (!E || E.proxied) return;
    E.proxied = true;
    await requestProxy();
  }
  async function requestProxy() {
    if(!E || E.proxyLoading)return;
    const me=E, resume=E.playing; pause();E.proxyLoading=true;
    stageMsg('<div><span class="spin"></span><p>Preparando una vista previa ligera…</p></div>');
    const a=Math.max(0,E.t-30),b=Math.min(E.d.source.duration,a+600);
    try {
      const p=await api(`/api/clips/${E.id}/proxy?a=${a}&b=${b}`,{method:'POST'});
      if(E!==me)return;
      E.proxyA=a;E.proxyB=b;E.offset=p.offset;E.vid.src=p.url;E.proxyLoading=false;
      E.vid.addEventListener('loadedmetadata',()=>{if(E!==me)return;if(E.t<a || E.t>=b){requestProxy();return;}syncVideo();if(resume)play();},{once:true});
    }catch(x){if(E===me){E.proxyLoading=false;stageMsg(`<div class="msg">${esc(x.message)}</div>`);}}
  }
  const stageMsg = h => { const m = $('#edMsg'); if (m) { m.innerHTML = h; m.classList.toggle('on', !!h) } };

  /* ---------- reproducción ---------- */
  function syncVideo() {
    if(E.proxied && (E.t<(E.proxyA??Infinity) || E.t>=(E.proxyB??-1))){requestProxy();return;}
    const v = E.vid, target = E.t - E.offset;
    if (v.readyState < 1) return;
    if (v.seeking) { E.pendingSeek = true; return }
    if (Math.abs(v.currentTime - target) > .02) v.currentTime = Math.max(0, target);
  }
  function seek(t) { E.t = clamp(t, E.win.a, E.win.b); if (!E.playing) syncVideo(); updateHead() }
  function play() {
    if (E.view != 'live') setView('live');
    if (E.t >= E.doc.end - .05 || E.t < E.doc.start) E.t = E.doc.start;
    if(E.proxied && (E.t<E.proxyA || E.t>=E.proxyB)){requestProxy();return;}
    const v = E.vid; v.currentTime = Math.max(0, E.t - E.offset); v.muted = E.muted; v.volume = clamp(E.doc.voice, 0, 1); E.lastT = E.t;
    v.play().then(() => { if (E) { E.playing = true; rTransport(); music(true) } }).catch(x => toast('No se pudo reproducir: ' + x.message, 'er'));
  }
  function pause() { if (!E.playing) return; E.playing = false; E.vid.pause(); music(false); rTransport() }
  function music(on) {   // la música de fondo suena en la vista previa (sin el "ducking", que solo se aplica en el render)
    const id = E.doc.music_id;
    if (!on || !id || E.muted) { if (E.music) E.music.pause(); return }
    if (!E.music || E.music.dataset.id != id) { if (E.music) E.music.pause(); E.music = new Audio('/api/sfx/' + id); E.music.loop = true; E.music.dataset.id = id }
    E.music.volume = clamp((lib(id).gain || .22) * E.doc.musicGain, 0, 1);
    try { E.music.currentTime = Math.max(0, E.t - E.doc.start) % (lib(id).duration || 10) } catch { }
    E.music.play().catch(() => { });
  }
  const toggle = () => E.playing ? pause() : play();
  function loop() {
    raf = requestAnimationFrame(loop);
    if (!E) return;
    previewMedia();
    if (E.playing) {
      const t = E.vid.currentTime + E.offset;
      if(E.proxied && t>=E.proxyB-.1 && E.proxyB<E.doc.end){E.t=E.proxyB;requestProxy();return;}
      if (!E.muted) for (const x of E.doc.sfx) if (x.t > E.lastT && x.t <= t && x.t < E.doc.end) beep(x.sid, x.gain);
      E.lastT = t;
      if (t >= E.doc.end) { pause(); E.t = E.doc.end } else E.t = t;
      updateHead(true); drawBg();
    }
    stage();
  }
  function beep(sid, gain = 1) { try { const a = new Audio('/api/sfx/' + sid); a.volume = clamp((lib(sid).gain || .6) * gain, 0, 1); a.play().catch(() => { }) } catch { } }

  /* ---------- escenario (vista previa) ---------- */
  function faceBox(effect) {
    const r = effect.region, s = E.d.source, w = r.w * s.width, h = r.h * s.height, scale = Math.min(360 / w, 200 / h);
    const iw = Math.max(2, Math.floor(w * scale / 2) * 2), ih = Math.max(2, Math.floor(h * scale / 2) * 2);
    const z = E.d.frame.safe;
    return {x:1080 - (z.right ?? z.side) - iw - 16, y:z.top, w:iw+16, h:ih+16, iw, ih};
  }
  const geom = () => {
    const g = {...E.d.geometry[E.doc.layout]};
    for (const f of E.doc.fx.filter(f => f.type == 'face_focus' && f.region)) { const box = faceBox(f); g.top_y = Math.min(1920-E.d.frame.safe.bottom-200,Math.max(g.top_y, box.y + box.h + 150)); }
    const z=E.doc.safe_area;
    if(z.enforced) Object.assign(g,{safe_width:1080-z.left-z.right,top_safe:z.top,bottom_safe:1920-z.bottom,center_x:(z.left+1080-z.right)/2,top_y:z.top+150,caption_y:1920-z.bottom-140});
    return g;
  };
  function fit() {
    if (!E) return;
    const w = $('#edWrap'), k = Math.max(.05, Math.min((w.clientHeight - 28) / 1920, (w.clientWidth - 28) / 1080));
    E.scale = k;
    const b = $('#edBox'); b.style.width = 1080 * k + 'px'; b.style.height = 1920 * k + 'px';
    $('#edStage').style.transform = `scale(${k})`;
  }
  function layoutStage() {
    E.d.frame.safe = {...E.doc.safe_area,width:1080-E.doc.safe_area.left-E.doc.safe_area.right,center_x:(E.doc.safe_area.left+1080-E.doc.safe_area.right)/2};
    const g = geom(), W = 1080, H = 1920, s = E.d.source, fg = $('#edFg'), v = E.vid, blur = E.doc.layout == 'gameplay_blur_bg';
    E.bg.style.display = blur ? '' : 'none';
    if (blur) {
      Object.assign(fg.style, { left: (g.fg_x || 0) + 'px', top: g.fg_y + 'px', width: (g.fg_w || W) + 'px', height: g.fg_h + 'px' });
      Object.assign(v.style, { left: '0px', top: '0px', width: (g.fg_w || W) + 'px', height: g.fg_h + 'px' });
    } else {
      const k = Math.max(W / s.width, H / s.height), vw = s.width * k, vh = s.height * k;
      Object.assign(fg.style, { left: '0px', top: '0px', width: W + 'px', height: H + 'px' });
      Object.assign(v.style, { left: -(vw - W) * E.doc.crop_x + 'px', top: -(vh - H) * (E.doc.crop_y ?? .5) + 'px', width: vw + 'px', height: vh + 'px' });
    }
    $('#edHook').style.top = $('#edNote').style.top = g.top_y + 'px';
    const safe = E.d.frame.safe;
    for (const id of ['edHook','edNote','edCap']) $('#'+id).style.left = (g.center_x ?? 540) + 'px';
    let capY = E.doc.capY != null ? Math.round(E.doc.capY * H) : g.caption_y;
    if (safe.enforced) capY = clamp(capY, safe.top+140, H-safe.bottom-140);
    $('#edCap').style.top = capY + 'px';
    if (safe.enforced) Object.assign($('#edTexts').style, { position:'absolute',inset:0,pointerEvents:'none',clipPath:`inset(${safe.top}px ${safe.right}px ${safe.bottom}px ${safe.left}px)` });
    // color: una sola matriz, la misma que usa el render
    const cp = colorParams(E.doc.color), rows = colorAffine(cp), ident = rows.every((r, i) => r.every((v, j) => Math.abs(v - (i == j ? 1 : 0)) < 1e-4));
    E.grade = ident ? '' : 'url(#edColor) ';
    if (!ident) $('#edColorM').setAttribute('values', matrixValues(rows));
    $('#edVig').style.opacity = cp.vignette;
    const pg = E.doc.progress, bar = $('#edProg');
    Object.assign(bar.style, { display: pg.on ? 'block' : 'none', background: pg.color, height: pg.height + 'px', left:(safe.enforced?safe.left:0)+'px', top: pg.position == 'top' ? (safe.enforced?safe.top:0)+'px' : 'auto', bottom: pg.position == 'top' ? 'auto' : (safe.enforced?safe.bottom:0)+'px' });
    const z = E.d.frame.safe, [a, b, c, e] = $$('#edSafe i');
    Object.assign(a.style, { left: 0, right: 0, top: 0, height: z.top + 'px', borderBottomWidth: '3px' });
    Object.assign(b.style, { left: 0, right: 0, bottom: 0, height: z.bottom + 'px', borderTopWidth: '3px' });
    Object.assign(c.style, { left: 0, top: z.top + 'px', bottom: z.bottom + 'px', width: (z.left??z.side) + 'px', borderRightWidth: '3px' });
    Object.assign(e.style, { right: 0, top: z.top + 'px', bottom: z.bottom + 'px', width: (z.right??z.side) + 'px', borderLeftWidth: '3px' });
    $('#edSafe').classList.toggle('on', E.safe);
    drawBg();
  }
  function drawBg() {
    if (!E || E.doc.layout != 'gameplay_blur_bg') return;
    const v = E.vid; if (v.readyState < 2 || !v.videoWidth) return;
    const c = E.bg, k = Math.max(c.width / v.videoWidth, c.height / v.videoHeight), w = v.videoWidth * k, h = v.videoHeight * k;
    try { c.getContext('2d').drawImage(v, (c.width - w) / 2, (c.height - h) / 2, w, h) } catch { }
  }
  const tw = (text, font, size) => { mctx.font = `${font.w} ${size * (font.s || .83)}px ${font.f}`; return mctx.measureText(text).width };   // size: tamaño ASS (alto de línea)
  function greedy(text, font, size, max) {
    const lines = []; let cur = '';
    for (const word of text.split(/\s+/).filter(Boolean)) {
      if (E.d.frame.safe.enforced && tw(word,font,size)>max) {
        if (cur) { lines.push(cur); cur=''; }
        for (const char of [...word]) { const part=cur+char; if(cur && tw(part,font,size)>max) { lines.push(cur);cur=char; } else cur=part; }
        continue;
      }
      const trial = (cur + ' ' + word).trim();
      if (cur && tw(trial, font, size) > max) { lines.push(cur); cur = word } else cur = trial;
    }
    if (cur) lines.push(cur); return lines;
  }
  function wrap(text, font, size, max) {   // igual que captions.wrap del render
    let lines = greedy(text, font, size, max); if (lines.length < 2) return lines;
    let lo = max * .5, hi = max;
    for (let i = 0; i < 8; i++) {
      const mid = (lo + hi) / 2, trial = greedy(text, font, size, mid);
      if (trial.length <= lines.length && trial.every(l => tw(l, font, size) <= max)) { hi = mid; lines = trial } else lo = mid;
    }
    return lines;
  }
  function stage() {
    updateTracking();
    const d = E.doc, t = E.t, it = t - d.start, dur = d.end - d.start, g = geom(), L = E.lang, K = E.keys, FS = { caption: fscale('caption'), text: fscale('text') }, on = t >= d.start - .001 && t < d.end, sel = E.sel || {};
    const face = on && d.fx.find(x => x.type == 'face_focus' && x.region && t >= x.t && t < x.t + x.duration), faceCanvas = $('#edFace');
    faceCanvas.style.display = face ? '' : 'none';
    if (face && E.vid.readyState >= 2 && E.vid.videoWidth) {
      const r = face.region, sw = E.vid.videoWidth, sh = E.vid.videoHeight;
      const cw = Math.max(2, Math.floor(r.w * sw / 2) * 2), ch = Math.max(2, Math.floor(r.h * sh / 2) * 2), box = faceBox(face);
      const x = Math.floor(r.x * sw / 2) * 2, y = Math.floor(r.y * sh / 2) * 2;
      if (faceCanvas.width != box.w) faceCanvas.width = box.w;
      if (faceCanvas.height != box.h) faceCanvas.height = box.h;
      const c = faceCanvas.getContext('2d'); c.fillStyle = '#FFE500'; c.fillRect(0, 0, box.w, box.h);
      try { c.drawImage(E.vid, x, y, cw, ch, 8, 8, box.iw, box.ih); } catch { }
      faceCanvas.style.left = box.x - 80 * (1 - clamp((t - face.t) / .18, 0, 1)) + 'px';
      faceCanvas.style.top = box.y + 'px';
      faceCanvas.style.opacity = Math.min(clamp((t - face.t) / .12, 0, 1), clamp((face.t + face.duration - t) / .12, 0, 1));
    }
    // cámara (zoom, zoom lento, temblor) y efectos con duración: las mismas cuentas que media/render.py
    const C = E.d.catalog.fx; let pz = 0, sl = 0, sh = 0, blur = 0, bw = 0, gl = 0, flash = 0;
    if (on) for (const x of d.fx) {
      const a = x.t - d.start, b = a + x.duration, I = x.intensity, inside = it >= a && it <= b;
      if (x.type == 'punch_in') pz += I * clamp((it - a) / C.ramp, 0, 1) * clamp((b - it) / C.ramp, 0, 1);
      else if (!inside) continue;
      else if (x.type == 'zoom_slow') sl += I * (it - a) / x.duration;
      else if (x.type == 'shake') sh += I;
      else if (x.type == 'blur') blur = Math.max(blur, C.blur_sigma * I);
      else if (x.type == 'bw') bw = 1;
      else if (x.type == 'glitch') gl = Math.max(gl, Math.max(2, Math.round(C.glitch_px * I)));
      else if (x.type == 'flash') flash += C.flash * I * (1 - (it - a) / x.duration);
    }
    const env = Math.min(2, sh), z = 1 + C.zoom * Math.min(2, pz) + C.slow * Math.min(2, sl) + C.shake_zoom * env, w = 2 * Math.PI * C.shake_hz;
    const dx = C.shake_px * env * Math.sin(w * it), dy = C.shake_px * env * Math.cos(w * 1.3 * it), zKey = z.toFixed(4) + '|' + dx.toFixed(1) + '|' + dy.toFixed(1);
    if (zKey != K.z) { E.zoomEl.style.transform = z == 1 && !env ? '' : `translate(${(-dx).toFixed(1)}px,${(-dy).toFixed(1)}px) scale(${z.toFixed(4)})`; K.z = zKey }
    const filt = E.grade + (blur ? `blur(${blur.toFixed(1)}px) ` : '') + (bw ? 'grayscale(1) ' : '') + (gl ? 'url(#edGlitch)' : '');
    if (filt != K.filt) { K.filt = filt; if (gl) { $('#edGlR').setAttribute('dx', -gl); $('#edGlB').setAttribute('dx', gl) } $('#edPic').style.filter = filt.trim() || 'none' }
    // destellos, fundidos y barra de progreso
    const tr = d.trans;
    if (on && tr.intro == 'flash' && it < tr.intro_d) flash += C.flash * 1.2 * (1 - it / tr.intro_d);
    let fade = 0;
    if (on && tr.intro == 'fade' && it < tr.intro_d) fade = 1 - it / tr.intro_d;
    if (on && tr.outro == 'fade' && it > dur - tr.outro_d) fade = Math.max(fade, (it - (dur - tr.outro_d)) / tr.outro_d);
    const fl = Math.min(1, flash).toFixed(3), fd = clamp(fade, 0, 1).toFixed(3);
    if (fl != K.fl) { K.fl = fl; $('#edFlash').style.opacity = fl }
    if (fd != K.fd) { K.fd = fd; $('#edFade').style.opacity = fd }
    if (d.progress.on) $('#edProg').style.width = (clamp(it / dur, 0, 1) * (E.d.frame.safe.enforced ? E.d.frame.safe.width : 1080)).toFixed(2) + 'px';
    // textos libres
    const A = E.d.catalog.anim, live = on ? d.texts.filter(x => t >= x.t && t < Math.min(d.end, x.t + x.duration) && (x.text[L] || '').trim()) : [];
    const tKey = live.map(x => x.id + (sel.k == 'text' && sel.id == x.id ? '*' : '')).join(',') + '|' + L + '|' + E.rev;
    if (tKey != K.texts) {
      K.texts = tKey;
      E.textPlacements = {};
      $('#edTexts').innerHTML = live.map(x => {
        const f = font(x.font), max = 1080 - 2 * E.d.text_layout.margin, pad = E.d.text_layout.box_pad;
        const placement = E.d.frame.safe.enforced ? SafeLayout.text(x,L,E.d.frame.safe,fscale(x.font),(s,fs)=>tw(s,f,fs),(s,fs,w)=>wrap(s,f,fs,w)) : null;
        E.textPlacements[x.id] = placement;
        const lines = placement ? placement.lines : (x.text[L] || '').split('\n').map(s => (x.upper ? s.toUpperCase() : s).trim()).filter(Boolean).flatMap(p => wrap(p, f, x.size / f.s, max));
        const look = x.box ? `background:${rgba(x.box, x.box_opacity)};padding:${pad - 2}px ${pad}px;`
          : `-webkit-text-stroke:${x.stroke_w * 2}px ${x.stroke};paint-order:stroke fill;${x.glow ? `text-shadow:0 0 ${x.glow * 2.5}px ${x.stroke},0 0 ${x.glow * 5}px ${x.stroke};` : ''}`;
        return `<div class="ed-ovl ed-txt ${sel.k == 'text' && sel.id == x.id ? 'sel' : ''}" data-k="text" data-id="${x.id}" style="pointer-events:auto;left:${placement?placement.x:x.x * 1080}px;top:${placement?placement.y:x.y * 1920}px;font-family:${f.f.replace(/"/g, "'")};font-weight:${f.w};font-size:${placement?placement.em:x.size}px;line-height:${placement?placement.fs:(x.size / fscale(x.font)).toFixed(1)}px;color:${x.color};${look}">${lines.map(esc).join('<br>')}</div>`;
      }).join('');
    }
    live.forEach((x, i) => {
      const el = $('#edTexts').children[i]; if (!el) return;
      const tau = t - x.t, left = Math.min(d.end, x.t + x.duration) - t;
      let op = x.opacity, sc = 1, ox = 0, oy = 0;
      const fin = x.anim_in == 'none' ? 0 : x.anim_in == 'pop' ? .08 : A.fade;
      if (fin && tau < fin) op *= tau / fin;
      if (x.anim_out == 'fade' && left < A.fade) op *= left / A.fade;
      if (x.anim_in == 'pop') sc = tau < A.pop_a ? .4 + .72 * tau / A.pop_a : tau < A.pop_b ? 1.12 - .12 * (tau - A.pop_a) / (A.pop_b - A.pop_a) : 1;
      else if (x.anim_in == 'zoom' && tau < A.zoom) sc = 1.6 - .6 * tau / A.zoom;
      else if (x.anim_in.startsWith('slide') && tau < A.slide) { const k = A.slide_px * (1 - tau / A.slide); ({ slide_up: () => oy = k, slide_down: () => oy = -k, slide_left: () => ox = k, slide_right: () => ox = -k })[x.anim_in]() }
      if (x.anim_out == 'shrink' && left < A.shrink) sc *= left / A.shrink;
      el.style.opacity = op.toFixed(3);
      el.style.transform = `translate(calc(-50% + ${ox.toFixed(1)}px),calc(-50% + ${oy.toFixed(1)}px)) rotate(${x.rotate}deg) scale(${sc.toFixed(3)})`;
    });
    // gancho
    const hk = on && it < d.hook.duration ? (d.hook.text[L] || '').toUpperCase() : '';
    const hkKey = hk + '|' + (sel.k == 'hook');
    if (hkKey != K.hook) {
      K.hook = hkKey; const el = $('#edHook');
      if (!hk) el.innerHTML = ''; else {
        let size = 86, lines = wrap(hk, font('caption'), size, g.safe_width - 40);
        while (lines.length > 3 && size > 56) { size -= 8; lines = wrap(hk, font('caption'), size, g.safe_width - 40) }
        Object.assign(el.style, { fontSize: size * FS.caption + 'px', lineHeight: size + 'px' }); el.innerHTML = lines.map(esc).join('<br>');
        if (E.d.frame.safe.enforced) { const half=Math.ceil(lines.length*size/2+18); el.style.top=clamp(g.top_y,E.d.frame.safe.top+half,1920-E.d.frame.safe.bottom-half)+'px'; }
      }
      el.classList.toggle('sel', sel.k == 'hook');
    }
    // nota
    const n = on ? d.notes.find(n => t >= n.t && t < n.t + n.duration) : null, nt = n ? (n.text[L] || '') : '';
    const nKey = (n ? n.id : '') + nt + '|' + (sel.k == 'note' && n && sel.id == n.id);
    if (nKey != K.note) {
      K.note = nKey; const el = $('#edNote');
      Object.assign(el.style, { fontSize: 46 * FS.text + 'px', lineHeight: '46px' });
      const lines = nt ? wrap(nt, font('text'), 46, g.safe_width - 40) : [];
      el.innerHTML = lines.map(esc).join('<br>');
      if (E.d.frame.safe.enforced) { const half=Math.ceil(lines.length*46/2+16); el.style.top=clamp(g.top_y,E.d.frame.safe.top+half,1920-E.d.frame.safe.bottom-half)+'px'; }
      el.dataset.id = n ? n.id : ''; el.classList.toggle('sel', !!n && sel.k == 'note' && sel.id == n.id);
    }
    // subtítulo
    const cue = on ? track().find(c => t >= c.start && t < c.end) : null;
    let k = 0; if (cue) for (let i = 0; i < cue.words.length; i++) if (cue.words[i].start <= t) k = i;
    const cKey = cue ? cue.id + '|' + k + '|' + d.style + '|' + d.capScale + '|' + cue.words.map(w => w.w + (w.hl ? '*' : '')).join(' ') + '|' + (sel.k == 'cue' && sel.id == cue.id) : '';
    if (cKey != K.cue) {
      K.cue = cKey; const el = $('#edCap');
      if (!cue || !cue.words.length) el.innerHTML = ''; else {
        const sp = E.d.styles[d.style], cf = font(sp.font), up = s => sp.upper ? s.toUpperCase() : s, base = Math.round(sp.size * d.capScale);
        const width = tw(up(cue.words.map(w => w.w).join(' ')), cf, base) * 1.2 + 2 * sp.outline;
        const size = width <= g.safe_width ? base : Math.max(E.d.frame.safe.enforced?12:40, Math.floor(base * g.safe_width / width));
        Object.assign(el.style, {
          fontFamily: cf.f, fontWeight: cf.w, fontSize: size * fscale(sp.font) + 'px', color: sp.color, webkitTextStroke: `${sp.outline * 2}px ${sp.outline_color}`,
          textShadow: sp.glow ? `0 0 ${sp.glow * 2.5}px ${sp.outline_color},0 0 ${sp.glow * 5}px ${sp.outline_color}` : '',
        });
        el.innerHTML = cue.words.map((w, j) => `<span style="${j == k ? `color:${sp.active};font-size:1.06em` : w.hl ? `color:${sp.keyword}` : ''}">${esc(up(w.w))}</span>`).join(' ');
      }
      el.dataset.id = cue ? cue.id : ''; el.classList.toggle('sel', !!cue && sel.k == 'cue' && sel.id == cue.id);
      if (K.cur != (cue ? cue.id : '')) {
        K.cur = cue ? cue.id : '';
        $$('#edSide .cue.cur').forEach(x => x.classList.remove('cur'));
        const row = cue && $(`#edSide .cue[data-id="${cue.id}"]`);
        if (row) { row.classList.add('cur'); if (E.playing) row.scrollIntoView({ block: 'nearest' }) }
      }
    }
    const positionBlock=(id,object,defaultY)=>{
      const el=$('#'+id);if(!el.textContent)return;
      const z=E.d.frame.safe,left=z.left??z.side??0,right=1080-(z.right??z.side??0);
      const pad=id==='edCap'?1.2:1,hw=Math.min((right-left)/2,el.offsetWidth*pad/2+4),hh=Math.min((1920-z.bottom-z.top)/2,el.offsetHeight*pad/2+4);
      const x=(object?.x??((g.center_x??540)/1080))*1080,y=(object?.y??(defaultY/1920))*1920;
      el.style.left=clamp(x,left+hw,right-hw)+'px';el.style.top=clamp(y,z.top+hh,1920-z.bottom-hh)+'px';
    };
    positionBlock('edHook',d.hook,g.top_y);
    positionBlock('edNote',n,g.top_y);
    positionBlock('edCap',{x:d.capX,y:d.capY},g.caption_y);
    // reloj
    const clock = tc(clamp(it, 0, d.end - d.start)) + ' <span>/ ' + tc(d.end - d.start) + '</span>';
    if (clock != K.clock) { K.clock = clock; const el = $('#edClock'); if (el) el.innerHTML = clock }
  }
  function bindStage() {
    const st = $('#edStage');
    st.addEventListener('pointerdown', e => {
      const o = e.target.closest('.ed-ovl');
      if (o) {
        select(o.dataset.k == 'hook' ? { k: 'hook' } : { k: o.dataset.k, id: o.dataset.id });
        if (!['text','hook','note','cue'].includes(o.dataset.k)) return;
        e.preventDefault(); pause();
        const kind=o.dataset.k, target=kind==='cue'?E.doc:find(kind,o.dataset.id);
        if(!target)return;
        const x0=e.clientX,y0=e.clientY;
        // Start from the displayed position, including safe-area clamping.
        const p0={x:parseFloat(o.style.left)/1080,y:parseFloat(o.style.top)/1920};
        const center=(E.d.frame.safe.center_x??540)/1080;
        let moved=false;
        st.setPointerCapture(e.pointerId);
        const mv=ev=>{
          const dx=(ev.clientX-x0)/E.scale/1080,dy=(ev.clientY-y0)/E.scale/1920;
          if(!moved&&Math.hypot(dx*1080,dy*1920)<4)return;
          if(!moved){pushUndo();moved=true;}
          let x=clamp(p0.x+dx,0,1),y=clamp(p0.y+dy,0,1);
          if(E.snap&&!ev.altKey&&Math.abs(x-center)<.015)x=center;
          if(kind==='cue'){target.capX=r3(x);target.capY=r3(y);}
          else{target.x=r3(x);target.y=r3(y);}
          E.keys={};stage();
        };
        const up=()=>{
          st.removeEventListener('pointermove',mv);st.removeEventListener('pointerup',up);st.removeEventListener('pointercancel',up);
          if(st.hasPointerCapture(e.pointerId))st.releasePointerCapture(e.pointerId);
          if(moved){dirty();rInsp();}
        };
        st.addEventListener('pointermove',mv);st.addEventListener('pointerup',up);st.addEventListener('pointercancel',up);
        return;
      }
      if (E.doc.layout != 'center_crop') return;
      const s = E.d.source, k = Math.max(1080 / s.width, 1920 / s.height), free = s.width * k - 1080, freeY = s.height * k - 1920;
      if (free < 2 && (!cfg.desktop || freeY < 2)) return;
      const x0 = e.clientX, y0 = e.clientY, c0 = E.doc.crop_x, cy0 = E.doc.crop_y ?? .5; let moved = false;
      st.setPointerCapture(e.pointerId);
      const mv = ev => {
        const dx = (ev.clientX - x0) / E.scale, dy = (ev.clientY - y0) / E.scale; if (!moved && Math.max(Math.abs(dx), Math.abs(dy)) < 4) return;
        if (!moved) { moved = true; pushUndo() }
        E.doc.tracking_enabled=false;
        if (free >= 2) E.doc.crop_x = r3(clamp(c0 - dx / free, 0, 1));
        if (cfg.desktop && freeY >= 2) E.doc.crop_y = r3(clamp(cy0 - dy / freeY, 0, 1)); layoutStage();
      };
      const up = () => { st.removeEventListener('pointermove', mv); st.removeEventListener('pointerup', up); if (moved) { dirty(); rSide() } };
      st.addEventListener('pointermove', mv); st.addEventListener('pointerup', up);
    });
  }
  function setView(v) {
    if (v == 'final' && !E.d.renders[E.lang]) { toast('Este clip todavía no tiene render.', 'er'); return }
    pause(); E.view = v;
    const f = $('#edFinal');
    $('#edStage').style.visibility = v == 'live' ? '' : 'hidden'; f.hidden = v == 'live';
    if (v == 'final') { f.src = E.d.renders[E.lang] + '?v=' + Date.now(); f.play().catch(() => { }) } else { f.pause(); f.removeAttribute('src') }
    rTransport(); rTop();
  }

  /* ---------- barra superior, transporte, herramientas ---------- */
  function rTop() {
    const d = E.d, st = { saved: E.hasDraft ? 'Borrador guardado' : 'Sin cambios', dirty: 'Cambios sin guardar', saving: 'Guardando…', error: E.saveErr || 'No se pudo guardar' }[E.save];
    const dur = E.doc.end - E.doc.start, pending = E.hasDraft || E.save != 'saved';
    $('#edTop').innerHTML = `
      <button class="b g sm" data-a="back">${icon('back')}Clips</button>
      <span class="ttl" title="${esc(E.id)}">${esc(E.doc.hook.text[E.lang] || E.id)}</span>${pill(d.clip.status)}
      <div class="seg">${d.edl.languages.map(l => `<button data-lang="${l}" class="${l == E.lang ? 'on' : ''}">${l.toUpperCase()}</button>`).join('')}</div>
      <span class="sp"></span>
      <button class="b g ic" data-a="undo" title="Deshacer (Ctrl+Z)" ${E.undo.length ? '' : 'disabled'}>${icon('undo')}</button>
      <button class="b g ic" data-a="redo" title="Rehacer (Ctrl+Y)" ${E.redo.length ? '' : 'disabled'}>${icon('redo')}</button>
      <span class="state ${E.save == 'error' ? 'err' : pending ? 'dirty' : ''}" title="${esc(E.saveErr)}"><i></i>${esc(st)}</span>
      ${E.hasDraft ? `<button class="b g sm" data-a="discard">Descartar</button>` : ''}
      <button class="b" data-a="render" ${dur < d.limits.min || dur > d.limits.max ? 'disabled' : ''}>${icon('render')}Renderizar</button>`;
    const bn = $('#edBanner');
    bn.innerHTML = E.view == 'final' && pending ? `<span class="pill wa">Este render no incluye tus últimos cambios</span>` : '';
  }
  function onTopClick(e) {
    const lang = e.target.closest('[data-lang]');
    if (lang) { E.lang = lang.dataset.lang; if (E.sel && E.sel.k == 'cue') E.sel = null; if (E.view == 'final') setView('final'); refresh(); return }
    const a = e.target.closest('[data-a]'); if (!a || a.disabled) return;
    ({ back: () => location.hash = '#/clips', undo: () => undo(false), redo: () => undo(true), render: renderNow, discard })[a.dataset.a]();
  }
  function rTransport() {
    const dur = E.doc.end - E.doc.start, live = E.view == 'live';
    $('#edTr').innerHTML = `
      <span class="tc" id="edClock">${tc(clamp(E.t - E.doc.start, 0, dur))} <span>/ ${tc(dur)}</span></span>
      <span class="sp"></span>
      <button class="b g ic" data-a="first" title="Ir al inicio (Inicio)" ${live ? '' : 'disabled'}>${icon('first')}</button>
      <button class="b ic play" data-a="play" title="Reproducir / pausar (Espacio)">${icon(E.playing ? 'pause' : 'play')}</button>
      <button class="b g ic" data-a="last" title="Ir al final (Fin)" ${live ? '' : 'disabled'}>${icon('last')}</button>
      <span class="sp"></span>
      <button class="b g ic" data-a="mute" title="Silenciar">${icon(E.muted ? 'mute' : 'vol')}</button>
      <button class="b g sm ${E.safe ? '' : ''}" data-a="safe" title="${E.d.frame.safe.enforced ? 'Mostrar u ocultar la guía. La protección del render permanece activa.' : 'Zonas donde las apps tapan el video'}" style="${E.safe ? 'border-color:var(--ac);color:#fff' : ''}">${icon('eye')}Zona segura</button>${cfg.desktop ? `<button class="b g sm" data-a="safelock" aria-label="${E.doc.safe_area.locked===false?'Bloquear zona segura':'Desbloquear zona segura'}">${icon(E.doc.safe_area.locked===false?'unlock':'lock')}</button>` : ''}
      <div class="seg"><button data-view="live" class="${live ? 'on' : ''}">En vivo</button><button data-view="final" class="${live ? '' : 'on'}">Render final</button></div>`;
    E.keys.clock = null;
  }
  function onTransportClick(e) {
    const v = e.target.closest('[data-view]'); if (v) { setView(v.dataset.view); return }
    const a = e.target.closest('[data-a]'); if (!a || a.disabled) return;
    ({
      play: toggle, first: () => { pause(); seek(E.doc.start) }, last: () => { pause(); seek(E.doc.end - .03) },
      safelock: () => { safeAction('lock'); E.tab='format';rSide();rTransport(); }, mute: () => { E.muted = !E.muted; E.vid.muted = E.muted; rTransport() }, safe: () => { E.safe = !E.safe; layoutStage(); rTransport() },
    })[a.dataset.a]();
  }
  function rBar() {
    $('#edBar').innerHTML = `
      <button class="b g sm" data-a="in" title="El clip empieza en el cursor (I)">[ Inicio aquí</button>
      <button class="b g sm" data-a="out" title="El clip termina en el cursor (O)">Fin aquí ]</button>
      <span class="sep"></span>
      <button class="b g sm" data-a="text" title="Texto en el cursor (T)">${icon('plus')}Texto</button>
      <button class="b g sm" data-a="note" title="Nota en el cursor (N)">${icon('plus')}Nota</button>
      <button class="b g sm" data-a="cue" title="Subtítulo en el cursor">${icon('plus')}Subtítulo</button>
      <button class="b g sm" data-a="fx" title="Zoom en el cursor (Z)">${icon('plus')}Zoom</button>
      <span class="sep"></span>
      <button class="b g sm" data-a="split" title="Dividir el subtítulo en el cursor (S)">${icon('split')}Dividir</button>
      <button class="b g sm" data-a="del" title="Eliminar lo seleccionado (Supr)">${icon('trash')}</button>
      <span class="sp"></span>
      <button class="b g ic" id="tlSnapBtn" data-a="snap" title="Imán: ajustar a palabras, cursor y bordes (Alt lo desactiva)">${icon('magnet')}</button>
      <button class="b g ic" data-a="fit" title="Ver todo el clip">${icon('fit')}</button>
      ${icon('zoomin')}<input type="range" id="tlZoom" min="0.02" max="240" step="0.02" value="${E.pps}">`;
    $('#tlSnapBtn').style.cssText = E.snap ? 'border-color:var(--cy);color:var(--cy)' : '';
  }
  function onBarClick(e) {
    const a = e.target.closest('[data-a]'); if (!a) return;
    ({ in: () => setEdge('start'), out: () => setEdge('end'), text: () => addText(), note: addNote, cue: addCue, fx: () => addFx(), split: splitCue, del: delSel, fit: fitZoom, snap: () => { E.snap = !E.snap; rBar() } })[a.dataset.a]();
  }

  /* ---------- línea de tiempo ---------- */
  const X = t => (t - E.win.a) * E.pps;
  const itemHtml = (k, o, label, extra = '', css = '') => {
    const [a, b] = span(k, o), w = Math.max(k == 'sfx' ? 20 : 4, (b - a) * E.pps), sel = E.sel && E.sel.k == k && (k == 'hook' || E.sel.id == o.id);
    return `<div class="it ${k} ${sel ? 'sel' : ''} ${inClip(a, b) ? '' : 'out'} ${extra}" data-k="${k}" data-id="${o.id || ''}" style="left:${X(a).toFixed(1)}px;width:${w.toFixed(1)}px;${css}" title="${esc(label)}">
      ${k == 'sfx' ? '' : `${k == 'hook' ? '' : '<i class="hd l"></i>'}<i class="hd r"></i>`}<span>${esc(label)}</span></div>`;
  };
  function rTimeline() {
    const d = E.doc, L = E.lang, total = (E.win.b - E.win.a) * E.pps;
    E.tl.style.width = total + 'px';
    // regla: tiempos relativos al inicio del clip
    const step = [.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600].find(s => s * E.pps >= 64) || 3600, first = Math.ceil((E.win.a - d.start) / step) * step;
    let h = '';
    for (let r = first; d.start + r <= E.win.b; r += step) h += `<div class="tk ${r < 0 ? 'neg' : ''}" style="left:${X(d.start + r).toFixed(1)}px">${Math.abs(r) < 1e-6 ? '0:00' : tc(r).replace(/\.0$/, '')}</div>`;
    $('#tlRuler').innerHTML = h;
    $('#tlText').innerHTML = (d.hook.text[L] || d.hook.duration > 0 ? itemHtml('hook', d.hook, d.hook.text[L] || 'Gancho') : '') + d.notes.map(n => itemHtml('note', n, n.text[L] || 'Nota')).join('');
    $('#tlCap').innerHTML = track().map(c => itemHtml('cue', c, c.words.map(w => w.w).join(' '), c.untr ? 'untr' : '')).join('');
    // textos y efectos pueden solaparse: cada uno va al primer carril libre y la fila crece
    const laneRow = (row, head, base, k, list, label, cls) => {
      const packed=EditorCore.lanes(list,x=>[x.t,x.t+x.duration]),lane=packed.positions;
      const n = packed.count, H = n > 1 ? Math.max(base, n * 15 + 6) : base, lh = (H - 6) / n;
      $(row).style.height = $(head).style.height = H + 'px';
      $(row).innerHTML = list.map((x, i) => itemHtml(k, x, label(x), cls(x), `top:${3 + lane[i] * lh}px;height:${lh - 1}px;bottom:auto;font-size:${Math.min(11.5, lh - 3)}px`)).join('');
    };
    laneRow('#tlFx', '#hdFx', ROW.fx, 'fx', d.fx, x => fxSpec(x.type).label, x => 'fx-' + x.type);
    laneRow('#tlTitle', '#hdTitle', ROW.title, 'text', d.texts, x => (x.text[L] || 'Texto').replace(/\n/g, ' '), () => '');
    laneRow('#tlMedia','#hdMedia',30,'media',d.media,x=>resource(x.resource_id)?.name || 'Recurso',()=> '');
    $('#tlSfx').innerHTML = d.sfx.map(x => itemHtml('sfx', x, '♪ ' + x.sid.replace(/_\d+$/, ''))).join('');
    const xa = X(d.start), xb = X(d.end);
    $('#tlClip').innerHTML = `<div class="tl-clip" style="left:${xa}px;width:${xb - xa}px"></div><div class="trim l" style="left:${xa}px" title="Arrastra para cambiar el inicio"></div><div class="trim r" style="left:${xb}px" title="Arrastra para cambiar el final"></div>`;
    $('#tlDimL').style.width = xa + 'px'; Object.assign($('#tlDimR').style, { left: xb + 'px', width: Math.max(0, total - xb) + 'px' });
    rFilm(); updateHead();
  }
  function rFilm() {
    const key = E.pps + '|' + E.win.a + '|' + E.win.b; if (key == E.filmKey) return; E.filmKey = key;
    const s = E.d.source, tileW = Math.max(40, Math.round(ROW.video * s.width / s.height)), total = (E.win.b - E.win.a) * E.pps, n = Math.min(500, Math.ceil(total / tileW));
    let h = '';
    for (let i = 0; i < n; i++) { const t = E.win.a + (i + .5) * (E.win.b-E.win.a)/n; h += `<img loading="lazy" draggable="false" style="left:${i * total/n}px;width:${total/n}px" src="/api/sources/${s.id}/frame?t=${(Math.round(t * 2) / 2).toFixed(1)}&h=72">` }
    $('#tlFilm').innerHTML = h;
    $('#tlWave').style.width = total + 'px';
  }
  function drawWave() {
    const w = E.wave, c = $('#tlWave'); if (!w || !w.peaks.length || !c) return;
    const n = w.peaks.length, W = Math.min(n, 8000), H = c.height, g = c.getContext('2d');
    c.width = W; g.fillStyle = '#8a8a8a';
    for (let x = 0; x < W; x++) { let p = 0; const i0 = Math.floor(x * n / W), i1 = Math.max(i0 + 1, Math.floor((x + 1) * n / W)); for (let i = i0; i < i1; i++) p = Math.max(p, w.peaks[i]); const hh = Math.max(1, p * (H - 6)); g.fillRect(x, (H - hh) / 2, 1, hh) }
  }
  function updateHead(follow) {
    const x = X(E.t); E.head.style.transform = `translateX(${x}px)`;
    if (follow) { const sc = E.scroll; if (x < sc.scrollLeft + 20 || x > sc.scrollLeft + sc.clientWidth - 40) sc.scrollLeft = x - sc.clientWidth * .2 }
  }
  function setPps(p, anchor) {
    const sx = X(anchor) - E.scroll.scrollLeft; E.pps = clamp(p, .02, 240);
    rTimeline(); E.scroll.scrollLeft = X(anchor) - sx;
    const z = $('#tlZoom'); if (z) z.value = E.pps;
  }
  function fitZoom() {
    const w = E.scroll.clientWidth || 900; E.pps = clamp((w - 120) / (E.doc.end - E.doc.start), .02, 240);
    rTimeline(); E.scroll.scrollLeft = X(E.doc.start) - 60;
    const z = $('#tlZoom'); if (z) z.value = E.pps;
  }
  function snapTo(t, cands, alt) {
    if (!E.snap || alt) return [t, null];
    let best = null, bd = 7 / E.pps;
    for (const c of cands) { const dd = Math.abs(c - t); if (dd < bd) { bd = dd; best = c } }
    return [best ?? t, best];
  }
  const showSnap = c => { const el = $('#tlSnap'); if (c == null) el.style.display = 'none'; else { el.style.display = 'block'; el.style.left = X(c) + 'px' } };
  function candidates(k, id) {
    const d = E.doc, c = [E.t, d.start, d.end, d.start + d.hook.duration];
    for (const n of d.notes) if (n.id != id) c.push(n.t, n.t + n.duration);
    for (const x of d.fx) if (x.id != id) c.push(x.t, x.t + x.duration);
    for (const x of d.texts) if (x.id != id) c.push(x.t, x.t + x.duration);
    for (const x of track()) if (x.id != id) c.push(x.start, x.end);
    if (k == 'cue' || k == 'trim' || k == 'sfx' || k == 'fx') for (const w of E.d.words) c.push(w.start, w.end);
    return c;
  }
  function neighbours(k, id, a, b) {   // huecos libres a cada lado, para que notas y subtítulos no se pisen
    let lo = E.win.a, hi = E.win.b;
    if (k == 'cue' || k == 'note') for (const o of listOf(k)) {
      if (o.id == id) continue; const [oa, ob] = span(k, o);
      if (ob <= a + .001) lo = Math.max(lo, ob); else if (oa >= b - .001) hi = Math.min(hi, oa);
    }
    return [lo, hi];
  }
  function bindTimeline() {
    const tl = E.tl;
    tl.addEventListener('pointerdown', e => {
      if (e.button) return;
      const t = E.win.a + (e.clientX - tl.getBoundingClientRect().left) / E.pps, d = E.doc;
      const trim = e.target.closest('.trim'), it = e.target.closest('.it'), hd = e.target.closest('.hd');
      const dr = { x0: e.clientX, moved: false };
      if (trim) { dr.kind = trim.classList.contains('l') ? 'trimL' : 'trimR'; dr.o = { start: d.start, end: d.end }; dr.cands = candidates('trim'); pause() }
      else if (it) {
        const k = it.dataset.k, id = it.dataset.id, o = find(k, id); if (!o) return;
        if (!(E.sel && E.sel.k == k && E.sel.id == (id || undefined))) select(k == 'hook' ? { k } : { k, id });
        const [a, b] = span(k, o);
        Object.assign(dr, { kind: hd ? (hd.classList.contains('l') ? 'l' : 'r') : (k == 'hook' ? 'none' : 'move'), k, id, a, b, cands: candidates(k, id) });
        [dr.lo, dr.hi] = neighbours(k, id, a, b);
      } else { dr.kind = 'seek'; if (!e.target.closest('.tl-ruler') && E.sel) select(null); pause(); seek(t) }
      E.drag = dr; tl.setPointerCapture(e.pointerId);
    });
    tl.addEventListener('pointermove', e => {
      const dr = E.drag; if (!dr) return;
      const d = E.doc, dt = (e.clientX - dr.x0) / E.pps, alt = e.altKey;
      if (dr.kind == 'seek') { seek(E.win.a + (e.clientX - tl.getBoundingClientRect().left) / E.pps); return }
      if (dr.kind == 'none' || (!dr.moved && Math.abs(e.clientX - dr.x0) < 3)) return;
      if (!dr.moved) { dr.moved = true; pushUndo() }
      let snapped = null;
      if (dr.kind == 'trimL') {
        let [ns, c] = snapTo(dr.o.start + dt, dr.cands.filter(x => x != d.start), alt); snapped = c;
        d.start = r3(clamp(ns, Math.max(E.win.a, d.end - E.d.limits.max), d.end - E.d.limits.min)); seek(d.start);
      } else if (dr.kind == 'trimR') {
        let [ne, c] = snapTo(dr.o.end + dt, dr.cands.filter(x => x != d.end), alt); snapped = c;
        d.end = r3(clamp(ne, d.start + E.d.limits.min, Math.min(E.win.b, d.start + E.d.limits.max))); seek(d.end - .04);
      } else {
        const o = find(dr.k, dr.id), len = dr.b - dr.a, min = minLen(dr.k, o), max = maxLen(dr.k, o);
        let a = dr.a, b = dr.b;
        if (dr.kind == 'move') {
          const [sa, ca] = snapTo(dr.a + dt, dr.cands, alt), [sb, cb] = snapTo(dr.b + dt, dr.cands, alt);
          const da = ca == null ? 1e9 : Math.abs(ca - dr.a - dt), db = cb == null ? 1e9 : Math.abs(cb - dr.b - dt);
          if (ca != null && da <= db) { a = sa; snapped = ca } else if (cb != null) { a = sb - len; snapped = cb } else a = dr.a + dt;
          a = clamp(a, dr.lo, Math.max(dr.lo, dr.hi - len)); b = a + len;
        } else if (dr.kind == 'l') { let c;[a, c] = snapTo(dr.a + dt, dr.cands, alt); snapped = c; a = clamp(a, Math.max(dr.lo, dr.b - max), dr.b - min) }
        else { let c;[b, c] = snapTo(dr.b + dt, dr.cands, alt); snapped = c; b = clamp(b, dr.a + min, Math.min(dr.hi, dr.a + max)) }
        setSpan(dr.k, o, a, b);
      }
      showSnap(snapped); rTimeline(); E.keys = {};
    });
    const end = () => {
      const dr = E.drag; if (!dr) return; E.drag = null; showSnap(null);
      if (!dr.moved || dr.kind == 'seek') return;
      if (dr.kind.startsWith('trim')) afterTrim(dr.o.start, dr.o.end);
      sortDoc(); E.lastKey = null; dirty(); refresh();
    };
    tl.addEventListener('pointerup', end); tl.addEventListener('pointercancel', end);
    tl.addEventListener('dblclick', e => { const it = e.target.closest('.it'); if (it) setTimeout(() => { const f = $('#edInsp [data-focus]'); if (f) { f.focus(); f.select && f.select() } }, 30) });
    E.scroll.addEventListener('wheel', e => {
      if (!(e.ctrlKey || e.metaKey)) return; e.preventDefault();
      setPps(E.pps * (e.deltaY < 0 ? 1.15 : 1 / 1.15), E.win.a + (e.clientX - tl.getBoundingClientRect().left) / E.pps);
    }, { passive: false });
  }

  /* ---------- acciones ---------- */
  function select(sel, soft) {
    E.sel = sel; rTimeline(); E.keys = {}; rInsp();
    if (!soft) { $$('#edSide .sel').forEach(x => x.classList.remove('sel')); if (sel && sel.id) { const row = $(`#edSide [data-id="${sel.id}"]`); if (row) { row.classList.add('sel'); row.scrollIntoView({ block: 'nearest' }) } } }
  }
  function afterTrim(oldStart, oldEnd) {
    const d = E.doc;
    d.hook.duration = Math.min(d.hook.duration, d.end - d.start);
    if (d.start < oldStart - .05) fillCaptions(d.start, oldStart);
    if (d.end > oldEnd + .05) fillCaptions(oldEnd, d.end);
    E.t = clamp(E.t, d.start, d.end);
  }
  function fillCaptions(a, b) {   // subtítulos para un tramo recién incluido, a partir de la transcripción
    const words = E.d.words.filter(w => { const m = (w.start + w.end) / 2; return m >= a && m <= b }), srcLang = E.d.edl.source_language;
    for (const [lang, cues] of Object.entries(E.doc.tracks)) {
      const free = words.filter(w => { const m = (w.start + w.end) / 2; return !cues.some(c => m >= c.start - .05 && m <= c.end + .05) });
      let cur = [], chars = 0; const made = [];
      const flush = () => { if (!cur.length) return; made.push({ id: uid(), start: cur[0].start, end: cur[cur.length - 1].end, fresh: true, untr: lang != srcLang, words: cur.map(w => ({ w: w.w, start: w.start, end: w.end, hl: false })) }); cur = []; chars = 0 };
      for (const w of free) {
        if (cur.length && (cur.length >= 3 || chars + w.w.length + 1 > 16 || w.start - cur[cur.length - 1].end > .6)) flush();
        cur.push(w); chars += w.w.length + 1;
        if (/[.!?…]["')\]]*$/.test(w.w)) flush();
      }
      flush(); cues.push(...made); cues.sort((x, y) => x.start - y.start);
      cues.forEach((c, i) => {
        const next = cues[i + 1] ? cues[i + 1].start : E.doc.end;
        if (c.fresh) { c.end = r3(Math.max(c.end, Math.min(c.end + .3, next))); delete c.fresh }
        if (c.end > next && next > c.start) setSpan('cue', c, c.start, next);
      });
    }
  }
  function setEdge(which) {
    const d = E.doc, t = E.t, L = E.d.limits;
    if (which == 'start' ? !(d.end - t >= L.min && d.end - t <= L.max) : !(t - d.start >= L.min && t - d.start <= L.max)) { toast(`El clip debe durar entre ${L.min} y ${L.max} s.`, 'er'); return }
    const o = { start: d.start, end: d.end };
    change(doc => { doc[which] = r3(t); afterTrim(o.start, o.end); sortDoc() });
    fitZoom();
  }
  function freeSlot(k, t, want) {   // tramo libre desde t para un elemento nuevo
    for (const o of [...listOf(k)].sort((x, y) => span(k, x)[0] - span(k, y)[0])) { const [oa, ob] = span(k, o); if (t >= oa - .001 && t < ob) t = ob }
    const [, hi] = neighbours(k, null, t, t + .001);
    return [t, Math.min(hi, E.doc.end, t + want)];
  }
  const blank = text => Object.fromEntries(E.d.edl.languages.map(l => [l, text]));
  function addNote() {
    const t = clamp(E.t, E.doc.start, E.doc.end - .6), [a, b] = freeSlot('note', t, 3);
    if (b - a < .5) { toast('No hay hueco para una nota aquí.', 'er'); return }
    const n = { id: uid(), t: r3(a), duration: r3(b - a), text: blank('Escribe tu comentario') };
    change(d => { d.notes.push(n); sortDoc() }); E.tab = 'text'; rSide(); select({ k: 'note', id: n.id }); focusInsp();
  }
  function addCue() {
    const t = clamp(E.t, E.doc.start, E.doc.end - .2);
    if (track().some(c => t >= c.start && t < c.end)) { toast('Ya hay un subtítulo en el cursor. Muévete a un hueco.', 'er'); return }
    const [a, b] = freeSlot('cue', t, 1.5); if (b - a < .15) { toast('No hay hueco para un subtítulo aquí.', 'er'); return }
    const c = { id: uid(), start: r3(a), end: r3(b), words: [{ w: 'texto', start: r3(a), end: r3(b), hl: false }] };
    change(d => { d.tracks[E.lang].push(c); sortDoc() }); E.tab = 'captions'; rSide(); select({ k: 'cue', id: c.id }); focusInsp();
  }
  function addFx(type = 'punch_in') {
    const sp = fxSpec(type), t = clamp(E.t, E.doc.start, E.doc.end - sp.min), x = { id: uid(), t: r3(t), duration: r3(Math.min(sp.d, E.doc.end - t)), type: sp.id, intensity: 1 };
    if (type == 'face_focus') x.region = { x: .25, y: .25, w: .5, h: .5 };
    change(d => { d.fx.push(x); sortDoc() }); select({ k: 'fx', id: x.id }); if (!E.playing) seek(x.t + Math.min(.15, x.duration / 2));
  }
  function addText(presetId) {
    const ps = E.d.catalog.text_presets, p = ps.find(x => x.id == presetId) || ps[0], d = E.doc;
    const t = p.whole ? d.start : clamp(E.t, d.start, d.end - .5), dur = p.whole ? d.end - d.start : Math.min(3, d.end - t);
    const { id: _i, label: _l, sample, whole: _w, ...style } = p;
    const x = { ...TEXT0, ...style, id: uid(), t: r3(t), duration: r3(dur), text: blank(sample) };
    change(doc => { doc.texts.push(x); sortDoc() }); E.tab = 'text'; rSide(); select({ k: 'text', id: x.id });
    if (!E.playing) seek(Math.min(x.t + .45, x.t + x.duration / 2)); focusInsp();
  }
  function addSfx(sid) {
    const x = { id: uid(), t: r3(clamp(E.t, E.doc.start, E.doc.end - .1)), sid, gain: 1 };
    change(d => { d.sfx.push(x); sortDoc() }); select({ k: 'sfx', id: x.id }); beep(sid);
  }
  function splitCue() {
    const c = track().find(c => E.t > c.start && E.t < c.end) || (E.sel && E.sel.k == 'cue' ? selObj() : null);
    if (!c || c.words.length < 2) { toast('Pon el cursor sobre un subtítulo de dos o más palabras.', 'er'); return }
    let i = c.words.findIndex(w => w.start >= E.t); if (i < 1) i = Math.ceil(c.words.length / 2);
    change(d => {
      const list = d.tracks[E.lang], k = list.findIndex(x => x.id == c.id), cc = list[k], cut = cc.words[i].start;
      const b = { id: uid(), start: cut, end: cc.end, words: cc.words.slice(i), untr: cc.untr };
      cc.words = cc.words.slice(0, i); cc.end = cut; list.splice(k + 1, 0, b);
    });
  }
  function delSel() {
    const s = E.sel; if (!s) return;
    if (s.k == 'clip') return;
    if (s.k == 'hook') { change(d => { d.hook.duration = 0 }); toast('Gancho oculto. Deshaz con Ctrl+Z o dale duración en Texto.'); return }
    change(d => { const key = { note: 'notes', sfx: 'sfx', fx: 'fx', text: 'texts', media:'media' }[s.k], list = s.k == 'cue' ? d.tracks[E.lang] : d[key]; const i = list.findIndex(x => x.id == s.id); if (i >= 0) list.splice(i, 1) });
    E.sel = null; refresh();
  }
  function setCueText(c, text) {
    const tokens = text.trim().split(/\s+/).filter(Boolean), norm = s => s.toLowerCase().replace(/[^\p{L}\p{N}']+/gu, '');
    if (tokens.length == c.words.length) c.words.forEach((w, i) => w.w = tokens[i]);
    else {
      const hl = new Set(c.words.filter(w => w.hl).map(w => norm(w.w))), a = c.start, b = c.words.length ? Math.max(c.words[c.words.length - 1].end, a + .1) : c.end;
      const total = tokens.reduce((s, t) => s + t.length + 1, 0); let t = a;
      c.words = tokens.map(tok => { const dur = (b - a) * (tok.length + 1) / total, w = { w: tok, start: r3(t), end: r3(t + dur), hl: hl.has(norm(tok)) }; t += dur; return w });
    }
    delete c.untr;
  }
  const focusInsp = () => setTimeout(() => { const f = $('#edInsp [data-focus]'); if (f) { f.focus(); f.select && f.select() } }, 30);
  async function discard() {
    if (!I18n.confirm('¿Descartar el borrador y volver a la versión renderizada?')) return;
    clearTimeout(saveTimer);
    try { await send('DELETE', `/api/clips/${E.id}/draft`); const id = E.id; E.save = 'saved'; close(); open(id) } catch (x) { toast(x.message, 'er') }
  }
  async function renderNow() {
    pause(); clearTimeout(saveTimer);
    const id = E.id, over = document.createElement('div'); over.className = 'ed-over';
    over.innerHTML = `<div class="card"><h2>Renderizando el clip</h2><div class="sub" id="rnS">Enviando…</div><div class="bar"><i></i></div><pre class="log" id="rnL" style="max-height:140px"></pre>
      <p class="hint">FFmpeg genera el video final en cada idioma, con subtítulos quemados y audio normalizado. Puedes dejar esta ventana abierta.</p></div>`;
    document.body.append(over);
    try {
      const { run_id } = await post(`/api/clips/${id}/render`, toServer());
      E.save = 'saved'; E.hasDraft = true;
      for (; ;) {
        await new Promise(r => setTimeout(r, 1500));
        const r = await api('/api/runs/' + run_id);
        $('#rnS', over).textContent = r.status == 'queued' ? 'En cola: hay otro trabajo en curso.' : 'Renderizando…';
        $('#rnL', over).textContent = r.log.join('\n');
        if (r.status == 'failed') throw new Error(r.error || 'El render falló.');
        if (r.status == 'done') break;
      }
      over.remove(); if (!E || E.id != id) return;
      close(); await open(id); if (E) { setView('final'); toast('Render listo. El clip volvió a la cola de aprobación.', 'ok') }
    } catch (x) { over.remove(); toast(x.message, 'er'); if (E) rTop() }
  }


  const resource = id => E.resources.find(x=>x.id===id);
  const mediaUrl = id => `/api/projects/${encodeURIComponent(E.project)}/resources/${encodeURIComponent(id)}/file`;
  const safeTemplates = () => { try { return JSON.parse(localStorage.getItem('clipper.safe.templates') || '[]'); } catch { return []; } };
  const modePanel = () => !cfg.desktop ? '' : `<div class="ed-sec"><label for="edMode">Editor</label><select id="edMode"><option value="clip" ${E.doc.editor_mode==='clip'?'selected':''}>Clips</option><option value="long" ${E.doc.editor_mode==='long'?'selected':''}>Videos largos</option></select><p class="hint">Toda la fuente está disponible. Ajusta el rango y el zoom para trabajar con videos largos.</p><button class="b g sm" data-safe-action="full">Usar fuente completa</button></div>`;
  function safePanel() {
    if(!cfg.desktop)return '';
    const z=E.doc.safe_area, locked=z.locked!==false;
    return `<div class="ed-sec"><h4>Zona segura<button class="b g sm" data-safe-action="lock" aria-pressed="${locked}">${icon(locked?'lock':'unlock')}${locked?'Desbloquear':'Bloquear'}</button></h4><p class="hint">La IA usa la zona centrada como referencia. Desbloquea para ajustar los márgenes del montaje.</p>${['left','right','top','bottom'].map(k=>`<div class="fld"><span>${({left:'Izquierda',right:'Derecha',top:'Arriba',bottom:'Abajo'})[k]}</span><input type="range" data-safe-margin="${k}" min="0" max="${k==='left'||k==='right'?300:700}" value="${z[k]}" ${locked?'disabled':''}><output>${z[k]} px</output></div>`).join('')}<label for="edSafeTemplate">Plantillas guardadas</label><select id="edSafeTemplate" ${locked?'disabled':''}><option value="">Selecciona una plantilla</option>${safeTemplates().map((x,i)=>`<option value="${i}">${esc(x.name)}</option>`).join('')}</select><input id="edSafeName" placeholder="Nombre de la plantilla" maxlength="80" ${locked?'disabled':''}><button class="b g sm" data-safe-action="save" ${locked?'disabled':''}>Guardar plantilla</button><button class="b g sm" data-safe-action="reset" ${locked?'disabled':''}>Centrar zona</button></div>`;
  }
  function safeAction(action) {
    if(action==='full'){const old={start:E.doc.start,end:E.doc.end};change(d=>{d.editor_mode='long';d.start=0;d.end=Math.min(E.d.source.duration,E.d.limits.max);afterTrim(old.start,old.end)});fitZoom();return;}
    if(action==='lock')change(d=>{d.safe_area.locked=d.safe_area.locked===false});
    else if(E.doc.safe_area.locked!==false)return;
    else if(action==='reset')change(d=>{Object.assign(d.safe_area,{left:100,right:100,top:240,bottom:240})});
    else if(action==='save') {
      const name=$('#edSafeName').value.trim(); if(!name){toast('Escribe un nombre para la plantilla','er');return;}
      const templates=safeTemplates().filter(x=>x.name!==name);templates.push({name,safe:copy(E.doc.safe_area)});
      try{localStorage.setItem('clipper.safe.templates',JSON.stringify(templates));toast('Plantilla guardada','ok');rSide();}catch{toast('No se pudo guardar la plantilla','er');}
    }
    rTransport();
  }
  async function loadResources() {
    const me=E;try {const rows=await api(`/api/projects/${encodeURIComponent(E.project)}/resources`);if(E!==me)return;E.resources=rows;E.resourceError='';if(E.tab==='resources')rSide();}catch(x){if(E===me){E.resourceError=x.message;if(E.tab==='resources')rSide();}}
  }
  function resourcePanel() {
    return `${modePanel()}<div class="ed-sec"><h4>Recursos del proyecto</h4><label for="edImport">Importar contenido</label><input id="edImport" type="file" accept=".png,.jpg,.jpeg,.webp,.gif,.mp4,.mov,.webm,.wav,.mp3,.m4a,.ogg,.flac"><p class="hint">Añade imágenes, videos o audio al cursor. Las imágenes y videos ocupan el lienzo; ajusta su tiempo y volumen en Propiedades.</p>${E.resourceError?`<p role="alert">${esc(E.resourceError)}</p>`:''}${E.resources.filter(x=>['image','audio','video'].includes(x.kind)).map(x=>`<div class="ed-sec"><strong data-ui-content>${esc(x.name)}</strong>${x.kind==='image'?`<img style="width:100%;max-height:100px;object-fit:contain" loading="lazy" src="${mediaUrl(x.id)}" alt="${esc(x.name)}">`:''}<button class="b g sm" data-resource-add="${esc(x.id)}">${icon('plus')}Añadir al cursor</button></div>`).join('') || '<p class="hint">Importa recursos para empezar.</p>'}</div>`;
  }
  async function importResource(file) {
    if(!file)return; const me=E, form=new FormData();form.append('file',file);
    try {await api(`/api/projects/${encodeURIComponent(E.project)}/resources`,{method:'POST',body:form});if(E===me)await loadResources();}catch(x){if(E===me)toast(x.message,'er');}
  }
  function addMedia(id) {
    const r=resource(id); if(!r)return;
    const t=clamp(E.t,E.doc.start,E.doc.end-.1), duration=Math.min(r.duration || 5,E.doc.end-t);
    change(d=>{d.media.push({id:uid(),resource_id:id,t,duration,offset:0,gain:1})});
  }
  function updateTracking() {
    if(E.doc.layout!=='center_crop' || !E.doc.tracking_enabled)return;
    const points=E.d.edl.framing?.track || [];if(points.length<2)return;
    const a=[...points].reverse().find(p=>
      p.source_time<=E.t) || points[0], b=points.find(p=>
      E.t<p.source_time) || points[points.length-1];
    const f=b.source_time>a.source_time?clamp((E.t-a.source_time)/(b.source_time-a.source_time),0,1):0;
    const s=E.d.source,k=Math.max(1080/s.width,1920/s.height);
    E.vid.style.left=-(s.width*k-1080)*(a.x+(b.x-a.x)*f)+'px';E.vid.style.top=-(s.height*k-1920)*(a.y+(b.y-a.y)*f)+'px';
  }
  function previewMedia() {
    const container=$('#edMedia'); if(!container)return;
    const active=E.doc.media.filter(x=>
      x.t<=E.t && E.t<x.t+x.duration && E.t>=E.doc.start && E.t<E.doc.end);
    const ids=new Set(active.map(x=>x.id));
    for(const el of [...container.children])if(!ids.has(el.dataset.id)){el.pause?.();el.remove();}
    for(const item of active){
      const r=resource(item.resource_id);if(!r)continue;
      let el=[...container.children].find(x=>x.dataset.id===item.id);
      if(!el){el=document.createElement(r.kind==='image'?'img':r.kind==='audio'?'audio':'video');el.dataset.id=item.id;el.src=mediaUrl(r.id);el.style.cssText='position:absolute;inset:0;width:100%;height:100%;object-fit:contain;background:#000';if(r.kind==='audio')el.style.display='none';container.append(el);}
      if(r.kind!=='image'){const time=E.t-item.t+item.offset;if(el.readyState>=1 && Math.abs(el.currentTime-time)>.2)el.currentTime=time;el.muted=E.muted;el.volume=clamp(item.gain,0,1);if(E.playing && el.paused)el.play().catch(()=>{});if(!E.playing && !el.paused)el.pause();}
    }
  }

  /* ---------- panel izquierdo ---------- */
  const rowTime = t => tc(t - E.doc.start);
  function rSide() {
    const d = E.doc, L = E.lang, langs = E.d.edl.languages;
    $$('#edTabs button').forEach(b => b.classList.toggle('on', b.dataset.tab == E.tab));
    const isSel = (k, id) => E.sel && E.sel.k == k && E.sel.id == id ? 'sel' : '';
    let h = '';
    if (E.tab == 'resources') { h = resourcePanel(); } else if (E.tab == 'captions') {
      const cues = track(), untr = cues.filter(c => c.untr && inClip(c.start, c.end)).length;
      const pv = sp => { const f = font(sp.font); return `font-family:${f.f.replace(/"/g, "'")};font-weight:${f.w};color:${sp.color};-webkit-text-stroke:3px ${sp.outline_color};paint-order:stroke fill;${sp.glow ? `text-shadow:0 0 6px ${sp.outline_color};` : ''}${sp.font == 'pixel' ? 'font-size:9px;' : ''}` };
      h = `<div class="ed-sec"><h4>Estilo</h4><div class="opt c3">${Object.entries(E.d.styles).map(([k, sp]) => `<button data-style="${k}" class="${d.style == k ? 'on' : ''}"><span class="pv" style="${pv(sp)}">${sp.upper ? 'HOLA' : 'Hola'}<br><b style="color:${sp.active}">${sp.upper ? 'MUNDO' : 'mundo'}</b></span>${esc(sp.label)}</button>`).join('')}</div>
        <div class="fld"><span>Tamaño</span><input type="range" data-f="capscale" min="0.6" max="1.5" step="0.05" value="${d.capScale}"><output>${Math.round(d.capScale * 100)}%</output></div>
        <div class="fld"><span>Altura</span><input type="range" data-f="capy" min="${E.d.frame.safe.enforced ? (E.d.frame.safe.top+140)/1920 : .1}" max="${E.d.frame.safe.enforced ? (1920-E.d.frame.safe.bottom-140)/1920 : .9}" step="0.01" value="${d.capY ?? (geom().caption_y / 1920).toFixed(2)}"><output>${d.capY == null ? 'Auto' : Math.round(d.capY * 100) + '%'}</output></div>
        ${d.capY == null ? '' : `<button class="b g sm" data-a="capauto">${icon('reset')}Altura automática</button>`}</div>
       <div class="ed-sec"><h4>Subtítulos · ${L.toUpperCase()}<span class="sp"></span><button class="b g sm" data-a="cue">${icon('plus')}Añadir</button></h4>
       ${untr ? `<div class="msg wa">${untr} subtítulo(s) del tramo añadido están sin traducir (en amarillo). Escribe la traducción.</div>` : ''}
       <div class="cues">${cues.map(c => `<div class="cue ${isSel('cue', c.id)} ${c.untr ? 'untr' : ''} ${inClip(c.start, c.end) ? '' : 'out'}" data-id="${c.id}">
         <span class="tm" data-seek="${c.start}">${rowTime(c.start)}</span><input data-cue="${c.id}" value="${esc(c.words.map(w => w.w).join(' '))}" spellcheck="false"><button class="del" data-del="cue" title="Eliminar">${icon('x')}</button></div>`).join('') || '<p class="hint">Este idioma no tiene subtítulos.</p>'}</div>
       <p class="hint">Edita el texto aquí. Arrastra los bloques en la línea de tiempo para moverlos o estirarlos.</p></div>`;
    } else if (E.tab == 'text') {
      const tp = p => { const f = font(p.font); return `font-family:${f.f.replace(/"/g, "'")};font-weight:${f.w};color:${p.color};${p.box ? `background:${rgba(p.box, p.box_opacity)};padding:1px 6px;border-radius:2px;` : p.stroke_w ? `-webkit-text-stroke:3px ${p.stroke};paint-order:stroke fill;` : ''}${p.glow ? `text-shadow:0 0 6px ${p.stroke};` : ''}${p.rotate ? `transform:rotate(${p.rotate}deg);` : ''}${p.font == 'pixel' ? 'font-size:8px;' : ''}` };
      h = `<div class="ed-sec"><h4>Añadir texto</h4><div class="opt c2">${E.d.catalog.text_presets.map(p => `<button data-preset="${p.id}" title="Añadir en el cursor"><span class="tpv"><span style="${tp(p)}">${esc(p.upper ? p.sample.toUpperCase() : p.sample)}</span></span>${esc(p.label)}</button>`).join('')}</div>
        <p class="hint">Toca un estilo para añadirlo en el cursor. Después arrástralo en la vista previa y cámbialo en el panel derecho.</p></div>
       <div class="ed-sec"><h4>Textos del clip</h4>${d.texts.map(x => `<div class="itemrow ${isSel('text', x.id)} ${inClip(x.t, x.t + x.duration) ? '' : 'out'}" data-sel="text" data-id="${x.id}"><span class="tm">${rowTime(x.t)}</span><span class="tx">${esc((x.text[L] || '—').replace(/\n/g, ' '))}</span></div>`).join('') || '<p class="hint">Todavía no hay textos.</p>'}</div>
       <div class="ed-sec"><h4>Gancho</h4>${langs.map(l => `<span class="lg">${l}</span><textarea data-hook="${l}" rows="2" maxlength="120">${esc(d.hook.text[l] || '')}</textarea>`).join('')}
        <div class="fld"><span>Duración</span><input type="range" data-f="hookdur" min="0" max="${Math.min(8, d.end - d.start).toFixed(1)}" step="0.1" value="${d.hook.duration}"><output>${d.hook.duration.toFixed(1)} s</output></div>
        <p class="hint">Texto grande que aparece al empezar. Con duración 0 no se muestra.</p></div>
       <div class="ed-sec"><h4>Notas de comentario<span class="sp"></span><button class="b g sm" data-a="note">${icon('plus')}Añadir</button></h4>
        ${d.notes.map(n => `<div class="itemrow ${isSel('note', n.id)} ${inClip(n.t, n.t + n.duration) ? '' : 'out'}" data-sel="note" data-id="${n.id}"><span class="tm">${rowTime(n.t)}</span><span class="tx">${esc(n.text[L] || '—')}</span></div>`).join('') || '<p class="hint">Sin notas. Tu comentario propio es lo que diferencia el clip del original.</p>'}</div>`;
    } else if (E.tab == 'audio') {
      const sfx = E.d.library.filter(l => l.kind == 'sfx'), music = E.d.library.filter(l => l.kind == 'music');
      h = `<div class="ed-sec"><h4>Efectos de sonido</h4><div class="lib">${sfx.map(l => `<div class="li"><button class="b g ic sm" data-play="${l.id}" title="Escuchar">${icon('play')}</button><div><b data-ui-content>${esc(l.id.replace(/_\d+$/, ''))}</b><small title="${esc(l.description)}">${esc(l.description)}</small>${l.author ? `<small data-ui-content title="${esc(l.source_url)}">${esc(l.license)} · ${esc(l.author)}</small>` : ''}</div><button class="b ic sm" data-sfx="${l.id}" title="Añadir en el cursor">${icon('plus')}</button></div>`).join('') || '<p class="hint">La biblioteca de sonidos está vacía.</p>'}</div></div>
       <div class="ed-sec"><h4>En el clip</h4>${d.sfx.map(x => `<div class="itemrow ${isSel('sfx', x.id)} ${inClip(x.t, x.t + .01) ? '' : 'out'}" data-sel="sfx" data-id="${x.id}"><span class="tm">${rowTime(x.t)}</span><span class="tx">♪ ${esc(x.sid)}</span></div>`).join('') || '<p class="hint">Ningún sonido colocado.</p>'}</div>
       <div class="ed-sec"><h4>Mezcla</h4>
         <div class="fld"><span>Voz</span><input type="range" data-f="voice" min="0" max="2" step="0.05" value="${d.voice}"><output>${Math.round(d.voice * 100)}%</output></div>
         <label style="display:flex;gap:8px;align-items:center"><input type="checkbox" data-f="enhance" style="width:auto" ${d.enhance ? 'checked' : ''}>Realzar la voz (quita graves y nivela)</label>
         <p class="hint">El realce se aplica en el render, no en la vista en vivo.</p></div>
       ${music.length ? `<div class="ed-sec"><h4>Música</h4><select data-f="music"><option value="">Sin música</option>${music.map(m => `<option value="${m.id}" ${d.music_id == m.id ? 'selected' : ''}>${esc(m.description.split(':')[0])} · ${esc(m.description.split(':').slice(1).join(':').trim())}</option>`).join('')}</select>
         ${d.music_id ? `<div class="fld"><span>Volumen</span><input type="range" data-f="musicgain" min="0" max="3" step="0.05" value="${d.musicGain}"><output>${Math.round(d.musicGain * 100)}%</output></div>` : ''}
         <label style="display:flex;gap:8px;align-items:center"><input type="checkbox" data-f="duck" style="width:auto" ${d.duck ? 'checked' : ''}>Bajar la música cuando alguien habla</label></div>` : ''}
       <div class="ed-sec"><h4>Volumen final</h4><div class="fld"><span>Objetivo</span><input type="range" data-f="lufs" min="-20" max="-10" step="1" value="${d.lufs}"><output>${d.lufs} LUFS</output></div>
        <p class="hint">−14 LUFS es lo habitual en YouTube, TikTok e Instagram. La normalización se aplica en el render, no en la vista en vivo.</p></div>`;
    } else if (E.tab == 'fx') {
      const T = E.d.catalog.transitions, tr = d.trans, pg = d.progress, sel2 = (name, list, v) => `<select data-tr="${name}">${list.map(([k, l]) => `<option value="${k}" ${k == v ? 'selected' : ''}>${esc(l)}</option>`).join('')}</select>`;
      h = `<div class="ed-sec"><h4>Añadir efecto</h4><div class="opt c2">${E.d.catalog.effects.map(e => `<button data-fxadd="${e.id}" title="${esc(e.desc)}"><span class="fxi fx-${e.id}"></span>${esc(e.label)}</button>`).join('')}</div>
        <p class="hint">Se añade en el cursor. Estíralo en la línea de tiempo y ajusta su fuerza en el panel derecho.</p></div>
       <div class="ed-sec"><h4>Efectos del clip</h4>${d.fx.map(x => `<div class="itemrow ${isSel('fx', x.id)} ${inClip(x.t, x.t + x.duration) ? '' : 'out'}" data-sel="fx" data-id="${x.id}"><span class="tm">${rowTime(x.t)}</span><span class="tx"><span>${esc(fxSpec(x.type).label)}</span> · ${x.duration.toFixed(1)} s</span></div>`).join('') || '<p class="hint">Sin efectos.</p>'}</div>
       <div class="ed-sec"><h4>Transiciones</h4>
        <label>Entrada</label>${sel2('intro', T.intro, tr.intro)}
        ${tr.intro == 'none' ? '' : `<div class="fld"><span>Duración</span><input type="range" data-trd="intro_d" min="0.1" max="2" step="0.05" value="${tr.intro_d}"><output>${tr.intro_d.toFixed(2)} s</output></div>`}
        <label>Salida</label>${sel2('outro', T.outro, tr.outro)}
        ${tr.outro == 'none' ? '' : `<div class="fld"><span>Duración</span><input type="range" data-trd="outro_d" min="0.1" max="2" step="0.05" value="${tr.outro_d}"><output>${tr.outro_d.toFixed(2)} s</output></div>`}</div>
       <div class="ed-sec"><h4>Barra de progreso</h4>
        <label style="display:flex;gap:8px;align-items:center"><input type="checkbox" data-pg="on" style="width:auto" ${pg.on ? 'checked' : ''}>Mostrar una barra que se llena con el clip</label>
        ${pg.on ? `<div class="row" style="grid-template-columns:1fr 1fr"><div><label>Color</label><input type="color" data-pg="color" value="${pg.color}"></div><div><label>Posición</label><select data-pg="position"><option value="top" ${pg.position == 'top' ? 'selected' : ''}>Arriba</option><option value="bottom" ${pg.position == 'bottom' ? 'selected' : ''}>Abajo</option></select></div></div>
        <div class="fld"><span>Grosor</span><input type="range" data-pg="height" min="4" max="40" step="1" value="${pg.height}"><output>${pg.height} px</output></div>` : ''}</div>`;
    } else if (E.tab == 'color') {
      const c = d.color, s = E.d.source, thumb = `/api/sources/${s.id}/frame?t=${(Math.round((d.start + d.end)) / 2).toFixed(1)}&h=120`;
      const filters = E.d.catalog.looks.map(l => { const rows = colorAffine(colorParams({ ...COLOR0, look: l.id })); return `<filter id="lk_${l.id}" color-interpolation-filters="sRGB"><feColorMatrix type="matrix" values="${matrixValues(rows)}"/></filter>` }).join('');
      h = `<svg width="0" height="0" style="position:absolute"><defs>${filters}</defs></svg>
       <div class="ed-sec"><h4>Filtros</h4><div class="opt c3 looks">${E.d.catalog.looks.map(l => `<button data-look="${l.id}" class="${c.look == l.id ? 'on' : ''}"><img src="${thumb}" style="filter:url(#lk_${l.id})" draggable="false" alt="">${esc(l.label)}</button>`).join('')}</div>
        ${c.look == 'none' ? '' : `<div class="fld"><span>Intensidad</span><input type="range" data-col="look_amount" min="0" max="1" step="0.01" value="${c.look_amount}"><output>${Math.round(c.look_amount * 100)}</output></div>`}</div>
       <div class="ed-sec"><h4>Ajustes<span class="sp"></span><button class="b g sm" data-a="colreset">${icon('reset')}Restablecer</button></h4>
        ${COLOR_UI.map(([k, l, lo]) => `<div class="fld"><span>${l}</span><input type="range" data-col="${k}" min="${lo}" max="1" step="0.01" value="${c[k]}"><output>${Math.round(c[k] * 100)}</output></div>`).join('')}
        <p class="hint">Lo que ves en la vista previa es lo que sale en el render: los dos usan la misma matriz de color.</p></div>`;
    } else {
      h = `${modePanel()}${safePanel()}<div class="ed-sec"><h4>Encuadre</h4>${E.d.edl.framing?.track?.length>1 ? `<label class="tog"><input type="checkbox" data-f="tracking" ${d.tracking_enabled?'checked':''}>Seguir sujeto con IA</label><p class="hint">Movimiento interpolado entre las referencias visuales de la IA.</p>` : ''}<div class="opt">
         <button data-layout="gameplay_blur_bg" class="${d.layout == 'gameplay_blur_bg' ? 'on' : ''}"><span class="lay blur"></span>Completo + fondo</button>
         <button data-layout="center_crop" class="${d.layout == 'center_crop' ? 'on' : ''}"><span class="lay crop"></span>Recorte vertical</button></div>
        ${d.layout == 'center_crop' ? `<div class="fld"><span>Horizontal</span><input type="range" data-f="crop" min="0" max="1" step="0.01" value="${d.crop_x}"><output>${Math.round(d.crop_x * 100)}%</output></div>${cfg.desktop ? `<div class="fld"><span>Vertical</span><input type="range" data-f="cropy" min="0" max="1" step="0.01" value="${d.crop_y}"><output>${Math.round(d.crop_y * 100)}%</output></div><p class="hint">El recorte solo muestra aproximadamente ${Math.round(Math.min(1, (1080/1920)/(E.d.source.width/E.d.source.height))*100)}% del ancho original. Si desaparecen ventanas o el creador, elige Completo + fondo.</p>` : ''}<p class="hint">También puedes arrastrar la imagen en la vista previa.</p>` : '<p class="hint">Se ve el video entero, con un fondo desenfocado arriba y abajo.</p>'}</div>
       <div class="ed-sec"><h4>Rango del clip</h4>
        <div class="row" style="grid-template-columns:1fr 1fr"><div><label>Inicio en la fuente (s)</label><input type="number" step="0.1" data-f="start" value="${d.start.toFixed(2)}"></div><div><label>Final en la fuente (s)</label><input type="number" step="0.1" data-f="end" value="${d.end.toFixed(2)}"></div></div>
        <p class="hint">Duración: ${(d.end - d.start).toFixed(1)} s. También puedes arrastrar los bordes blancos del clip en la línea de tiempo, o usar <kbd>I</kbd> y <kbd>O</kbd>.</p></div>`;
    }
    $('#edSide').innerHTML = h; E.keys.cur = null;
  }
  function onSideInput(e) {
    const t = e.target, f = t.dataset.f;
    if(t.dataset.safeMargin) { const k=t.dataset.safeMargin; if(E.doc.safe_area.locked !== false)return; change(d=>{d.safe_area[k]=clamp(+t.value,0,k==='left'||k==='right'?300:700)}, {key:'safe-'+k,soft:true});t.nextElementSibling.textContent=t.value+' px';return; }
    if (t.dataset.cue) { const id = t.dataset.cue; change(d => { const c = d.tracks[E.lang].find(x => x.id == id); if (c) setCueText(c, t.value) }, { key: 'cue' + id, soft: true }); t.closest('.cue').classList.remove('untr'); if (E.sel && E.sel.id == id) rInsp(); return }
    if (t.dataset.hook) { change(d => { d.hook.text[t.dataset.hook] = t.value }, { key: 'hook' + t.dataset.hook, soft: true }); return }
    if (f == 'hookdur') { change(d => { d.hook.duration = +t.value }, { key: f, soft: true }); t.nextElementSibling.textContent = (+t.value).toFixed(1) + ' s'; if (!E.playing) seek(E.doc.start + Math.max(0, +t.value - .05)) }
    else if (f == 'crop') { change(d => { d.tracking_enabled=false; d.crop_x = +t.value }, { key: f, soft: true }); t.nextElementSibling.textContent = Math.round(t.value * 100) + '%' }
    else if (f == 'cropy') { change(d => { d.tracking_enabled=false; d.crop_y = +t.value }, { key: f, soft: true }); t.nextElementSibling.textContent = Math.round(t.value * 100) + '%' }
    else if (f == 'lufs') { change(d => { d.lufs = +t.value }, { key: f, soft: true }); t.nextElementSibling.textContent = t.value + ' LUFS' }
    else if (f == 'capscale') { change(d => { d.capScale = +t.value }, { key: f, soft: true }); t.nextElementSibling.textContent = Math.round(t.value * 100) + '%' }
    else if (f == 'capy') { change(d => { d.capY = +t.value }, { key: f, soft: true }); t.nextElementSibling.textContent = Math.round(t.value * 100) + '%' }
    else if (f == 'voice') { change(d => { d.voice = +t.value }, { key: f, soft: true }); t.nextElementSibling.textContent = Math.round(t.value * 100) + '%'; E.vid.volume = clamp(+t.value, 0, 1) }
    else if (f == 'musicgain') { change(d => { d.musicGain = +t.value }, { key: f, soft: true }); t.nextElementSibling.textContent = Math.round(t.value * 100) + '%'; if (E.music) E.music.volume = clamp((lib(E.doc.music_id).gain || .22) * t.value, 0, 1) }
    else if (t.dataset.col) { const k = t.dataset.col; change(d => { d.color[k] = +t.value }, { key: 'col' + k, soft: true }); t.nextElementSibling.textContent = Math.round(t.value * 100) }
    else if (t.dataset.trd) { const k = t.dataset.trd; change(d => { d.trans[k] = +t.value }, { key: k, soft: true }); t.nextElementSibling.textContent = (+t.value).toFixed(2) + ' s'; if (!E.playing) seek(k == 'intro_d' ? E.doc.start + t.value / 2 : E.doc.end - t.value / 2) }
    else if (t.dataset.pg && t.type != 'checkbox' && t.tagName != 'SELECT') { const k = t.dataset.pg; change(d => { d.progress[k] = k == 'height' ? +t.value : t.value }, { key: 'pg' + k, soft: true }); if (k == 'height') t.nextElementSibling.textContent = t.value + ' px' }
  }
  function onSideChange(e) {
    const t = e.target, f = t.dataset.f;
    if(t.id==='edImport') { importResource(t.files[0]); return; }
    if(t.id==='edMode') { change(d=>{d.editor_mode=t.value}); if(t.value==='long'){E.tab='resources';rSide();} return; }
    if(f==='tracking') {change(d=>{d.tracking_enabled=t.checked});return;}
    if(t.id==='edSafeTemplate') { if(t.value==='')return; const template=safeTemplates()[+t.value]; if(template)change(d=>{d.safe_area={...d.safe_area,...template.safe,locked:false}});return; }
    if (f == 'music') { change(d => { d.music_id = t.value || null }); if (E.playing) music(true) }
    else if (f == 'duck') change(d => { d.duck = t.checked }, { soft: true });
    else if (f == 'enhance') change(d => { d.enhance = t.checked }, { soft: true });
    else if (t.dataset.tr) change(d => { d.trans[t.dataset.tr] = t.value });
    else if (t.dataset.pg == 'on') change(d => { d.progress.on = t.checked });
    else if (t.dataset.pg == 'position') change(d => { d.progress.position = t.value }, { soft: true });
    else if (f == 'start' || f == 'end') {
      const v = +t.value, d = E.doc, L = E.d.limits, ns = f == 'start' ? v : d.start, ne = f == 'end' ? v : d.end;
      if (!(ns >= E.win.a && ne <= E.win.b && ne - ns >= L.min && ne - ns <= L.max)) { toast(`Rango no válido: entre ${E.win.a.toFixed(1)} y ${E.win.b.toFixed(1)} s de la fuente, con ${L.min}–${L.max} s de duración.`, 'er'); rSide(); return }
      const o = { start: d.start, end: d.end };
      change(doc => { doc.start = r3(ns); doc.end = r3(ne); afterTrim(o.start, o.end); sortDoc() }); fitZoom();
    }
  }
  function onSideFocus(e) { const id = e.target.dataset.cue; if (id && !(E.sel && E.sel.id == id)) { const c = find('cue', id); select({ k: 'cue', id }, true); $$('#edSide .cue.sel').forEach(x => x.classList.remove('sel')); e.target.closest('.cue').classList.add('sel'); if (c && !E.playing) seek(c.start + .01) } }
  function onSideClick(e) {
    const q = s => e.target.closest(s); let b;
    if(b=q('[data-resource-add]')) { addMedia(b.dataset.resourceAdd); return; }
    if(b=q('[data-safe-action]')) { safeAction(b.dataset.safeAction);return; }
    if (b = q('[data-style]')) change(d => { d.style = b.dataset.style });
    else if (b = q('[data-layout]')) change(d => { d.layout = b.dataset.layout });
    else if (b = q('[data-del]')) { const id = b.closest('[data-id]').dataset.id; E.sel = { k: 'cue', id }; delSel() }
    else if (b = q('[data-seek]')) { pause(); seek(+b.dataset.seek + .01) }
    else if (b = q('[data-play]')) beep(b.dataset.play);
    else if (b = q('[data-sfx]')) addSfx(b.dataset.sfx);
    else if (b = q('[data-preset]')) addText(b.dataset.preset);
    else if (b = q('[data-fxadd]')) { addFx(b.dataset.fxadd); rSide() }
    else if (b = q('[data-look]')) change(d => { d.color.look = b.dataset.look; d.color.look_amount = 1 });
    else if (b = q('[data-a]')) ({ cue: addCue, note: addNote, fx: () => addFx(), capauto: () => change(d => { d.capY = null }), colreset: () => change(d => { d.color = { ...COLOR0 } }) })[b.dataset.a]();
    else if (b = q('[data-sel]')) { const k = b.dataset.sel, o = find(k, b.dataset.id); select({ k, id: b.dataset.id }); if (o && !E.playing) { const [a, e2] = span(k, o); seek(k == 'text' ? Math.min(a + .45, (a + e2) / 2) : a + .01) } }
  }

  /* ---------- inspector ---------- */
  function rInsp() {
    const d = E.doc, s = E.sel, o = selObj(), langs = E.d.edl.languages, L = E.lang, rel = t => (t - d.start).toFixed(2);
    const head = (k, del = true) => `<h4>${KNAME[k]}<span class="sp"></span>${del ? `<button class="b er sm" data-a="del">${icon('trash')}Eliminar</button>` : ''}</h4>`;
    const timing = (a, len, minLen) => `<div class="row" style="grid-template-columns:1fr 1fr"><div><label>Empieza (s)</label><input type="number" step="0.05" data-n="a" value="${rel(a)}"></div>${len == null ? '' : `<div><label>Dura (s)</label><input type="number" step="0.05" min="${minLen}" data-n="len" value="${len.toFixed(2)}"></div>`}</div>`;
    let h;
    if(s && s.k === 'media' && o) {
      h=`${head('media')}<p data-ui-content>${esc(resource(o.resource_id)?.name || o.resource_id)}</p>${timing(o.t,o.duration,.1)}<label>Inicio en el recurso (s)</label><input type="number" min="0" step="0.1" data-i="mediaoffset" value="${o.offset}"><label>Volumen</label><input type="number" min="0" max="3" step="0.1" data-i="mediagain" value="${o.gain}">`;
    } else if (s && s.k == 'cue' && o) {
      h = `${head('cue')}<label>Texto (${L.toUpperCase()})</label><input data-i="cuetext" data-focus value="${esc(o.words.map(w => w.w).join(' '))}" spellcheck="false">
        <label>Palabras resaltadas</label><div class="words">${o.words.map((w, i) => `<button data-w="${i}" class="${w.hl ? 'hl' : ''}">${esc(w.w)}</button>`).join('')}</div>
        <p class="hint">Toca una palabra para pintarla como palabra clave. La palabra que se está diciendo siempre se ilumina sola.</p>
        ${timing(o.start, o.end - o.start, .1)}
        <div style="margin-top:12px"><button class="b g sm" data-a="split">${icon('split')}Dividir en el cursor</button></div>
        ${o.untr ? '<div class="msg wa">Sin traducir: este texto viene de la transcripción original.</div>' : ''}`;
    } else if (s && s.k == 'note' && o) {
      h = `${head('note')}${langs.map((l, i) => `<span class="lg">${l}</span><textarea data-i="note" data-lang="${l}" rows="3" maxlength="200" ${i ? '' : 'data-focus'}>${esc(o.text[l] || '')}</textarea>`).join('')}
        ${timing(o.t, o.duration, .5)}<p class="hint">Las notas salen en un recuadro arriba. Que no coincidan con el gancho.</p>`;
    } else if (s && s.k == 'hook') {
      h = `${head('hook', false)}${langs.map((l, i) => `<span class="lg">${l}</span><textarea data-i="hook" data-lang="${l}" rows="2" maxlength="120" ${i ? '' : 'data-focus'}>${esc(d.hook.text[l] || '')}</textarea>`).join('')}
        <div class="fld"><span>Duración</span><input type="range" data-i="hookdur" min="0" max="${Math.min(8, d.end - d.start).toFixed(1)}" step="0.1" value="${d.hook.duration}"><output>${d.hook.duration.toFixed(1)} s</output></div>`;
    } else if (s && s.k == 'fx' && o) {
      const sp = fxSpec(o.type);
      h = `${head('fx')}<label>Tipo</label><select data-i="fxtype">${E.d.catalog.effects.map(e => `<option value="${e.id}" ${e.id == o.type ? 'selected' : ''}>${esc(e.label)}</option>`).join('')}</select>
        <p class="hint">${esc(sp.desc)}</p>
        ${o.type == 'face_focus' ? `<p class="hint">Región en el video original. Ajusta el recuadro si la cara se mueve; no hay seguimiento automático.</p>${['x','y','w','h'].map(k => `<div class="fld"><span>${({x:'Izquierda',y:'Arriba',w:'Ancho',h:'Alto'})[k]}</span><input type="range" data-i="face-${k}" min="${k == 'w' || k == 'h' ? .04 : 0}" max="1" step="0.01" value="${(o.region || {x:.25,y:.25,w:.5,h:.5})[k]}"><output>${Math.round((o.region || {x:.25,y:.25,w:.5,h:.5})[k] * 100)}%</output></div>`).join('')}` : o.type == 'bw' ? '' : `<div class="fld"><span>Fuerza</span><input type="range" data-i="fxint" min="0.2" max="2" step="0.05" value="${o.intensity}"><output>${Math.round(o.intensity * 100)}%</output></div>`}
        ${timing(o.t, o.duration, sp.min)}<p class="hint">Entre ${sp.min} y ${sp.max} segundos.</p>`;
    } else if (s && s.k == 'sfx' && o) {
      h = `${head('sfx')}<label>Sonido</label><select data-i="sid">${E.d.library.filter(l => l.kind == 'sfx').map(l => `<option value="${l.id}" ${l.id == o.sid ? 'selected' : ''}>${esc(l.description.split(':')[0])}</option>`).join('')}</select>
        <div class="fld"><span>Volumen</span><input type="range" data-i="sfxgain" min="0" max="3" step="0.05" value="${o.gain}"><output>${Math.round(o.gain * 100)}%</output></div>
        ${timing(o.t, null)}<div style="margin-top:12px"><button class="b g sm" data-a="hear">${icon('play')}Escuchar</button></div>`;
    } else if (s && s.k == 'text' && o) {
      const C = E.d.catalog, rng = (k, l, min, max, step, unit = '') => `<div class="fld"><span>${l}</span><input type="range" data-tp="${k}" min="${min}" max="${max}" step="${step}" value="${o[k]}"><output>${fmtTp(k, o[k])}${unit}</output></div>`;
      const opts = (list, v) => list.map(([k, l]) => `<option value="${k}" ${k == v ? 'selected' : ''}>${esc(l)}</option>`).join('');
      h = `${head('text')}${langs.map((l, i) => `<span class="lg">${l}</span><textarea data-tp="text" data-lang="${l}" rows="2" maxlength="200" ${i ? '' : 'data-focus'}>${esc(o.text[l] || '')}</textarea>`).join('')}
        <label>Fuente</label><select data-tp="font">${Object.entries(E.d.fonts).map(([k, f]) => `<option translate="no" value="${k}" ${k == o.font ? 'selected' : ''}>${esc(f.label)}</option>`).join('')}</select>
        ${rng('size', 'Tamaño', 16, 300, 2)}
        <div class="row c3"><div><label>Color</label><input type="color" data-tp="color" value="${o.color}"></div>
         <div><label>${o.box ? 'Caja' : 'Borde'}</label><input type="color" data-tp="${o.box ? 'box' : 'stroke'}" value="${o.box || o.stroke}"></div>
         <div><label>Fondo</label><label class="tog"><input type="checkbox" data-tp="boxon" ${o.box ? 'checked' : ''}>Caja</label></div></div>
        ${o.box ? rng('box_opacity', 'Opacidad caja', .1, 1, .05) : rng('stroke_w', 'Grosor borde', 0, 24, .5) + rng('glow', 'Brillo', 0, 12, .5)}
        ${rng('opacity', 'Opacidad', .1, 1, .05)}${rng('rotate', 'Giro', -45, 45, 1, '°')}
        <label class="tog"><input type="checkbox" data-tp="upper" ${o.upper ? 'checked' : ''}>MAYÚSCULAS</label>
        <h4 style="margin-top:14px">Posición</h4>${rng('x', 'Horizontal', 0, 1, .005)}${rng('y', 'Vertical', 0, 1, .005)}
        <div style="display:flex;gap:6px"><button class="b g sm" data-a="tcenter">Centrar</button><button class="b g sm" data-a="twhole">Todo el clip</button></div>
        <h4 style="margin-top:14px">Animación</h4>
        <div class="row" style="grid-template-columns:1fr 1fr"><div><label>Entrada</label><select data-tp="anim_in">${opts(C.anim_in, o.anim_in)}</select></div><div><label>Salida</label><select data-tp="anim_out">${opts(C.anim_out, o.anim_out)}</select></div></div>
        ${timing(o.t, o.duration, .3)}`;
    } else {
      const dur = d.end - d.start, lim = E.d.limits, rv = E.d.clip.review || {}, untr = Object.values(d.tracks).flat().filter(c => c.untr && inClip(c.start, c.end)).length;
      h = `<h4>Clip</h4><div class="stat"><div><small>Duración</small><b>${dur.toFixed(1)} s</b></div><div><small>Inicio en fuente</small><b>${tc(d.start)}</b></div>
         <div><small>Subtítulos</small><b>${track().filter(c => inClip(c.start, c.end)).length}</b></div><div><small>Notas</small><b>${d.notes.filter(n => inClip(n.t, n.t + n.duration)).length}</b></div></div>
        ${E.d.frame.safe.enforced ? `<p class="hint">Shorts/Reels: textos y animaciones se ajustan automáticamente al área útil. Ocultar la guía no desactiva la protección. Las interfaces reales pueden variar.</p>` : ''}
        ${E.d.requires_safe_render ? '<div class="msg wa">La vista previa incluye las correcciones de encuadre y zonas seguras. Pulsa Renderizar para actualizar el video anterior.</div>' : ''}
        ${d.editor_mode !== 'long' && dur > lim.recommended_max ? `<div class="msg wa">Dura más de ${lim.recommended_max} s, el máximo configurado para clips. Se puede renderizar, pero el guard de duración lo marcará.</div>` : ''}
        ${untr ? `<div class="msg wa">${untr} subtítulo(s) sin traducir en los tramos añadidos.</div>` : ''}
        ${rv.feedback ? `<div class="msg info"><b>Revisión:</b> ${esc(rv.feedback)}</div>` : ''}
        <p class="hint" style="margin-bottom:14px">Selecciona un elemento en la línea de tiempo o en la vista previa para editarlo aquí.</p>
        <h4>Atajos</h4><div class="kbd">
         <kbd>Espacio</kbd><span>Reproducir / pausar</span><kbd>← →</kbd><span>Un fotograma (Mayús: 1 s)</span><kbd>I</kbd> <span>Inicio del clip en el cursor</span><kbd>O</kbd><span>Final del clip en el cursor</span>
         <kbd>S</kbd><span>Dividir subtítulo</span><kbd>T</kbd><span>Texto nuevo</span><kbd>N</kbd><span>Nota nueva</span><kbd>Z</kbd><span>Zoom nuevo</span><kbd>Supr</kbd><span>Eliminar selección</span>
         <kbd>Ctrl+Z</kbd><span>Deshacer</span><kbd>Ctrl+Y</kbd><span>Rehacer</span><kbd>Ctrl+rueda</kbd><span>Zoom de la línea de tiempo</span><kbd>Alt</kbd><span>Arrastrar sin imán</span></div>`;
    }
    $('#edInsp').innerHTML = h;
  }
  const fmtTp = (k, v) => k == 'x' || k == 'y' || k == 'opacity' || k == 'box_opacity' ? Math.round(v * 100) + '%' : String(v);
  function setTextProp(t, discrete) {   // una propiedad del texto seleccionado
    const s = E.sel, k = t.dataset.tp, v = t.type == 'checkbox' ? t.checked : t.type == 'range' ? +t.value : t.value;
    change(d => {
      const o = d.texts.find(x => x.id == s.id); if (!o) return;
      if (k == 'text') o.text[t.dataset.lang] = v; else if (k == 'boxon') { o.box = v ? '#FFE500' : ''; if (v && o.color.toUpperCase() == '#FFFFFF') o.color = '#000000' } else o[k] = v;
    }, { key: 'tp' + k + s.id, soft: !discrete });
    if (t.type == 'range') t.nextElementSibling.textContent = fmtTp(k, v) + (k == 'rotate' ? '°' : '');
    if (k == 'text' && E.tab == 'text' && t.dataset.lang == E.lang) { const row = $(`#edSide [data-id="${s.id}"] .tx`); if (row) row.textContent = v.replace(/\n/g, ' ') || '—' }
    if ((k == 'anim_in' || k == 'anim_out') && !E.playing) { const o = selObj(); if (o) { seek(k == 'anim_in' ? o.t : Math.max(o.t, o.t + o.duration - .6)); play() } }
  }
  function onInspInput(e) {
    const t = e.target, i = t.dataset.i, s = E.sel; if (!s) return;
    if (t.dataset.tp) { if (t.type != 'checkbox' && t.tagName != 'SELECT') setTextProp(t, false); return }
    if(i==='mediaoffset' || i==='mediagain'){change(d=>{const x=d.media.find(x=>x.id===s.id);if(x)x[i==='mediaoffset'?'offset':'gain']=Math.max(0,+t.value)}, {key:i+s.id,soft:true});return;}
    if (i == 'fxint') { change(d => { const x = d.fx.find(x => x.id == s.id); if (x) x.intensity = +t.value }, { key: 'fxint' + s.id, soft: true }); t.nextElementSibling.textContent = Math.round(t.value * 100) + '%'; return }
    if (i && i.startsWith('face-')) {
      const k = i.slice(5);
      change(d => {
        const x = d.fx.find(x => x.id == s.id); if (!x) return;
        x.region = {x:.25,y:.25,w:.5,h:.5,...x.region}; const r = x.region;
        r[k] = +t.value;
        if (k == 'x') r.x = Math.min(r.x, 1-r.w);
        if (k == 'y') r.y = Math.min(r.y, 1-r.h);
        if (k == 'w') r.w = Math.min(r.w, 1-r.x);
        if (k == 'h') r.h = Math.min(r.h, 1-r.y);
        t.value = r[k];
      }, {key:i+s.id,soft:true});
      t.nextElementSibling.textContent = Math.round(t.value*100)+'%'; return;
    }
    if (i == 'sfxgain') { change(d => { const x = d.sfx.find(x => x.id == s.id); if (x) x.gain = +t.value }, { key: 'sfxgain' + s.id, soft: true }); t.nextElementSibling.textContent = Math.round(t.value * 100) + '%'; return }
    if (!i) return;
    if (i == 'cuetext') {
      change(d => { const c = d.tracks[E.lang].find(x => x.id == s.id); if (c) setCueText(c, t.value) }, { key: 'cue' + s.id, soft: true });
      const row = $(`#edSide input[data-cue="${s.id}"]`); if (row) row.value = t.value;
      const c = selObj(), box = $('#edInsp .words'); if (c && box) box.innerHTML = c.words.map((w, j) => `<button data-w="${j}" class="${w.hl ? 'hl' : ''}">${esc(w.w)}</button>`).join('');
    } else if (i == 'note') { change(d => { const n = d.notes.find(x => x.id == s.id); if (n) n.text[t.dataset.lang] = t.value }, { key: 'note' + s.id + t.dataset.lang, soft: true }); if (E.tab == 'text') { const row = $(`#edSide [data-id="${s.id}"] .tx`); if (row && t.dataset.lang == E.lang) row.textContent = t.value || '—' } }
    else if (i == 'hook') { change(d => { d.hook.text[t.dataset.lang] = t.value }, { key: 'hook' + t.dataset.lang, soft: true }); const ta = $(`#edSide [data-hook="${t.dataset.lang}"]`); if (ta) ta.value = t.value }
    else if (i == 'hookdur') { change(d => { d.hook.duration = +t.value }, { key: 'hookdur', soft: true }); t.nextElementSibling.textContent = (+t.value).toFixed(1) + ' s' }
  }
  function onInspChange(e) {
    const t = e.target, s = E.sel; if (!s) return;
    if (t.dataset.i == 'sid') { change(d => { const x = d.sfx.find(x => x.id == s.id); if (x) x.sid = t.value }); beep(t.value); return }
    if (t.dataset.tp) { if (t.type == 'checkbox' || t.tagName == 'SELECT') setTextProp(t, true); return }
    if (t.dataset.i == 'fxtype') { change(d => { const x = d.fx.find(x => x.id == s.id); if (x) { const sp = fxSpec(t.value); x.type = sp.id; x.duration = clamp(x.duration, sp.min, sp.max) } }); return }
    if (!t.dataset.n) return;
    const o = selObj(); if (!o) return;
    const [a0, b0] = span(s.k, o), len0 = b0 - a0, v = +t.value, d = E.doc;
    if (!isFinite(v)) { rInsp(); return }
    const min = minLen(s.k, o), max = maxLen(s.k, o);
    const [lo, hi] = neighbours(s.k, s.id, a0, b0);
    let a = a0, b = b0;
    if (t.dataset.n == 'a') { a = clamp(d.start + v, lo, Math.max(lo, hi - len0)); b = a + len0 }
    else b = clamp(a0 + v, a0 + min, Math.min(hi, a0 + max));
    change(() => { setSpan(s.k, find(s.k, s.id), a, b); sortDoc() });
  }
  function onInspClick(e) {
    const w = e.target.closest('[data-w]'), s = E.sel;
    if (w && s) { change(d => { const c = d.tracks[E.lang].find(x => x.id == s.id); if (c) c.words[+w.dataset.w].hl = !c.words[+w.dataset.w].hl }, { soft: true }); w.classList.toggle('hl'); return }
    const a = e.target.closest('[data-a]'); if (!a) return;
    ({
      del: delSel, split: splitCue, hear: () => { const o = selObj(); if (o) beep(o.sid, o.gain) },
      tcenter: () => change(d => { const o = d.texts.find(x => x.id == s.id); if (o) o.x = .5 }),
      twhole: () => change(d => { const o = d.texts.find(x => x.id == s.id); if (o) { o.t = d.start; o.duration = r3(d.end - d.start) } }),
    })[a.dataset.a]();
  }

  /* ---------- teclado ---------- */
  function shortClipboard(command){
    const object=selObj(),kind=E.sel?.k;
    if(command!=='v'){
      if(!object||['clip','hook'].includes(kind))return;
      EditorCore.copy(kind,object,span(kind,object)[0],E.project,'shorts');
      if(command==='x'){delSel();return;}
      if(command==='c')return;
    }
    const at=command==='d'?span(kind,object)[1]:E.t,entry=EditorCore.read(E.project,'shorts',at);
    if(!entry)return;
    const [a,b]=span(entry.kind,entry.value);
    if(a<E.doc.start||b>E.doc.end){toast('El elemento no cabe en el rango actual.','er');return;}
    entry.value.id=uid();
    change(()=>{listOf(entry.kind).push(entry.value);sortDoc();});
    select({k:entry.kind,id:entry.value.id});
  }
  function onKey(e) {
    if (!E || $('.ed-over') || $('#modal').innerHTML) return;
    const tag = (e.target.tagName || '').toLowerCase(), typing = tag == 'textarea' || tag == 'select' || (tag == 'input' && !['range', 'checkbox'].includes(e.target.type));
    const mod = e.ctrlKey || e.metaKey, k = e.key.toLowerCase();
    if (mod && k == 'z' && !typing) { e.preventDefault(); undo(e.shiftKey); return }
    if (mod && k == 'y' && !typing) { e.preventDefault(); undo(true); return }
    if (mod && k == 's') { e.preventDefault(); clearTimeout(saveTimer); saveDraft(); return }
    if (typing) { if (e.key == 'Escape') e.target.blur(); return }
    if(mod&&['c','x','v','d'].includes(k)){e.preventDefault();shortClipboard(k);return;}
    if (mod) return;
    const f = 1 / E.d.frame.fps;
    const map = {
      ' ': toggle, k: toggle, arrowleft: () => { pause(); seek(E.t - (e.shiftKey ? 1 : f)) }, arrowright: () => { pause(); seek(E.t + (e.shiftKey ? 1 : f)) },
      home: () => { pause(); seek(E.doc.start) }, end: () => { pause(); seek(E.doc.end - .03) }, i: () => setEdge('start'), o: () => setEdge('end'),
      s: splitCue, n: addNote, z: () => addFx(), t: () => addText(), delete: delSel, backspace: delSel, escape: () => select(null),
      '+': () => setPps(E.pps * 1.25, E.t), '=': () => setPps(E.pps * 1.25, E.t), '-': () => setPps(E.pps / 1.25, E.t),
    };
    if (map[k]) { e.preventDefault(); map[k]() }
  }

  return { open, close, get state() { return E }, tick: () => { if (E) { E.keys = {}; stage() } } };
})();
window.addEventListener('error', e => { try { toast('Error en la página: ' + e.message, 'er') } catch { } });
