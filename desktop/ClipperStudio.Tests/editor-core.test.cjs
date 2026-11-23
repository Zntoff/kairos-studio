const {test}=require('node:test'),assert=require('node:assert/strict');
const core=require('../../clipper/web/static/editor-core.js');
test('Sequential items share a track even when the document is unsorted',()=>{
  const clips=[{start:10,duration:5},{start:0,duration:5},{start:5,duration:5}];
  const packed=core.lanes(clips,x=>[x.start,x.start+x.duration]);
  assert.equal(packed.count,1);assert.deepEqual(packed.positions,[0,0,0]);
});
test('Only overlapping items need another lane; later clips reuse it',()=>{
  const packed=core.lanes([[0,5],[2,4],[5,8],[4,5]],x=>x);
  assert.equal(packed.count,2);assert.deepEqual(packed.positions,[0,1,0,1]);
});
test('Repeated pastes retain the source trim and are independent objects',()=>{
  const item={start:3,duration:2,offset:17.25,gain:.4,fit:'cover'};
  core.copy('media',item,3,'project-a','composition');
  const first=core.read('project-a','composition',10),second=core.read('project-a','composition',20);
  assert.equal(first.value.offset,17.25);assert.equal(first.value.start,10);assert.equal(second.value.start,20);
  first.value.gain=0;assert.equal(second.value.gain,.4);assert.equal(item.start,3);
  assert.equal(core.read('project-b','composition',10),null);
});
test('Pasting captions shifts every acoustic word timestamp by the same amount',()=>{
  const cue={start:1,end:4,words:[{w:'Hola',start:1,end:1.4},{w:'mundo',start:3,end:3.6}]};
  core.copy('cue',cue,1,'project-a','shorts');
  const pasted=core.read('project-a','shorts',8).value;
  assert.deepEqual(pasted.words.map(w=>[w.start,w.end]),[[8,8.4],[10,10.6]]);
  assert.equal(pasted.end,11);assert.equal(cue.words[1].start,3);
  assert.equal(core.read('project-a','composition',8),null);
});
