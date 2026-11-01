export async function run({command,evaluate,wait,click,set,persona,check,idle,url}){
 const rows="document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)').length";
 async function open(){
  await command('Page.enable');await command('Runtime.enable');await command('Page.navigate',{url});await wait(idle);
  await persona('trader');
  const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')).id;})()");
  await set('#snapshot',frame);await wait(idle);await click('#nav-market');
  await evaluate(`(()=>{const send=window.fetch;window.fetch=async(input,init)=>{
   if(String(input)!=='/api/trading/workspace/stream')return send(input,init);
   window.__streamSignal=init.signal;
   const response=await send(input,init);if(!response.ok)return response;
   const frames=(await response.text()).trim().split('\\n').map(line=>JSON.parse(line));
   window.__streamFrames=frames;
   const first=frames.findIndex(f=>f.type==='rows'&&f.rows.length);
   if(first<0)throw Error('Synthetic portfolio has no streamed rows');
   return new Response(new ReadableStream({start(controller){
    const emit=f=>controller.enqueue(new TextEncoder().encode(JSON.stringify(f)+'\\n'));
    frames.slice(0,first+1).forEach(emit);
    window.__releaseStream=mode=>{try{
     if(mode==='denied')emit({type:'error',code:'access_denied',message:'Synthetic authority revoked'});
     else if(mode!=='truncated')for(const frame of frames.slice(first+1)){
      if(mode==='context'&&frame.type==='complete')frame.blotter.context.contextId='wrong-context';emit(frame);
     }
     controller.close();
    }catch{}};
   }}),{status:200,headers:{'Content-Type':'application/x-ndjson'}});
  };})()`);
  await evaluate("document.querySelector('#nav-futures').click()");
  await wait(`typeof window.__releaseStream==='function'&&document.querySelector('#blotter-load-text').textContent.includes('complete')&&document.body.getAttribute('aria-busy')==='false'`);
 }
 try{
  await open();
  await check('valuation progress advances before the final frame without displaying rows',`${rows}===0&&document.querySelector('#futures-page').dataset.pricingState==='pending'&&document.querySelector('#blotter-load-text').textContent.includes('complete')`);
  await check('pending valuation has no rows, group totals, export or risk action',"document.querySelectorAll('#desk-positions .tabulator-group').length===0&&document.querySelector('#blotter-export').disabled&&document.querySelector('#blotter-risk').disabled&&document.querySelector('#blotter-strip').textContent.includes('Pending')");
  await evaluate('window.__releaseStream()');await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
  await check('completion displays the complete grouped report',`${rows}>0&&!document.querySelector('#blotter-export').disabled&&document.querySelector('#blotter-load-status').hidden&&document.querySelector('#blotter-strip').textContent.indexOf('Pending')<0`);
  for(const failure of ['denied','truncated','context']){
   await open();await evaluate(`window.__releaseStream(${JSON.stringify(failure)})`);
   await wait("document.querySelector('#futures-page').dataset.pricingState!=='pending'&&"+idle);
   await check(failure+' stream never displays incomplete rows or enables export',`${rows}===0&&document.querySelector('#blotter-export').disabled&&!document.querySelector('#error').hidden`,"({rows:document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)').length,exportDisabled:document.querySelector('#blotter-export').disabled,errorHidden:document.querySelector('#error').hidden,error:document.querySelector('#error').textContent})");
  }
  await open();await click('#nav-market');await wait(idle);
  await check('leaving the blotter cancels the active stream',"window.__streamSignal.aborted");
  await evaluate('window.__releaseStream()');
  await check('late frames cannot restore rows after leaving the blotter',`${rows}===0&&document.querySelector('#futures-page').hidden`);
 }finally{await evaluate('window.__releaseStream?.()');}
}
