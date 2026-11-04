// A card or cell starts its optional read only after it enters the viewport.
// Its owner disposes the observer when the account, context or content changes.
export function visibleReads(current:()=>boolean,read:(target:Element)=>void){
  let stopped=false;
  const targets=new Set<Element>();
  const observer=new IntersectionObserver(entries=>{
    for(const entry of entries){
      if(stopped||!current()){cancel();return;}
      if(entry.isIntersecting&&entry.target.isConnected&&targets.delete(entry.target)){observer.unobserve(entry.target);read(entry.target);}
    }
  });
  function cancel(){stopped=true;targets.clear();observer.disconnect();}
  return {cancel,reset:(next:Iterable<Element>)=>{observer.disconnect();targets.clear();if(!stopped&&current())for(const target of next){targets.add(target);observer.observe(target);}}};
}
export function whenVisible(target:Element,read:()=>void):()=>void{
  const observer=visibleReads(()=>true,()=>{observer.cancel();read();});observer.reset([target]);return observer.cancel;
}
