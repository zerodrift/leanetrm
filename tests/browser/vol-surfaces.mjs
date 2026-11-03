// Vol surfaces (Markets): the stored volatility surface of one option product at the selected context, drawn as a rich/cheap heat
// map and one smile per expiry that highlight the same series together, and a fit submitted as a risk-queue job that the page
// checks back on until it finishes, and a rotatable 3D view of the fitted surface with each market point on a stalk. Runs after the HTTP checks of the exchange example, which leave the trader's own failed fit of
// nymex_lo at the latest close and fitted surfaces of nymex_on there and of both products at the close that leaves one option
// underlying unsettled. The preview grants the trader PublishMarketData and serves a risk worker for this suite.
import {mkdtemp,writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join} from 'node:path';

const q=JSON.stringify;
const model='smile_quadratic_log_moneyness_v1';
// The close of the first day that pins every input but one option series' settlement and one option underlying's.
const unsettledAt='2026-10-13T17:00:00Z';
const page='#vol-surfaces-page';
const ready=`document.querySelector(${q(page)}).dataset.state`;
// Whether shown text is the exact decimal fraction `value` as volatility points to two decimals: written with two decimals and
// within half a hundredth of a point of the value times one hundred.
const asPoints=(text,value)=>/^[+-]?\d+\.\d{2}$/.test(text)&&Math.abs(Number(text)-Number(value)*100)<=0.005+1e-9;
// The same test in the page, over the readout's value for a term ("40.27 vol pts").
const inPage=`const asPoints=${asPoints.toString()};const term=label=>[...document.querySelectorAll('#vol-readout dt')].find(t=>t.textContent===label)?.nextElementSibling.textContent.replace(/ vol pts$/,'');`;

