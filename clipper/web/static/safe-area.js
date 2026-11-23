/* Layout companion to desktop/Engine/SafeArea.cs. Margins come from the server. */
const SafeLayout = (() => {
  function text(tx, language, safe, fontScale, measure, wrap) {
    const W=safe.canvas_width || 1080,H=safe.canvas_height || 1920;
    const left = safe.left, right = W - safe.right, top = safe.top, bottom = H - safe.bottom;
    const animation = tx.anim_in || 'fade', factor = animation == 'zoom' ? 1.6 : animation == 'pop' ? 1.12 : 1;
    const slide = animation.startsWith('slide_') ? 140 : 0;
    const lm = animation == 'slide_right' ? slide : 0, rm = animation == 'slide_left' ? slide : 0;
    const tm = animation == 'slide_down' ? slide : 0, bm = animation == 'slide_up' ? slide : 0;
    const pad = tx.box ? 14 : (tx.stroke_w ?? 6) + (tx.glow || 0) * 3, angle = Math.abs(tx.rotate || 0) * Math.PI / 180;
    for (let em = tx.size ?? 80; em >= 8; em -= 1) {
      const fs = Math.max(1, Math.round(em / fontScale));
      const lines = (tx.text[language] || '').replace(/\r/g, '').split('\n').filter(s => s.trim()).flatMap(s => wrap(tx.upper ? s.toUpperCase() : s, fs, Math.max(40, (right-left-lm-rm)/factor-2*pad)));
      const w = Math.max(0, ...lines.map(s => measure(s, fs))) + 2*pad, h = lines.length*fs + 2*pad;
      const hw = (Math.cos(angle)*w + Math.sin(angle)*h)*factor/2+4, hh = (Math.sin(angle)*w + Math.cos(angle)*h)*factor/2+4;
      const minX = Math.ceil(left+hw+lm), maxX = Math.floor(right-hw-rm), minY = Math.ceil(top+hh+tm), maxY = Math.floor(bottom-hh-bm);
      if (minX > maxX || minY > maxY) continue;
      const x = Math.min(maxX, Math.max(minX, Math.round((tx.x ?? .5)*W))), y = Math.min(maxY, Math.max(minY, Math.round((tx.y ?? .3)*H)));
      return {x,y,em,fs,lines,hw,hh,lm,rm,tm,bm};
    }
    throw new Error('El texto no cabe en la zona segura. Reduce su contenido o rotación.');
  }
  return { text };
})();
if (typeof module !== 'undefined') module.exports = SafeLayout;
