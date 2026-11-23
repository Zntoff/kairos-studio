/* Shared timeline primitives for Shorts and multi-source compositions. */
const EditorCore=(()=>{
  const clone=value=>JSON.parse(JSON.stringify(value));
  let clipboard=null;
  function lanes(items,span){
    const ends=[],positions=Array(items.length).fill(0);
    items.map((item,index)=>({index,range:span(item)})).sort((a,b)=>a.range[0]-b.range[0]||a.index-b.index).forEach(({index,range:[start,end]})=>{
      let lane=ends.findIndex(value=>{return value<=start+.0001;});
      if(lane<0)lane=ends.length;
      positions[index]=lane;ends[lane]=end;
    });
    return {positions,count:Math.max(1,ends.length)};
  }
  function copy(kind,value,start,project,scope){clipboard={kind,value:clone(value),start,project,scope};}
  function read(project,scope,at){
    if(!clipboard||clipboard.project!==project||clipboard.scope!==scope)return null;
    const value=clone(clipboard.value),delta=at-clipboard.start;
    for(const key of ['t','start','end'])if(typeof value[key]==='number')value[key]=Math.round((value[key]+delta)*10000)/10000;
    if(value.words)for(const word of value.words)for(const key of ['start','end'])word[key]=Math.round((word[key]+delta)*10000)/10000;
    return {kind:clipboard.kind,value};
  }
  const has=(project,scope)=>!!clipboard&&clipboard.project===project&&clipboard.scope===scope;
  const editing=target=>!!target.closest('input,textarea,select,[contenteditable=true]');
  return {lanes,copy,read,has,editing};
})();
if(typeof module!=='undefined')module.exports=EditorCore;