export async function run({command,evaluate,wait,click,set,persona,check,idle,fitsWidth,url}){
 const artifacts=await mkdtemp(join(tmpdir(),'openetrm-vol-surfaces-'));console.log('VOL_SURFACE_VISUAL_ARTIFACTS='+artifacts);
 const picture=async name=>{const shot=await command('Page.captureScreenshot',{format:'png',captureBeyondViewport:true});await writeFile(join(artifacts,name+'.png'),Buffer.from(shot.data,'base64'));};
 await command('Runtime.enable');await command('Page.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 // Every vol-surface request with its answer and when it was sent and answered; and switches that lose the next fit's response
 // after the backend has answered it, as a dropped connection would, mark the next read's stored revision as having revised
 // inputs, and leave one option product out of the reference catalog's answers.
 const {identifier}=await command('Page.addScriptToEvaluateOnNewDocument',{source:`
  window.surfaceCalls=[];window.reviseNextRead=false;window.loseNextFit=false;window.unlistedProduct=null;const send=window.fetch;
  // The resize observers of the page's views still observing, so a view replaced or reset can be seen to release its own.
  const Observer=window.ResizeObserver;window.volObservers=new Set();
  window.ResizeObserver=class extends Observer{observe(target,options){if(target.closest?.(${q(page)}))window.volObservers.add(this);return super.observe(target,options);}
   disconnect(){window.volObservers.delete(this);return super.disconnect();}};
  window.fetch=async(input,init)=>{
   const path=String(typeof input==='string'?input:input.url),call=/[/](vol-surfaces[/][a-z]+)$/.exec(path);
   if(window.unlistedProduct&&/[/]reference[/]catalog[?]table=futures_option_products/.test(path)){
    const response=await send(input,init),page=await response.json();
    return new Response(JSON.stringify({...page,rows:page.rows.filter(row=>row.code!==window.unlistedProduct)}),{status:response.status,headers:{'Content-Type':'application/json'}});
   }
   if(!call)return send(input,init);
   const record={path:call[1],body:init?.body?JSON.parse(init.body):null,sent:performance.now()};window.surfaceCalls.push(record);
   let response=await send(input,init);
   record.status=response.status;record.answer=await response.clone().json().catch(()=>null);record.answered=performance.now();
   if(call[1]==='vol-surfaces/fit'&&window.loseNextFit){window.loseNextFit=false;throw new TypeError('Failed to fetch');}
   if(call[1]==='vol-surfaces/read'&&response.ok&&window.reviseNextRead&&record.answer.revision){
    window.reviseNextRead=false;const revised={...record.answer,revision:{...record.answer.revision,inputsRevised:true}};
    response=new Response(JSON.stringify(revised),{status:200,headers:{'Content-Type':'application/json'}});
   }
   return response;
  };`});
 const calls=path=>`window.surfaceCalls.filter(c=>c.path===${q(path)})`;
 const lastRead=()=>evaluate(`${calls('vol-surfaces/read')}.at(-1)`);
 // A wait that, should it time out, says what the page showed and what the surface reads answered.
 const settled=(expression,ms)=>wait(expression,ms).catch(async error=>{throw Error(error.message+' · observed '+q(await evaluate(
  `({state:${ready},status:document.querySelector('#vol-fit-status')?.textContent,error:document.querySelector('#vol-surface-error')?.textContent,calls:window.surfaceCalls.slice(-3).map(c=>[c.path,c.status,c.answer?.state??c.answer?.code,c.answer?.job])})`)));});
 // Choosing a product reads its surface afresh; the read answered and drawn is the newest.
 async function choose(product){
  const reads=await evaluate(`${calls('vol-surfaces/read')}.length`);await set('#vol-product',product);
  await settled(`${calls('vol-surfaces/read')}.length>${reads}&&${calls('vol-surfaces/read')}.at(-1).status!==undefined&&${calls('vol-surfaces/read')}.at(-1).body.product===${q(product)}&&${ready}!=='loading'`,30000);
 }
 try{
  await command('Page.navigate',{url:url+'/?example=exchange'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle,60000);
  const listed="(()=>{const n=document.querySelector('#nav-vol-surfaces');return !!n&&!n.hidden&&!!n.closest('#nav-group-markets');})()";
  await persona('admin');
  await check('the page is not listed without Read market data',"document.querySelector('#nav-vol-surfaces')?.hidden===true");
  await persona('viewer');
  await check('the page is not listed for a market reader without Read trades',"document.querySelector('#nav-vol-surfaces')?.hidden===true");

  // A reader who may not fit sees the surface's state and no fit action.
  await persona('risk');
  await check('the page is listed in Markets for a reader of market data and trades',listed);
  await click('#example');await click('#nav-vol-surfaces');
  await settled(`document.querySelector('#vol-product').options.length>1&&${idle}`,30000);
  await check('the page is titled Vol surfaces under Markets, with a product picker that chooses nothing and the read-only model',
   `document.querySelector('#page-title').textContent==='Vol surfaces'&&document.querySelector('#page-section').textContent==='MARKETS'&&
    [...document.querySelector('#vol-product').options].map(o=>o.value).filter(Boolean).sort().join()==='nymex_lo,nymex_on'&&document.querySelector('#vol-product').value===''&&
    document.querySelector('#vol-model').textContent===${q(model)}&&!document.querySelector('#vol-model').matches('input,select')`,
   "[document.querySelector('#page-title').textContent,[...document.querySelector('#vol-product').options].map(o=>o.value),document.querySelector('#vol-model')?.textContent]");
  await check('a short legend says what rich and cheap mean',"/rich/i.test(document.querySelector('#vol-legend').textContent)&&/cheap/i.test(document.querySelector('#vol-legend').textContent)");
  await choose('nymex_lo');
  const unfitted=await lastRead();
  await check('the read names the product, the model and the selected context, and its snapshot is top-level',
   q(unfitted.status===200&&unfitted.body.product==='nymex_lo'&&unfitted.body.model===model&&unfitted.body.context.convention==='futures_option_premium_v1'&&
    unfitted.body.context.knowledgeMode==='current'&&!('snapshotId' in unfitted.body)&&unfitted.answer.state==='notFitted'&&unfitted.answer.canFit===false));
  await check('a reader who may not fit sees Not fitted for this date and no Fit now',
   `document.querySelector('#vol-fit-status').textContent.includes('Not fitted for this date')&&!document.querySelector('#vol-fit')?.offsetParent`);

  // The trader may fit; the trader's own last fit of this key failed, and its code is shown next to the button.
  await persona('trader');await click('#example');await click('#nav-vol-surfaces');
  await settled(`document.querySelector('#vol-product').options.length>1&&${idle}`,30000);
  await choose('nymex_lo');
  const failed=await lastRead();
  await check('a fitter sees Not fitted for this date, Fit now, and the failed job\'s code beside it',
   `${q(failed.answer.canFit===true&&failed.answer.job?.state==='failed')}&&document.querySelector('#vol-fit-status').textContent.includes('Not fitted for this date')&&
    document.querySelector('#vol-fit')?.textContent==='Fit now'&&document.querySelector('#vol-fit-failure').textContent.includes(${q(failed.answer.job?.errorCode??'')})&&
    document.querySelector('#vol-fit-failure').parentElement===document.querySelector('#vol-fit').parentElement`,
   "[document.querySelector('#vol-fit-status')?.textContent,document.querySelector('#vol-fit')?.textContent,document.querySelector('#vol-fit-failure')?.textContent]");

  // Fit now submits a job. Its response is lost after the backend queued it: the request is kept and retried under its own id.
  await evaluate('window.loseNextFit=true');await click('#vol-fit');
  await settled(`${calls('vol-surfaces/fit')}.length===1&&!document.querySelector('#vol-fit-retry').hidden`,30000);
  const lost=await evaluate(`${calls('vol-surfaces/fit')}.at(-1)`);
  await check('Fit now submits the product and model at the selected close under a fresh request id',
   q(Object.keys(lost.body).sort().join()==='model,product,requestId,snapshotId'&&lost.body.product==='nymex_lo'&&lost.body.model===model&&
    lost.body.snapshotId===unfitted.body.context.snapshotId&&/^[0-9a-f-]{36}$/.test(lost.body.requestId)&&lost.status===200&&['queued','running'].includes(lost.answer.state)));
  await check('a lost fit response keeps the request to retry, and offers no fresh fit meanwhile',
   "!!document.querySelector('#vol-fit-retry').offsetParent&&!document.querySelector('#vol-fit').offsetParent&&/not confirmed/.test(document.querySelector('#vol-fit-failure').textContent)",
   "[document.querySelector('#vol-fit-retry').hidden,document.querySelector('#vol-fit').hidden,document.querySelector('#vol-fit-failure').textContent]");
  await click('#vol-fit-retry');
  await settled(`${calls('vol-surfaces/fit')}.length===2&&${calls('vol-surfaces/fit')}.at(-1).status===200`,30000);
  const submitted=await evaluate(`${calls('vol-surfaces/fit')}.at(-1)`);
  await check('the retry sends the original request under its own id and the backend answers its first job',
   q(JSON.stringify(submitted.body)===JSON.stringify(lost.body)&&submitted.answer.jobId===lost.answer.jobId),q([lost.body,submitted.body,lost.answer,submitted.answer]));
  await settled(`/queued|running/i.test(document.querySelector('#vol-fit-status').textContent)&&!document.querySelector('#vol-fit')?.offsetParent`,30000);
  await check('the queued or running job is shown with its state, and nothing offers a second fit meanwhile',
   `/Fit (queued|running)/.test(document.querySelector('#vol-fit-status').textContent)&&!document.querySelector('#vol-fit')?.offsetParent&&document.querySelector('#vol-fit-retry').hidden`,
   "document.querySelector('#vol-fit-status').textContent");
  const sinceRetry=`${calls('vol-surfaces/read')}.filter(c=>c.sent>${q(submitted.sent)})`;
  await settled(`${sinceRetry}.length>=3&&${sinceRetry}.at(-1).answer!==undefined`,15000);
  const polls=await evaluate(sinceRetry);
  // Each read is sent 1.5 s after the previous one was answered: never before it was answered, and never sooner or later than that.
  const spacing=polls.slice(1).map((c,i)=>[c.sent-polls[i].answered,c.sent-polls[i].sent]);
  await check('while the job runs the page reads again 1.5 s after each answer, one read at a time',
   q(polls.slice(0,-1).every(c=>['queued','running'].includes(c.answer.job?.state))&&spacing.every(([afterAnswer,between])=>afterAnswer>=1450&&afterAnswer<=1750&&between<=2000)),q(spacing));
  // Hidden, the page reads nothing; shown again, it reads at once and goes on checking back.
  // The page's clock just before the change, which the page may answer with a read in the same task.
  const visibility=hidden=>evaluate(`(()=>{const at=performance.now();Object.defineProperty(document,'hidden',{configurable:true,get:()=>${hidden}});
   Object.defineProperty(document,'visibilityState',{configurable:true,get:()=>${q(hidden?'hidden':'visible')}});document.dispatchEvent(new Event('visibilitychange'));return at;})()`);
  const hid=await visibility(true);
  await new Promise(resolve=>setTimeout(resolve,4000));
  // A read's send time is recorded as it is sent, so one already under way when the page was hidden was sent before it.
  await check('while the browser hides the page it sends no read',`!${calls('vol-surfaces/read')}.some(c=>c.sent>${hid})`,`${calls('vol-surfaces/read')}.map(c=>c.sent-${hid})`);
  const shownAt=await visibility(false);
  await settled(`${calls('vol-surfaces/read')}.some(c=>c.sent>=${shownAt})`,2000);
  await check('shown again, the page reads at once and checks back on the job',`${calls('vol-surfaces/read')}.find(c=>c.sent>=${shownAt}).sent-${shownAt}<500`,
   `${calls('vol-surfaces/read')}.map(c=>[Math.round(c.sent-${shownAt}),c.answer?.job?.state])`);
  await evaluate("delete document.hidden;delete document.visibilityState");
  // The served worker allows a fit 300 s, which a slow hosted runner can need most of; one page wait is at most 60 s.
  const fitShown=`${ready}==='fitted'&&${calls('vol-surfaces/read')}.at(-1).answer.state==='fitted'`;
  for(let waited=60000;;waited+=60000){try{await settled(fitShown,60000);break;}catch(error){if(waited>=300000)throw error;}}
  await check('the page went on checking back until the job finished and then showed its surface',`${calls('vol-surfaces/read')}.at(-1).answer.job?.state==='completed'&&${calls('vol-surfaces/read')}.at(-1).answer.job.id===${q(submitted.answer.jobId)}`);
  const before=await evaluate(`${calls('vol-surfaces/read')}.length`);
  await new Promise(resolve=>setTimeout(resolve,3500));
  await check('once the job finished the page stops checking back',`${calls('vol-surfaces/read')}.length===${before}`);
  const fitted=(await lastRead()).answer;
  await check('the fitted surface offers Refit and states when it became known',
   `document.querySelector('#vol-fit')?.textContent==='Refit'&&document.querySelector('#vol-fit-status').textContent.startsWith('Fitted')&&!document.querySelector('#vol-fit-failure')?.textContent`);

  // The heat map: one row per expiry and one column per strike of the backend's axis, each split into a call and a put half.
  const chart=selector=>`(await import('/vendor/echarts.js')).getInstanceByDom(document.querySelector(${q(selector)}))`;
  const heat=chart('#vol-heatmap .vol-chart');
  await check('the heat map has one row per expiry and one column per strike axis value',
   `(async()=>{const o=${heat}.getOption(),strikes=o.xAxis.find(a=>a.id==='strikes').data,rows=o.yAxis[0].data;
    return rows.length===${fitted.surface.expiries.length}&&strikes.join()===${q(fitted.surface.strikeAxis.join())}&&o.series.some(s=>s.type==='heatmap');})()`,
   `(async()=>{const o=${heat}.getOption();return [o.yAxis[0].data,o.xAxis.map(a=>a.data)];})()`);
  await check('the call and put halves of each strike are labelled',
   `(async()=>{const halves=${heat}.getOption().xAxis.find(a=>a.id==='halves').data;
    return halves.length===${fitted.surface.strikeAxis.length*2}&&halves.every((h,i)=>h.endsWith(i%2?'|put':'|call'))&&/Call/.test(document.querySelector('#vol-heatmap .vol-halves').textContent)&&/Put/.test(document.querySelector('#vol-heatmap .vol-halves').textContent);})()`);
  // A cell of the heat map, by its series or by its expiry, strike and right, hovered with the mouse; the tooltip and readout it fills.
  async function hover(surface,expiry,strike,right){
   const row=surface.expiries.map(e=>e.expiryAt).indexOf(expiry);
   const point=await evaluate(`(async()=>{const host=document.querySelector('#vol-heatmap .vol-chart');host.scrollIntoView({block:'center'});
    const c=${heat},o=c.getOption(),x=o.xAxis.find(a=>a.id==='halves').data.indexOf(${q(strike)}+'|'+${q(right)});
    const [px,py]=c.convertToPixel({xAxisId:'halves',yAxisIndex:0},[x,${row}]),r=host.getBoundingClientRect();return {x:r.left+px,y:r.top+py,found:x>=0&&${row}>=0};})()`);
   if(!point.found)throw Error('The heat map has no cell for '+[expiry,strike,right].join(' '));
   await command('Input.dispatchMouseEvent',{type:'mouseMoved',x:point.x,y:point.y});
   await wait(`!!document.querySelector('.vol-tooltip')?.offsetParent&&document.querySelector('.vol-tooltip').textContent.includes(${q(strike)})`,5000);
   return evaluate("document.querySelector('.vol-tooltip').textContent");
  }
  const solved=fitted.surface.series.find(s=>s.fitPoint&&s.richCheap!==null);
  const tip=await hover(fitted.surface,solved.series.expiryAt,solved.series.strike,solved.series.right);
  await check('hovering a heat-map cell names its right and strike and shows its rich/cheap as volatility points to two decimals',
   q(tip.includes(solved.series.right==='call'?'Call':'Put')&&tip.includes(solved.series.strike)&&asPoints(/(?:Rich|Cheap) ([+-]?[\d.]+) vol pts/.exec(tip)?.[1],solved.richCheap)),q(tip));
  const expiryIndex=fitted.surface.expiries.findIndex(e=>e.expiryAt===solved.series.expiryAt);
  await check('the hovered series is highlighted in its expiry\'s smile and fills the shared readout',
   `(()=>{${inPage}const smile=document.querySelectorAll('#vol-smiles .vol-smile')[${expiryIndex}];
    return smile.dataset.linked===${q(solved.series.code)}&&term('Series')===${q(solved.series.code)}&&term('Strike')===${q(solved.series.strike)}&&/^-?\\d+\\.\\d{4}$/.test(term('k'))&&
     asPoints(term('Market'),${q(solved.series.marketVolatility)})&&asPoints(term('Fitted'),${q(solved.fitted)})&&asPoints(term('Rich / cheap'),${q(solved.richCheap)});})()`,
   "[document.querySelector('#vol-readout').innerText,[...document.querySelectorAll('#vol-smiles .vol-smile')].map(s=>s.dataset.linked)]");
  await check('each smile header shows its RMS and maximum residual as volatility points',
   `(()=>{${inPage}const expiries=${q(fitted.surface.expiries.map(e=>e.status==='fitted'?[e.rmsResidual,e.maxAbsResidual]:null))};
    const smiles=[...document.querySelectorAll('#vol-smiles .vol-smile')];
    return smiles.length===expiries.length&&smiles.every((s,i)=>{if(!expiries[i])return true;const head=s.querySelector('.vol-smile-head').textContent;
     return asPoints(/RMS ([\\d.]+)/.exec(head)?.[1],expiries[i][0])&&asPoints(/Max ([\\d.]+)/.exec(head)?.[1],expiries[i][1]);});})()`,
   "[...document.querySelectorAll('#vol-smiles .vol-smile-head')].map(h=>h.textContent)");
  await check('each fitted smile draws the fitted line, market points shaped by right and rich/cheap bars',
   `(async()=>{const hosts=[...document.querySelectorAll('#vol-smiles .vol-smile .vol-chart')];if(!hosts.length)return false;const e=await import('/vendor/echarts.js');
    return hosts.every(h=>{const s=e.getInstanceByDom(h).getOption().series;const call=s.find(x=>x.id==='calls'),put=s.find(x=>x.id==='puts');
     return s.some(x=>x.type==='line'&&x.id==='fitted')&&call?.type==='scatter'&&put?.type==='scatter'&&call.symbol!==put.symbol&&s.some(x=>x.type==='bar');});})()`);

  // The 3D view: a canvas the page draws itself, whose pixels are compared at one size and pixel ratio.
  const canvas3d="document.querySelector('#vol-surface-3d canvas')";
  const pixels=()=>evaluate(`(()=>{const c=${canvas3d},d=c.getContext('2d').getImageData(0,0,c.width,c.height).data;let h=2166136261;
   for(let i=0;i<d.length;i++){h^=d[i];h=Math.imul(h,16777619);}return {size:[c.width,c.height,devicePixelRatio],digest:h>>>0};})()`);
  const painted=`(()=>{const c=${canvas3d},d=c.getContext('2d').getImageData(0,0,c.width,c.height).data,colours=new Set();let opaque=0;
   for(let i=0;i<d.length;i+=4)if(d[i+3]>0){opaque++;colours.add(d[i]<<16|d[i+1]<<8|d[i+2]);}return {share:opaque/(d.length/4),colours:colours.size};})()`;
  const scheme=async value=>{await command('Emulation.setEmulatedMedia',{features:[{name:'prefers-color-scheme',value}]});await evaluate('new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))');};
  const capture3d=async name=>{const r=await evaluate("(()=>{const n=document.querySelector('#vol-surface-3d');n.scrollIntoView({block:'center'});const r=n.getBoundingClientRect();return {x:r.left+scrollX,y:r.top+scrollY,width:r.width,height:r.height};})()");
   const shot=await command('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{...r,scale:1}});await writeFile(join(artifacts,name+'.png'),Buffer.from(shot.data,'base64'));};
  await settled(`!!${canvas3d}&&document.fonts.status==='loaded'`,10000);
  await check('the 3D legend names the fitted rows, the straight panels between expiries as a visual join, and the rich and cheap stalks',
   "(t=>/fitted smile/i.test(t)&&/straight panels? between expiries[^.]*visual join/i.test(t)&&/rich/i.test(t)&&/cheap/i.test(t))(document.querySelector('#vol-surface-3d .vol-3d-legend').textContent)",
   "document.querySelector('#vol-surface-3d')?.textContent");
  await check('the 3D mesh has a row for each fitted expiry, joined to its fitted neighbour',
   `(()=>{const c=${canvas3d},fitted=${q(fitted.surface.expiries.filter(e=>e.status==='fitted').map(e=>e.expiryAt))};
    return c.dataset.rows===fitted.join(' ')&&c.dataset.joins.split(' ').filter(Boolean).length===fitted.length-1;})()`,`[${canvas3d}.dataset.rows,${canvas3d}.dataset.joins]`);
  await check('the series hovered in the heat map is highlighted on its stalk in the 3D view',`${canvas3d}.dataset.linked===${q(solved.series.code)}&&/^\\d+(\\.\\d+)?,\\d+(\\.\\d+)?$/.test(${canvas3d}.dataset.linkedAt)`,
   `[${canvas3d}.dataset.linked,${canvas3d}.dataset.linkedAt]`);
  await check('the 3D view paints its shaded surface in the light theme',`(p=>p.share>0.1&&p.colours>=50)(${painted})`,painted);
  await capture3d('vol-surface-3d-light');
  // The canvas takes its colours from the theme's tokens each time it draws. An emulated colour scheme does not fire the media query's
  // change event the view redraws on, so Reset view, which draws again, stands in for it.
  await evaluate("document.querySelector('#vol-surface-3d').style.setProperty('--vol-rich','rgb(255, 0, 255)')");
  await scheme('dark');await click('#vol-surface-reset');
  await check('the 3D view paints its shaded surface in the dark theme, in the colours of the theme\'s tokens',
   `(()=>{const p=${painted},c=${canvas3d},d=c.getContext('2d').getImageData(0,0,c.width,c.height).data;let token=false;
    for(let i=0;i<d.length&&!token;i+=4)token=d[i]===255&&d[i+1]===0&&d[i+2]===255&&d[i+3]===255;return p.share>0.1&&p.colours>=50&&token;})()`,painted);
  await evaluate("document.querySelector('#vol-surface-3d').style.removeProperty('--vol-rich')");await click('#vol-surface-reset');
  await capture3d('vol-surface-3d-dark');
  await scheme('light');await click('#vol-surface-reset');
  // Dragging rotates the view and Reset view returns it to the default angle; the arrow keys rotate it when the canvas has the focus.
  const original=await pixels();
  const box=await evaluate(`(()=>{const r=${canvas3d}.getBoundingClientRect();return {x:r.left,y:r.top};})()`);
  const from={x:box.x+6,y:box.y+6};
  await command('Input.dispatchMouseEvent',{type:'mouseMoved',x:from.x,y:from.y});
  await command('Input.dispatchMouseEvent',{type:'mousePressed',x:from.x,y:from.y,button:'left',buttons:1,clickCount:1});
  for(let step=1;step<=8;step++)await command('Input.dispatchMouseEvent',{type:'mouseMoved',x:from.x+step*20,y:from.y+step*5,button:'left',buttons:1});
  await command('Input.dispatchMouseEvent',{type:'mouseReleased',x:from.x+160,y:from.y+40,button:'left',buttons:0,clickCount:1});
  const dragged=await pixels();
  await check('dragging the 3D view rotates it',q(dragged.digest!==original.digest&&q(dragged.size)===q(original.size)),q([original,dragged]));
  await click('#vol-surface-reset');
  const restored=await pixels();
  await check('Reset view redraws the default angle pixel for pixel at the same size and pixel ratio',q(q(restored)===q(original)),q([original,restored]));
  await evaluate(`${canvas3d}.focus()`);
  for(const type of ['keyDown','keyUp'])await command('Input.dispatchKeyEvent',{type,key:'ArrowLeft',code:'ArrowLeft',windowsVirtualKeyCode:37});
  const turned=await pixels();
  await check('an arrow key rotates the focused 3D view',`${q(turned.digest!==original.digest)}&&document.activeElement===${canvas3d}`,q([original,turned]));
  await click('#vol-surface-reset');
  await check('Reset view returns from a keyboard rotation to the default angle',q(q(await pixels())===q(original)));
  // Hovering a stalk links its series: the heat map highlights it, as do its smile and the readout.
  const other=fitted.surface.series.find(s=>s.richCheap!==null&&s.series.code!==solved.series.code&&s.series.expiryAt!==solved.series.expiryAt)??fitted.surface.series.find(s=>s.richCheap!==null&&s.series.code!==solved.series.code);
  await hover(fitted.surface,other.series.expiryAt,other.series.strike,other.series.right);
  const [sx,sy]=(await evaluate(`${canvas3d}.dataset.linkedAt`)).split(',').map(Number);
  await hover(fitted.surface,solved.series.expiryAt,solved.series.strike,solved.series.right);
  const stalk=await evaluate(`(()=>{const c=${canvas3d};c.scrollIntoView({block:'center'});const r=c.getBoundingClientRect();return {x:r.left+${sx},y:r.top+${sy}};})()`);
  await command('Input.dispatchMouseEvent',{type:'mouseMoved',x:stalk.x,y:stalk.y});
  const otherIndex=fitted.surface.expiries.findIndex(e=>e.expiryAt===other.series.expiryAt);
  await settled(`${canvas3d}.dataset.linked===${q(other.series.code)}`,5000);
  await check('hovering a stalk in the 3D view highlights its series in the heat map, its smile and the readout',
   `(async()=>{${inPage}const d=${heat}.getModel().getSeriesByIndex(0).getData(),el=d.getItemGraphicEl(d.indexOfName(${q(other.series.code)}));
    return !!el&&el.currentStates.includes('emphasis')&&term('Series')===${q(other.series.code)}&&document.querySelectorAll('#vol-smiles .vol-smile')[${otherIndex}].dataset.linked===${q(other.series.code)};})()`,
   `(async()=>{const d=${heat}.getModel().getSeriesByIndex(0).getData(),el=d.getItemGraphicEl(d.indexOfName(${q(other.series.code)}));return [el?.currentStates,document.querySelector('#vol-readout').innerText];})()`);
  await picture('vol-surface-fitted');
  // A refit's job is checked on only while its product is shown; the next product's stored revision is flagged as having revised inputs.
  await click('#vol-fit');
  await settled(`/Fit (queued|running)/.test(document.querySelector('#vol-fit-status').textContent)`,30000);
  await check('a later fit is a new request with an id of its own',`${calls('vol-surfaces/fit')}.length===3&&${calls('vol-surfaces/fit')}.at(-1).body.requestId!==${q(submitted.body.requestId)}`);
  await evaluate('window.reviseNextRead=true');await choose('nymex_on');
  const switched=await evaluate('performance.now()');
  await new Promise(resolve=>setTimeout(resolve,3500));
  await check('choosing another product while a refit runs stops checking on the first',`!${calls('vol-surfaces/read')}.some(c=>c.sent>${switched}&&c.body.product==='nymex_lo')`);
  await check('a surface whose inputs were revised after its fit says so and offers Refit',
   "document.querySelector('#vol-fit-status').textContent.includes('Inputs revised after this fit')&&document.querySelector('#vol-fit')?.textContent==='Refit'");
  const on=(await lastRead()).answer;
  // The deep in-the-money series settles at intrinsic: a gap with its reason; a strike an expiry does not list is not a gap.
  const intrinsic=on.surface.series.find(s=>s.series.outcome==='at_intrinsic');
  const listedKeys=new Set(on.surface.series.map(s=>[s.series.expiryAt,s.series.strike,s.series.right].join()));
  const unlisted=on.surface.expiries.flatMap(e=>on.surface.strikeAxis.map(k=>[e.expiryAt,k,'call'])).find(key=>!listedKeys.has(key.join()));
  if(!intrinsic||!unlisted)throw Error('The nymex_on surface lacks its at-intrinsic series or an unlisted strike.');
  const gapTip=await hover(on.surface,intrinsic.series.expiryAt,intrinsic.series.strike,intrinsic.series.right);
  const unlistedTip=await hover(on.surface,...unlisted);
  await check('a gap cell gives its reason and a not-listed cell says it is not listed, drawn differently',
   `${q(/intrinsic/i.test(gapTip)&&/not listed/i.test(unlistedTip)&&!/intrinsic/i.test(unlistedTip))}&&(async()=>{const s=${heat}.getOption().series;
    const gap=s.find(x=>x.id==='gaps'),none=s.find(x=>x.id==='unlisted');return !!gap&&!!none&&gap.itemStyle.color!==none.itemStyle.color;})()`,q([gapTip,unlistedTip]));

  // The close that leaves one underlying unsettled: that expiry of nymex_lo has no fit, and says why in both views.
  const frames=await evaluate("fetch('/examples/exchange/api/workspace/navigation').then(r=>r.json()).then(d=>d.frames)");
  const unsettled=frames.find(f=>new Date(f.at).getTime()===Date.parse(unsettledAt));
  await set('#snapshot',unsettled.id);
  await settled(`${calls('vol-surfaces/read')}.at(-1)?.body.context.snapshotId===${q(unsettled.id)}&&${ready}!=='loading'&&${idle}`,30000);
  await choose('nymex_lo');
  const gapped=(await lastRead()).answer;
  const gap=gapped.surface.expiries.findIndex(e=>e.status!=='fitted');
  if(gap<0)throw Error('The unsettled close fitted every nymex_lo expiry.');
  const reason=gapped.surface.expiries[gap].status;
  const gapSeries=gapped.surface.series.find(s=>s.series.expiryAt===gapped.surface.expiries[gap].expiryAt);
  const rowTip=await hover(gapped.surface,gapSeries.series.expiryAt,gapSeries.series.strike,gapSeries.series.right);
  await check('the expiry without a fit shows its reason in the heat map and in its smile',
   `${q(rowTip.includes(reason))}&&(async()=>{const label=${heat}.getOption().yAxis[0].data[${gap}];const smile=document.querySelectorAll('#vol-smiles .vol-smile')[${gap}];
    return /not fitted/i.test(typeof label==='string'?label:label.value)&&smile.textContent.includes(${q(reason)})&&!smile.querySelector('.vol-chart');})()`,
   `[${q(rowTip)},document.querySelectorAll('#vol-smiles .vol-smile')[${gap}]?.textContent]`);

  await check('the expiry without a fit draws no row of the 3D mesh, and no join reaches across it',
   `(()=>{const c=${canvas3d},expiries=${q(gapped.surface.expiries.map(e=>e.expiryAt))},fitted=${q(gapped.surface.expiries.filter(e=>e.status==='fitted').map(e=>e.expiryAt))};
    const rows=c.dataset.rows.split(' '),joins=c.dataset.joins.split(' ').filter(Boolean).map(j=>j.split('/'));
    return rows.join()===fitted.join()&&!rows.includes(${q(gapped.surface.expiries[gap].expiryAt)})&&joins.every(([a,b])=>expiries.indexOf(b)===expiries.indexOf(a)+1);})()`,
   `[${canvas3d}.dataset.rows,${canvas3d}.dataset.joins]`);
  await check('each redrawn or replaced view has released its resize observer: one is left for each chart shown and one for the 3D view',
   "window.volObservers.size===document.querySelectorAll('#vol-surfaces-page .vol-chart').length+document.querySelectorAll('#vol-surface-3d canvas').length",
   "[window.volObservers.size,document.querySelectorAll('#vol-surfaces-page .vol-chart').length]");
  await picture('vol-surface-gap');
  // Leaving the page through the sidebar, with nothing else changed, releases its views; coming back draws them again.
  await click('#nav-market');
  await check('leaving Vol surfaces for another page releases its views and every resize observer they held',
   "window.volObservers.size===0&&!document.querySelector('#vol-surface-3d canvas')","[window.volObservers.size,!!document.querySelector('#vol-surface-3d canvas')]");
  await click('#nav-vol-surfaces');
  await settled(`${ready}==='fitted'&&!!${canvas3d}&&${idle}`,30000);
  await check('coming back draws the surface again, with one resize observer for each chart shown and one for the 3D view',
   "window.volObservers.size===document.querySelectorAll('#vol-surfaces-page .vol-chart').length+1&&document.querySelectorAll('#vol-surface-3d canvas').length===1",
   "[window.volObservers.size,document.querySelectorAll('#vol-surfaces-page .vol-chart').length]");
  // At phone width nothing scrolls sideways and the views stack.
  await command('Emulation.setDeviceMetricsOverride',{width:375,height:812,deviceScaleFactor:2,mobile:true});
  await wait(idle,10000);await new Promise(resolve=>setTimeout(resolve,500));
  await check('at 375 px the page has no horizontal scroll and its views fit the width',
   `${fitsWidth}&&[...document.querySelectorAll('${page} .vol-chart')].every(c=>c.getBoundingClientRect().right<=document.documentElement.clientWidth+1)`,
   `[document.documentElement.scrollWidth,document.documentElement.clientWidth,[...document.querySelectorAll('${page} *')].filter(n=>n.getBoundingClientRect().right>document.documentElement.clientWidth+1).slice(0,5).map(n=>n.className||n.tagName)]`);
  await picture('vol-surface-phone');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
  // A product the context's reference data does not list is said by name, never swapped for another or for none.
  await evaluate("window.unlistedProduct='nymex_lo'");await click('#nav-vol-surfaces');
  await settled(`${ready}==='error'`,30000);
  await check('a chosen product the context does not list is a stated error naming it',
   "document.querySelector('#vol-surface-error').textContent==='Option product nymex_lo is not in the reference data at this context.'&&document.querySelector('#vol-product').value===''",
   "[document.querySelector('#vol-surface-error').textContent,document.querySelector('#vol-product').value]");
  await evaluate('window.unlistedProduct=null');

  // A historical view reads the stored surface known at its cutoff and offers no fit, which is admitted only in the current workspace.
  const send=(path,body)=>evaluate(`(async()=>{const boot=await(await fetch('/examples/exchange/api/bootstrap?includeFrames=false')).json();
   const r=await fetch('/examples/exchange/api/'+${q(path)},{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},body:${q(JSON.stringify(body))}});return r.status;})()`);
  const historical=await send('market/context',{effectiveOn:'2026-10-16',knownAt:new Date().toISOString(),mode:'at',selection:'latest_correction'});
  if(historical!==200)throw Error('The historical context was refused: '+historical);
  try{
   await command('Page.navigate',{url:url+'/?example=exchange'});await wait("(document.querySelector('#snapshot')?.options.length??0)>1&&"+idle,60000);
   // A historical view the session kept chooses no close; the person chooses one of its date's closes.
   if(!await evaluate("!!document.querySelector('#snapshot').value"))await set('#snapshot',await evaluate("[...document.querySelector('#snapshot').options].find(o=>o.value).value"));
   await wait(idle,30000);
   await click('#nav-vol-surfaces');await settled(`document.querySelector('#vol-product').options.length>1&&${idle}`,30000);
   await choose('nymex_on');
   const retained=await lastRead();
   await check('a historical view reads at its retained context and says to fit in the current workspace, with no fit button',
    `${q(retained.body.context.knowledgeMode==='retained'&&!!retained.body.context.contextId&&retained.answer.canFit===true&&retained.answer.state==='fitted')}&&
     document.querySelector('.vol-fit-note').textContent==='Fit in the current workspace'&&!document.querySelector('#vol-fit').offsetParent`,
    "[document.querySelector('.vol-fit-note')?.textContent,document.querySelector('#vol-fit')?.hidden]");
  }finally{await send('market/current',{});}
 }finally{
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
  await command('Page.removeScriptToEvaluateOnNewDocument',{identifier});
 }
}
