import assert from 'node:assert/strict';

const q=JSON.stringify;
const field=name=>'[name="'+name+'"]';
const mark=name=>'[data-mark="'+name+'"]';

async function openDesk(actions){
 await actions.wait("!!document.querySelector('#nav-curve-marks')&&"+actions.idle);
 await actions.click('#nav-curve-marks');
 await actions.wait("document.querySelector('#page-title').textContent==='Volatility & correlation'&&"+actions.idle);
 await actions.evaluate(`window.mark=name=>document.querySelector('[data-mark="'+name+'"]');
  window.cell=(row,column)=>document.querySelector('[data-mark-cell="'+row+':'+column+'"]');
  window.metric=name=>document.querySelector('[data-metric="'+name+'"] strong').textContent;`);
}
async function start(actions){
 await actions.command('Emulation.setDeviceMetricsOverride',{width:1440,height:1000,deviceScaleFactor:1,mobile:false});
 await actions.command('Emulation.setFocusEmulationEnabled',{enabled:true});
 await actions.command('Page.navigate',{url:actions.url});
 await actions.wait("document.querySelector('#profile-open')&&!document.querySelector('#profile-open').disabled&&"+actions.idle);
 await actions.persona('editor');
 await openDesk(actions);
}
async function source(actions){
 await actions.evaluate("mark('clock-panel').open=true");
 const first=await actions.evaluate("document.querySelector('[name=markSource]').options[1].value");
 await actions.set(field('markSource'),first);
 await actions.set(field('markDate'),'2020-12-18');
 await actions.set(field('markAsOf'),'2020-12-18T16:00:00Z');
 await actions.submit(mark('clock-form'));
 await actions.wait("!mark('clock-panel').open&&!mark('selector').hidden&&mark('curves').querySelectorAll('input').length");
}
async function scope(actions,codes,from,to){
 await actions.evaluate("mark('selector').open=true");
 await actions.evaluate(`for(const item of mark('curves').querySelectorAll('input')){
  if(item.checked!==${q(codes)}.includes(item.value))item.click();
 }`);
 await actions.set(field('markFrom'),from);
 if(to)await actions.set(field('markTo'),to);
 await actions.submit(mark('view-form'));
 await actions.wait("!mark('desk').hidden&&!mark('read-view').disabled&&!mark('selector').open");
}
async function paste(actions,text,row=0,column=0){
 assert.equal(await actions.evaluate(`cell(${row},${column}).dataset.editable`),'true',`Editable paste destination ${row},${column}`);
 await actions.evaluate(`cell(${row},${column}).focus();
 {const data=new DataTransfer();data.setData('text/plain',${q(text)});
  cell(${row},${column}).dispatchEvent(new ClipboardEvent('paste',{clipboardData:data,bubbles:true,cancelable:true}));}`);
 assert.equal(await actions.evaluate("mark('paste-dialog').open"),true,'Clipboard gesture opens its addressed preview');
 await actions.wait("mark('paste-status').textContent&&!mark('paste-status').textContent.includes('Checking')");
}
async function apply(actions){
 await actions.wait("!mark('paste-apply').disabled");
 await actions.click(mark('paste-apply'));
 await actions.wait("!mark('paste-dialog').open");
}
async function review(actions,reason){
 await actions.click(mark('review'));
 await actions.set(field('markReason'),reason);
 await actions.submit(mark('reason-form'));
 await actions.wait("mark('publish')&&!mark('publish').disabled");
}
async function publish(actions,reason){
 await review(actions,reason);
 await actions.click(mark('publish'));
 await actions.wait("!mark('receipt').hidden&&mark('message').textContent.includes('Marks refreshed')");
}

