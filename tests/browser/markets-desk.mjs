// The market board of Curves & prices (docs/markets-desk.md): opened on the newest close with no curve chosen, a card for each listed
// curve in the library's groups and order, from curve-shapes reads of at most 64 curves and no evaluate; each card states the backend's
// front contract, exact price, unit and count, or why the close prices none; the library rows keep their markup and gain the curve's
// commodity dot and sparkline; neither shows a previous close's or context's figures while another is read; a read that fails says so on
// its cards and is read again; and a card opens its curve as the library does.
export async function run({command,evaluate,wait,click,set,check,shot,idle,fitsWidth,url}){
 await command('Runtime.enable');await command('Page.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});
 const {identifier}=await command('Page.addScriptToEvaluateOnNewDocument',{source:`
  window.__shapeReads=[];window.__evaluates=0;const send=window.fetch;window.fetch=async(input,init)=>{
   const path=new URL(typeof input==='string'?input:input.url,location.href).pathname;
   if(path==='/api/evaluate')window.__evaluates++;
   if(path==='/api/market/curve-shapes'&&window.__holdShapes)await new Promise(resolve=>window.__releaseShapes=()=>{window.__holdShapes=false;window.__releaseShapes=undefined;resolve();});
   if(path==='/api/market/curve-shapes'&&window.__failShapes){window.__failShapes=false;window.__shapeReads.push({curves:JSON.parse(init.body).curves,snapshotId:JSON.parse(init.body).snapshotId,shapes:null});return new Response(JSON.stringify({code:'request_failed'}),{status:503,headers:{'Content-Type':'application/json'}});}
   const response=await send(input,init);
   if(path==='/api/market/curve-shapes')window.__shapeReads.push({curves:JSON.parse(init.body).curves,snapshotId:JSON.parse(init.body).snapshotId,shapes:response.ok?(await response.clone().json()).curves:null});
   return response;
  };`});
 try{
  await command('Page.navigate',{url:url+'/'});
  await wait(idle+"&&document.querySelectorAll('.curve-button').length>0&&performance.getEntriesByName('leanetrm:workspace-ready').length===1");
  await click('#nav-market');await wait(idle);
  await check('Curves & prices opens on the market board at the newest close, with no curve chosen',
   "(async()=>{const frames=(await(await fetch('/api/workspace/navigation')).json()).frames,newest=Math.max(...frames.map(f=>Date.parse(f.at))),latest=frames.filter(f=>Date.parse(f.at)===newest);"+
   "return latest.length===1&&document.querySelector('#snapshot').value===latest[0].id&&!document.querySelector('#market-board').hidden&&document.querySelector('#market-board-empty').hidden&&document.querySelector('#curve-detail').hidden&&document.querySelector('#market-board-status').textContent.startsWith('Prices at the close of ')&&document.querySelector('#error').hidden;})()");
  await check('the board has a card for each listed curve, in the library\'s groups and order',
   "(()=>{const library=[...document.querySelectorAll('#curves .curve-group')].map(g=>[g.querySelector('summary').textContent,...[...g.querySelectorAll('.curve-button')].map(b=>b.querySelector('span').textContent)]);"+
   "const board=[...document.querySelectorAll('.market-board-group')].map(g=>[g.querySelector('h3').textContent,...[...g.querySelectorAll('.market-card-open')].map(b=>b.textContent)]);"+
   "return library.length>1&&JSON.stringify(board)===JSON.stringify(library);})()",
   "[...document.querySelectorAll('.market-board-group h3')].map(h=>h.textContent)");
  await check('the board reads the shapes of its listed curves at the close in one read per 64 curves, rates aside, and evaluates nothing before a curve is chosen',
   "(()=>{const shaped=[...document.querySelectorAll('.market-card')].filter(c=>c.dataset.state!=='rates').map(c=>c.dataset.curve),read=window.__shapeReads.flatMap(r=>r.curves);"+
   "return shaped.length>0&&window.__shapeReads.length===Math.ceil(shaped.length/64)&&window.__shapeReads.every(r=>r.curves.length<=64&&r.shapes)&&read.length===shaped.length&&shaped.every(c=>read.includes(c))&&window.__evaluates===0;})()",
   "({reads:window.__shapeReads.map(r=>r.curves.length),evaluates:window.__evaluates})");
  await check('each priced card shows the backend\'s unit, front contract, exact front price and contract count, with its curve drawn',
   "(()=>{const shapes=new Map(window.__shapeReads.flatMap(r=>r.shapes).map(s=>[s.curve,s])),priced=[...document.querySelectorAll('.market-card[data-state=priced]')];"+
   "return priced.length>0&&priced.every(card=>{const s=shapes.get(card.dataset.curve);return s.front&&card.querySelector('.kpi-figure').textContent===s.front.price&&card.querySelector('.market-card-unit').textContent===s.unit&&"+
   "card.querySelector('.market-card-label').textContent.startsWith('Front · ')&&card.querySelector('.market-card-count').textContent===s.contractCount+(s.contractCount===1?' contract':' contracts')&&card.querySelector('svg.sparkline path.sparkline-line')&&card.querySelector('svg.sparkline').getAttribute('aria-label').startsWith(card.querySelector('.market-card-open').textContent+' · forward curve');});})()");
  await check('library rows keep their name and type text and gain the commodity dot and, for a priced curve, a decorative sparkline',
   "(()=>{const rows=[...document.querySelectorAll('.curve-button')],cards=new Map([...document.querySelectorAll('.market-card')].map(c=>[c.querySelector('.market-card-open').textContent,c]));"+
   "return rows.length===cards.size&&rows.every(b=>{const name=b.querySelector('span').textContent,card=cards.get(name),line=b.querySelector('svg.sparkline');"+
   "return card&&b.firstElementChild.classList.contains('commodity-dot')&&b.textContent===name+b.querySelector('small').textContent&&(card.dataset.state==='priced'?line?.getAttribute('aria-hidden')==='true'&&!!line.querySelector('path'):!line);});})()");
  await check('a curve\'s dot on the board and in the library share its commodity color',
   "[...document.querySelectorAll('.market-card')].every(card=>{const row=[...document.querySelectorAll('.curve-button')].find(b=>b.querySelector('span').textContent===card.querySelector('.market-card-open').textContent);return row.querySelector('.commodity-dot').style.getPropertyValue('--commodity')===card.querySelector('.commodity-dot').style.getPropertyValue('--commodity');})");
  await evaluate('window.scrollTo(0,0)');await shot('markets-board-desktop');
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await evaluate('window.scrollTo(0,0)');
  await check('the board fits a 390px screen without page-wide horizontal scrolling',fitsWidth);
  await evaluate("document.querySelector('#market-board').scrollIntoView()");await shot('markets-board-mobile');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});
  // Nothing of the previous close is shown while another close is read: every card waits for its read, and no library row has a line.
  const newest=await evaluate("document.querySelector('#snapshot').value"),shownAt=await evaluate("document.querySelector('#market-board-status').textContent");
  const older=await evaluate("[...document.querySelector('#snapshot').options].find(o=>o.value&&o.value!==document.querySelector('#snapshot').value).value");
  const nothingShown="[...document.querySelectorAll('.market-card')].length>0&&!document.querySelector('.market-card .kpi-figure')&&!document.querySelector('.market-card .sparkline')&&!document.querySelector('.curve-button .sparkline')";
  await evaluate('window.__holdShapes=true');await set('#snapshot',older);await wait("typeof window.__releaseShapes==='function'");
  await check('while a newly chosen close is read, neither the board nor the library shows the previous close\'s figures or lines',
   `document.querySelector('#snapshot').value===${JSON.stringify(older)}&&document.querySelector('#market-board-status').textContent!==${JSON.stringify(shownAt)}&&[...document.querySelectorAll('.market-card')].every(c=>['unread','rates'].includes(c.dataset.state))&&${nothingShown}`,
   "[document.querySelector('#market-board-status').textContent,[...document.querySelectorAll('.market-card')].map(c=>c.dataset.state)]");
  await evaluate('window.__releaseShapes()');await wait(idle);
  await check('the newly chosen close then fills the board and the library from its own read',
   `window.__shapeReads.at(-1).snapshotId===${JSON.stringify(older)}&&document.querySelectorAll('.market-card[data-state=priced]').length>0&&document.querySelectorAll('.curve-button .sparkline').length===document.querySelectorAll('.market-card[data-state=priced]').length&&document.querySelector('#error').hidden`);
  // An unapplied context change puts away what the applied context showed.
  await evaluate("document.querySelector('#date-context-on').value='2020-12-17';document.querySelector('#date-context-on').dispatchEvent(new Event('input',{bubbles:true}))");
  await check('an unapplied context change shows no figures or lines of the applied context, and says to apply it',
   "!document.querySelector('#market-board-empty').hidden&&document.querySelector('#market-board-empty-title').textContent==='Apply the changed context'&&document.querySelector('#market-board-groups').hidden&&!document.querySelector('.market-card')&&!document.querySelector('.curve-button .sparkline')");
  await click('#return-working');
  await check('returning to working data reads the newest close for the board again',
   `document.querySelector('#snapshot').value===${JSON.stringify(newest)}&&window.__shapeReads.at(-1).snapshotId===${JSON.stringify(newest)}&&document.querySelectorAll('.market-card[data-state=priced]').length>0&&document.querySelector('#error').hidden`);
  // A read that fails says so on each card it was for, draws nothing, and is read again by the next load of the board.
  await evaluate('window.__failShapes=true');await set('#snapshot',older);await wait(idle);
  await check('a curve-shapes read that fails says why on each of its cards, with no price or line',
   "(()=>{const cards=[...document.querySelectorAll('.market-card')].filter(c=>c.dataset.state!=='rates');return cards.length>0&&cards.every(c=>c.dataset.state==='failed'&&c.querySelector('.market-card-reason').textContent==='Curve shape unavailable · the curve read failed'&&!c.querySelector('.kpi-figure,.sparkline'))&&!document.querySelector('.curve-button .sparkline');})()");
  const readsBeforeRetry=await evaluate('window.__shapeReads.length');
  await click('#nav-market');
  await check('opening the board again reads the curves whose read failed, and draws them',
   `window.__shapeReads.length===${readsBeforeRetry}+1&&window.__shapeReads.at(-1).shapes&&window.__shapeReads.at(-1).snapshotId===${JSON.stringify(older)}&&!document.querySelector('.market-card[data-state=failed]')&&document.querySelectorAll('.market-card[data-state=priced]').length>0`);
  await evaluate("[...document.querySelectorAll('.market-card-open')].find(b=>b.textContent==='Local power forwards').click()");await wait(idle);
  await check('choosing a card opens its curve as the library does: one evaluate, the curve shown and selected in the library, the board put away',
   "window.__evaluates===1&&document.querySelector('#market-board').hidden&&!document.querySelector('#curve-detail').hidden&&document.querySelector('#curve-title').textContent==='Local power forwards'&&"+
   "document.querySelector('.curve-button[aria-pressed=true] span')?.textContent==='Local power forwards'&&document.querySelector('#curve-chart canvas')&&document.querySelector('#error').hidden");
 }finally{await command('Page.removeScriptToEvaluateOnNewDocument',{identifier});await command('Emulation.clearDeviceMetricsOverride');}
}
