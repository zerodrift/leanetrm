// A card or cell starts its optional read only after it enters the viewport.
// Its owner disposes the observer when the account, context or content changes.
export function whenVisible(target:Element,read:()=>void):()=>void{
  let stopped=false;
  const observer=new IntersectionObserver(entries=>{
    if(stopped||!target.isConnected||!entries.some(entry=>entry.target===target&&entry.isIntersecting))return;
    stopped=true;observer.disconnect();read();
  });
  observer.observe(target);
  return ()=>{stopped=true;observer.disconnect();};
}
