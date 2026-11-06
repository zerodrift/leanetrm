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
export async function clickPhysicalTrade(actions,tradeId){await actions.wait("!document.querySelector('#physical-operations-refresh').disabled&&"+actions.idle);await clickRow(actions,'#physical-operations-trades',`r.dataset.rowKey.startsWith(${JSON.stringify(tradeId+':')})`);}
export async function openPhysicalTrade(actions,tradeId){await clickPhysicalTrade(actions,tradeId);await actions.wait("!document.querySelector('#physical-operations-instrument').hidden&&!document.querySelector('#physical-operations-refresh').disabled&&"+actions.idle);}
export async function openGasDelivery(actions,day,side){
 await actions.wait("!document.querySelector('[data-ops=refresh]').disabled&&"+actions.idle);
 await clickRow(actions,'[data-ops=rows]',`r.dataset.rowKey.endsWith(${JSON.stringify(':'+day)})${side?'&&r.querySelector(\'[tabulator-field=c1]\').textContent==='+JSON.stringify(side):''}`);
 await actions.wait(`document.querySelector('[data-ops=selection]').textContent.includes(${JSON.stringify(day)})&&!document.querySelector('[data-ops=refresh]').disabled&&`+actions.idle);
}

// Regional close sessions filter the header's As of choices. Tests select one
// explicit original fixture frame and its session before interacting with a row.
export async function selectDeliveryFrame({evaluate,set,wait,idle},atPrefix){
 const frame=await evaluate(`(async()=>{const w=await(await fetch('/api/workspace')).json();const rows=w.frames.filter(f=>w.initialFrameIds.includes(f.id)&&f.at.startsWith(${JSON.stringify(atPrefix)}));if(rows.length!==1)throw Error('Select one exact original delivery frame.');return {id:rows[0].id,session:rows[0].close?.session??null};})()`);
 if(frame.session){await set('#close-session',frame.session);await wait(idle);}await set('#snapshot',frame.id);await wait(idle);
}
