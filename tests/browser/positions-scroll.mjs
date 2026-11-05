// The grid restores its retained row after the action releases busy. Let that placement
// settle before moving to the first virtual rows; both fixtures use the same native scroll.
export async function scrollPositionsToTop(evaluate){
  await evaluate(`(async()=>{
    const frames=()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
    await new Promise(resolve=>setTimeout(resolve,500));
    const holder=document.querySelector('#desk-positions .tabulator-tableholder');
    if(!holder)throw Error('The positions viewport is unavailable.');
    for(let attempt=0;attempt<5&&holder.scrollTop>0;attempt++){holder.scrollTop=0;await frames();}
    if(!holder.isConnected||holder.scrollTop!==0)throw Error('The positions viewport did not settle at the top.');
  })()`);
}
