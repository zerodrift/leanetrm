// Exercise the shared grid through visible rows, including its virtual scroll.
async function clickRow({evaluate,wait,idle},host,match){
 await wait(`!!document.querySelector(${JSON.stringify(host+' .tabulator-row[data-row-key]')})&&`+idle);
 await evaluate(`document.querySelector(${JSON.stringify(host+' .tabulator-tableholder')}).scrollTop=0;new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))`);
 for(let attempt=0;attempt<80;attempt++){
  const row=`[...document.querySelectorAll(${JSON.stringify(host+' .tabulator-row[data-row-key]')})].find(r=>${match})`;
  if(await evaluate(`!!(${row})`)){await evaluate(`${row}.click()`);return;}
  const moved=await evaluate(`(()=>{const view=document.querySelector(${JSON.stringify(host+' .tabulator-tableholder')}),before=view.scrollTop;view.scrollTop+=Math.max(100,view.clientHeight*.8);return view.scrollTop!==before})()`);
  if(!moved)break;await evaluate('new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))');
 }
 throw Error('The expected authorized grid row was not found: '+host);
}
export const clickPhysicalTrade=(actions,tradeId)=>clickRow(actions,'#physical-operations-trades',`r.dataset.rowKey.startsWith(${JSON.stringify(tradeId+':')})`);
export async function openPhysicalTrade(actions,tradeId){await clickPhysicalTrade(actions,tradeId);await actions.wait("!document.querySelector('#physical-operations-instrument').hidden&&!document.querySelector('#physical-operations-refresh').disabled&&"+actions.idle);}
export async function openGasDelivery(actions,day,side){
 await clickRow(actions,'[data-ops=rows]',`r.dataset.rowKey.endsWith(${JSON.stringify(':'+day)})${side?'&&r.querySelector(\'[tabulator-field=c1]\').textContent==='+JSON.stringify(side):''}`);
 await actions.wait(`document.querySelector('[data-ops=selection]').textContent.includes(${JSON.stringify(day)})&&!document.querySelector('[data-ops=refresh]').disabled&&`+actions.idle);
}
