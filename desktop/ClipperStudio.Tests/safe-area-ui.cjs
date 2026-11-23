const assert = require('node:assert/strict');
const safeLayout = require('../../clipper/web/static/safe-area.js');
const safe = {left:80,right:180,top:300,bottom:680};
const measure = (text,fs) => text.length*fs*.6;
const wrap = (text,fs,max) => {
  const lines=[]; let current='';
  for(const word of text.split(/\s+/)) {
    const trial=(current+' '+word).trim();
    if(current && measure(trial,fs)>max) {lines.push(current);current=word;} else current=trial;
  }
  if(current) lines.push(current); return lines;
};
let count=0;
for(const animation of ['none','fade','pop','zoom','slide_up','slide_down','slide_left','slide_right'])
  for(const angle of [-45,0,45]) for(const corner of [[0,0],[0,1],[1,0],[1,1]]) {
    const tx={size:180,rotate:angle,anim_in:animation,x:corner[0],y:corner[1],text:{es:'MIRA LA REACCIÓN COMPLETA'},stroke_w:8,glow:3};
    const p=safeLayout.text(tx,'es',safe,1,measure,wrap);
    const width=Math.max(...p.lines.map(line=>measure(line,p.fs)))+2*(8+9), height=p.lines.length*p.fs+2*(8+9);
    const radians=angle*Math.PI/180;
    for(const phase of [0,.15,.18,.25,.3,1]) {
      let scale=1,dx=0,dy=0;
      if(animation==='zoom') scale=phase<.25?1.6-.6*phase/.25:1;
      if(animation==='pop') scale=phase<.18?.4+.72*phase/.18:phase<.28?1.12-.12*(phase-.18)/.1:1;
      if(animation.startsWith('slide') && phase<.3) {
        const shift=140*(1-phase/.3);
        if(animation==='slide_left')dx=shift;if(animation==='slide_right')dx=-shift;
        if(animation==='slide_up')dy=shift;if(animation==='slide_down')dy=-shift;
      }
      const hw=(Math.abs(Math.cos(radians))*width+Math.abs(Math.sin(radians))*height)*scale/2;
      const hh=(Math.abs(Math.sin(radians))*width+Math.abs(Math.cos(radians))*height)*scale/2;
      assert(p.x+dx-hw>=safe.left && p.x+dx+hw<=1080-safe.right, `${animation} x envelope`);
      assert(p.y+dy-hh>=safe.top && p.y+dy+hh<=1920-safe.bottom, `${animation} y envelope`);
    }
    count++;
  }
console.log(`Passed ${count} UI safe-area placement cases, including animation peaks and rotations.`);
for(const canvasHeight of [463,608,1080,1920]) {
  const margins={left:100,right:100,top:Math.floor(canvasHeight*.125),bottom:Math.floor(canvasHeight*.125),canvas_width:1080,canvas_height:canvasHeight};
  for(const animation of ['none','zoom','slide_up','slide_down']) for(const y of [0,1]) {
    const placement=safeLayout.text({size:110,x:.5,y,anim_in:animation,text:{en:'SHARED TOOLS'},stroke_w:6},'en',margins,1,measure,wrap);
    assert(placement.y-placement.hh-placement.tm>=margins.top);
    assert(placement.y+placement.hh+placement.bm<=canvasHeight-margins.bottom);
  }
}
console.log('Passed 32 composition safe-area cases across cinematic, landscape, square and portrait canvases.');