export async function gasMark(actions){
 await start(actions);await source(actions);
 await scope(actions,[actions.fixture.curve],'2021-01','2021-01');
 await actions.check('the gas exercise curve has no January volatility', "cell(0,0).dataset.state==='missing'");
 await paste(actions,'0.50');await apply(actions);await publish(actions,'Synthetic gas exercise volatility');
 await actions.check('the editor records and refreshes the January exercise volatility',
  "cell(0,0).textContent==='0.5Published'&&mark('receipt').textContent.includes('Recorded revision 1')");
}

export async function run(actions){
 const {check,wait,evaluate,click,set,submit,command,shot}=actions;
 const curves=Array.from({length:20},(_,i)=>'zz_mark_'+String(i+1).padStart(2,'0'));
 await start(actions);
 await shot('marks-empty');
 await source(actions);await scope(actions,curves,'2025-01','2025-12');
 await check('the 20 by 12 volatility desk scrolls inside the grid and preserves the full curve name',
  "document.querySelectorAll('[data-mark-cell]').length===240&&mark('grid').scrollWidth>mark('grid').clientWidth&&document.documentElement.scrollWidth===innerWidth&&document.querySelector('.mark-grid tbody th').title.includes('SyntheticNorthernInterconnectionPeakDelivery')");
 await paste(actions,'0.5\t0.123456789012\t0\n0.46\t\t0.49\n');
 await check('the backend addresses five values, with blank untouched and zero visible before application',
  "mark('paste-details').querySelectorAll('tbody tr').length===5&&metric('changed')==='0'&&cell(0,2).dataset.state==='missing'");
 await apply(actions);
 for(const [text,row,col] of [['0.3\t0.4\n0.3',0,0],['1\t=1+1',0,0],['0\t0',19,11]]){
  await paste(actions,text,row,col);
  await check('a refused rectangle preserves all five prior draft values',
   "mark('paste-apply').disabled&&metric('changed')==='5'&&cell(0,2).textContent==='0Validated draft'");
  await shot('marks-error');await evaluate("mark('paste-dialog').close()");
 }
 await evaluate("cell(0,0).focus();scrollTo(0,0)");
 await review(actions,'Synthetic January through March volatility');
 await shot('marks-review');await click(mark('publish'));
 await wait("mark('message').textContent.includes('Marks refreshed')");
 await check('published zero and missing remain distinct on refreshed working knowledge',
  "cell(0,2).textContent==='0Published'&&cell(1,1).dataset.state==='missing'");
 await evaluate("cell(0,0).focus();scrollTo(0,0)");
 await wait("!!mark('inspector').querySelector('canvas')");
 await shot('marks-volatility');
 await command('Emulation.setEmulatedMedia',{features:[{name:'prefers-reduced-motion',value:'reduce'}]});
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:false});
 await check('the narrow desk fits at 390px under reduced motion',
  "matchMedia('(prefers-reduced-motion: reduce)').matches&&document.documentElement.scrollWidth===innerWidth&&getComputedStyle(mark('inspector')).display==='none'");
 await evaluate(`(async()=>{
  await document.fonts.ready;
  for(let frame=0;frame<3;frame++){
   await new Promise(requestAnimationFrame);
   const top=scrollY+mark('grid').getBoundingClientRect().top;
   scrollTo({top:top-document.querySelector('.mark-context').offsetHeight-44,behavior:'instant'});
  }
 })()`);
 await check('the narrow preview exposes its row and month headers above the entered values',
  `(()=>{const head=mark('grid').querySelector('thead th'),r=head.getBoundingClientRect();
    return r.top>=document.querySelector('.mark-context').getBoundingClientRect().bottom&&document.elementFromPoint(r.left+12,r.top+12)===head&&cell(0,0).getBoundingClientRect().bottom<innerHeight;
  })()`,"({grid:mark('grid').getBoundingClientRect().top,head:mark('grid').querySelector('thead th').getBoundingClientRect().top,context:document.querySelector('.mark-context').getBoundingClientRect().bottom,cell:cell(0,0).getBoundingClientRect().bottom,scroll:scrollY})");
 await shot('marks-narrow');
 await click(mark('inspect'));await wait("getComputedStyle(mark('inspector')).display==='block'");
 await check('the selected term structure stays inside the narrow viewport',
  "(()=>{const r=mark('inspector').getBoundingClientRect();return r.left>=0&&r.right===innerWidth&&r.top===0&&r.bottom===innerHeight})()");
 await shot('marks-drawer');await click('.mark-drawer-close');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1000,deviceScaleFactor:1,mobile:false});
 await paste(actions,'0.6');await apply(actions);await publish(actions,'Synthetic volatility correction');
 await paste(actions,'0.50');await apply(actions);await review(actions,'Acknowledge the original synthetic mark');
 await check('review names the accepted old revision and the current corrected head',
  "mark('review-details').textContent.includes('Acknowledge revision 1')&&mark('review-details').textContent.includes('Revision 2 · 0.6')");
 await evaluate(`{const send=fetch;window.fetch=async(input,init)=>{
  const response=await send(input,init);
  if(String(input).endsWith('/curve-marks/editor/record')&&response.ok)throw Error('Synthetic lost publication reply');
  return response;
 };}`);
 await click(mark('publish'));await wait("!mark('pending').hidden&&!mark('status').disabled");
 await check('an unknown publication blocks replacement commands',
  "mark('review').disabled&&mark('read-view').disabled&&mark('dismiss').disabled");
 await shot('marks-recovery');
 const savedKey=await evaluate("Object.keys(sessionStorage).find(key=>key.startsWith('curve-mark-publication:')&&!key.endsWith(':read-references'))");
 const original=await evaluate(`sessionStorage.getItem(${q(savedKey)})`);
 await command('Page.reload',{ignoreCache:true});
 await wait("document.querySelector('#profile-label')?.textContent==='Market Data Editor'&&"+actions.idle);
 await openDesk(actions);
 await check('a normal reload restores the original command before status is known',
  "!mark('pending').hidden&&mark('dismiss').disabled&&mark('review').disabled");
 // A new login cannot reattach the expired view. Start a fresh working page in
 // this tab, as on return from sign-in, preserving its actor-keyed transport.
 await command('Network.clearBrowserCookies');
 await command('Page.reload',{ignoreCache:true});
 await wait("document.querySelector('#error')?.textContent.includes('workspace selection expired')");
 await check('an expired view refuses reattachment and retains the identical original command',
  `sessionStorage.getItem(${q(savedKey)})===${q(original)}`);
 await command('Page.navigate',{url:'about:blank'});
 await command('Page.navigate',{url:actions.url});
 await wait("document.querySelector('#profile-label')?.textContent==='Viewer'&&"+actions.idle);
 await openDesk(actions);
 await check('a different actor cannot see the original publication', "mark('pending').hidden");
 await actions.persona('editor');await openDesk(actions);
 await wait("!mark('pending').hidden&&!mark('status').disabled");
 await check('the fresh working page restores the same operation without reviving its prior frame',
  `sessionStorage.getItem(${q(savedKey)})===${q(original)}&&mark('desk').hidden&&mark('scope-known').textContent==='—'`);
 await click(mark('status'));await wait("!mark('receipt').hidden&&mark('pending').hidden");
 await check('the original actor recovers the immutable acknowledgement after losing the reply and session',
  "mark('receipt').textContent.includes('Acknowledged revision 1')&&mark('receipt').textContent.includes('Revision 2 · 0.6')");
 await source(actions);await scope(actions,curves,'2025-01','2025-12');
 await paste(actions,'0.61\t0.52');await apply(actions);await review(actions,'Synthetic two-cell race');
 await evaluate(`{const send=fetch;let compete=true;window.fetch=async(input,init)=>{
  if(compete&&String(input).endsWith('/curve-marks/editor/record')){
   compete=false;const batch=JSON.parse(init.body);
   const other={...batch,operationId:crypto.randomUUID(),reason:'Synthetic concurrent correction',changes:[{...batch.changes[0],value:'0.62'}]};
   const result=await send(input,{...init,body:JSON.stringify(other)});
   if(!result.ok)throw Error('Competing publication was refused');
  }
  return send(input,init);
 };}`);
 await click(mark('publish'));await wait("!mark('publish')&&mark('review-status').textContent");
 await check('one stale predecessor rejects the whole publication and preserves both draft cells',
  "mark('pending').hidden&&cell(0,0).textContent==='0.61Validated draft'&&cell(0,1).textContent==='0.52Validated draft'");
 await evaluate("mark('review-dialog').close();mark('discard').click()");
 await source(actions);await scope(actions,curves,'2025-01','2025-12');
 await check('fresh knowledge contains the competing correction and none of the rejected batch',
  "cell(0,0).textContent==='0.62Published'&&cell(0,1).textContent==='0.123456789012Published'",
  "[cell(0,0).textContent,cell(0,1).textContent,mark('scope-known').textContent,mark('message').textContent]");
 await evaluate("cell(0,0).focus()");
 for(const [key,code,modifiers,next] of [['Tab',9,0,'0:1'],['Tab',9,8,'0:0']]){
  for(const type of ['keyDown','keyUp'])await command('Input.dispatchKeyEvent',{type,key,code:key,windowsVirtualKeyCode:code,modifiers});
  await check('Tab navigation follows the visible cell order',`document.activeElement.dataset.markCell===${q(next)}`);
 }
 await click('[data-mark-kind=correlation]');await scope(actions,curves,'2025-01');
 await check('the complete correlation desk has twenty structural diagonal cells and 190 editable pairs',
  "document.querySelectorAll('.mark-diagonal').length===20&&document.querySelectorAll('[data-mark-cell][data-editable=true]').length===190");
 await click(mark('paste'));await set(field('markPasteMode'),'full_matrix');
 const matrix=Array.from({length:20},(_,r)=>Array.from({length:20},(_,c)=>r===c?'1':r<c&&r<4&&c<6?['0','0.25','-0.5','0.75','1'][(c+r)%5]:''));
 matrix[1][0]='-0.8';
 const tsv=()=>matrix.map(row=>row.join('\t')).join('\n')+'\n';
 await set(field('markPasteText'),tsv());await submit('.mark-paste-form');
 await wait("!mark('paste-apply').disabled||mark('paste-status').textContent.toLowerCase().includes('mirror')");
 await check('conflicting full-matrix mirrors preserve the clean draft',"mark('paste-apply').disabled&&metric('changed')==='0'");
 matrix[1][0]='0.2500';
 matrix[0][0]='0.5';
 await set(field('markPasteText'),tsv());await submit('.mark-paste-form');
 await wait("mark('paste-status').textContent.includes('diagonal')");
 await check('a non-unit diagonal rejects every addressed pair',"mark('paste-apply').disabled&&metric('changed')==='0'");
 matrix[0][0]='';matrix[19][18]='0';
 await set(field('markPasteText'),matrix.slice(1).map(row=>row.join('\t')).join('\n'));await submit('.mark-paste-form');
 await wait("mark('paste-status').textContent.includes('exactly one row')");
 await check('an incomplete square cannot expand the matrix',"mark('paste-apply').disabled&&metric('changed')==='0'");
 await set(field('markPasteText'),tsv());await submit('.mark-paste-form');await apply(actions);
 await publish(actions,'Synthetic pair matrix');
 await evaluate("cell(0,1).focus();scrollTo(0,0)");
 await check('the lower matrix mirrors the normalized upper fact with its exact value and convention',
  "cell(1,0).dataset.editable==='false'&&cell(1,0).textContent==='0.25Published'&&cell(18,19).textContent==='0Published'&&cell(0,1).title.includes('curve_marks_log_return_held_v1')&&document.documentElement.scrollWidth===innerWidth");
 await evaluate(`{const grid=mark('grid'),bounds=grid.getBoundingClientRect(),populated=cell(1,2).getBoundingClientRect();
  grid.scrollTop=0;grid.scrollLeft+=populated.left+populated.width/2-bounds.left-28;}`);
 await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
 await check('the pinned row label covers a populated pair at an intermediate horizontal scroll',
  `(()=>{const grid=mark('grid'),bounds=grid.getBoundingClientRect(),pair=cell(1,2),under=pair.getBoundingClientRect();
   const x=bounds.left+28,y=under.top+under.height/2,header=pair.closest('tr').querySelector('th[scope=row]');
   return grid.scrollLeft>0&&grid.scrollLeft<grid.scrollWidth-grid.clientWidth&&under.left<=x&&x<under.right&&pair.dataset.state==='published'&&
    header.textContent.trim()&&document.elementFromPoint(x,y)?.closest('th')===header&&
    Math.abs(header.getBoundingClientRect().left-bounds.left)<3&&getComputedStyle(header).backgroundColor==='rgb(17, 23, 40)';
  })()`);
 await shot('marks-correlation-populated-scroll');
 await evaluate("mark('grid').scrollLeft=mark('grid').scrollWidth;mark('grid').scrollTop=185");
 await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
 await check('scrolled row headings remain fixed, opaque and on top of the matrix values',
  `(()=>{const grid=mark('grid'),bounds=grid.getBoundingClientRect(),x=bounds.left+28,y=bounds.top+88;
   const hit=document.elementFromPoint(x,y)?.closest('th[scope=row]');
   const expected=[...grid.querySelectorAll('tbody tr')].find(row=>{const r=row.getBoundingClientRect();return r.top<=y&&r.bottom>y;})?.firstElementChild;
   return grid.scrollLeft>0&&grid.scrollTop>0&&!!hit&&hit===expected&&Math.abs(hit.getBoundingClientRect().left-bounds.left)<3&&getComputedStyle(hit).backgroundColor==='rgb(17, 23, 40)';
  })()`);
 await shot('marks-correlation-scrolled');
 await evaluate("mark('grid').scrollLeft=0;mark('grid').scrollTop=0");
 await shot('marks-correlation');
 await paste(actions,'0.33',0,1);await apply(actions);await review(actions,'Synthetic absent publication retry');
 await evaluate(`{const send=fetch;let failRefresh=false;window.markAttempts=[];
  window.fetch=async(input,init)=>{
   const route=String(input);
   if(route.endsWith('/curve-marks/editor/record')){
    markAttempts.push({body:init.body,proof:init.headers['X-OpenETRM-Read-References']});
    if(markAttempts.length===1)throw Error('Synthetic interrupted dispatch');
    failRefresh=true;
   }
   if(failRefresh&&route.endsWith('/date-schedules/context')){failRefresh=false;throw Error('Synthetic refresh failure');}
   return send(input,init);
  };
 }`);
 await click(mark('publish'));await wait("markAttempts.length===1&&!mark('status').disabled");
 await click(mark('status'));await wait("!mark('dismiss').disabled");
 await check('only successful absent status enables dismissal, with replacement publication still blocked',
  "mark('pending-status').textContent.includes('no committed')&&mark('review').disabled");
 await click(mark('retry'));await wait("mark('message').textContent.includes('Mark refresh failed')");
 await check('retry sends the original operation, body and certified proof and keeps the receipt when refresh fails',
  "markAttempts.length===2&&markAttempts[0].proof&&JSON.stringify(markAttempts[0])===JSON.stringify(markAttempts[1])&&mark('pending').hidden&&mark('receipt').textContent.includes(JSON.parse(markAttempts[0].body).operationId)&&mark('receipt').textContent.includes('Recorded revision 2')");
}
