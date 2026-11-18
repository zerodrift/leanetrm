import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';
const fixture=JSON.parse(readFileSync(new URL('./fixtures/desk.json',import.meta.url),'utf8'));
const holdingRevision=fixture.holdings[0].revision,holdingTrade=fixture.holdings[0].tradeId;

// A position's details over a stub of the pane's parts, with the page's trade actions: what the head offers and what pressing each does.
const page='<meta charset="utf-8"><link rel="stylesheet" href="/app.css"><div id="position-details-head" class="side-panel-head" style="width:WIDTHpx"><div id="head"></div></div><div id="tabs"></div><div id="body"><div id="view"></div></div>';
const setup=`(async()=>{
 const {setupPositionPanel}=await import('/position-panel.js'),{positionActions}=await import('/position-actions.js'),{positionRows}=await import('/desk-model.js'),{setCurveNames}=await import('/market-view.js');
 const fixture=${JSON.stringify(fixture)};setCurveNames(fixture.curveNames);
 window.holding={...fixture.holdings[0],instrumentType:'future',lots:'40',unit:'MMBtu'};window.row=positionRows([fixture.holdings[0]])[0];
 window.state={busy:false,can:{book:true,amend:true,risk:true}};window.ran=[];
 const formats=Object.fromEntries(['money','quantity','price','mark','markMinusPrice'].map(k=>[k,v=>v]));
 window.input=h=>({holding:h,row,formats,color:'#123456',risk:{state:'none'}});
 window.panel=setupPositionPanel({element:part=>document.querySelector('#'+part),form(){},reveal(){},focusForm(){}},{zone:()=> 'UTC',busy:()=>state.busy,canAmend:()=>true,curveListed:()=>true,context:()=> 'same',
  read:async()=>({view:()=>({terms:[],history:[]})}),amendment:{shown:()=>false,open(){},close:async()=>{}},onDenied(){},openCurve(){},
  actions:{list:h=>positionActions(h,state.can),closeOut:h=>ran.push(['closeOut',h.revision]),adjust:(h,direction,quantity)=>{ran.push([direction,h.revision,quantity]);},risk:h=>ran.push(['risk',h.tradeId])}});
 window.tick=()=>new Promise(resolve=>setTimeout(resolve,0));
 panel.show(input(holding),'terms');await tick();
})()`;
const buttons=`[...document.querySelectorAll('.side-panel-actions button')].filter(b=>!b.hidden).map(b=>b.id.replace('side-panel-','')+':'+b.firstChild.textContent)`;

test('a position\'s details offer its trade actions as buttons in order, the unsupported events last and never sent',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page.replace('WIDTH','900'),async evaluate=>{
  await evaluate(setup);
  assert.deepEqual(await evaluate(buttons),['close-out:Close out…','amend:Amend','increase:Buy more…','reduce:Sell…','risk:Risk…','open-curve:Open curve','terminate:Terminate','novate:Novate']);
  assert.deepEqual(await evaluate(`['terminate','novate'].map(id=>(b=>[b.disabled,b.getAttribute('aria-disabled'),b.classList.contains('ticket-soon'),b.querySelector('.ticket-soon-tag').textContent])(document.querySelector('#side-panel-'+id)))`),
   [[true,'true',true,'Coming soon'],[true,'true',true,'Coming soon']]);
  assert.deepEqual(await evaluate(`(document.querySelector('#side-panel-close-out').click(),document.querySelector('#side-panel-risk').click(),document.querySelector('#side-panel-terminate').click(),ran)`),[['closeOut',holdingRevision],['risk',holdingTrade]]);
  assert.equal(await evaluate(`document.querySelector('#side-panel-adjust').checkVisibility()`),false,'no quantity is asked until an adjustment is chosen');
  assert.equal(await evaluate(`document.querySelector('#side-panel-actions-note').checkVisibility()`),false);
 });
});

test('an adjustment asks its quantity in the position\'s unit and sends it as typed, for the position as it was when asked',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page.replace('WIDTH','900'),async evaluate=>{
  await evaluate(setup);
  await evaluate(`document.querySelector('#side-panel-increase').click()`);
  assert.deepEqual(await evaluate(`[document.querySelector('#side-panel-adjust').checkVisibility(),document.querySelector('label[for=side-panel-adjust-quantity]').textContent,document.activeElement.id,document.querySelector('#side-panel-adjust-quantity').required]`),
   [true,'Buy more: quantity in lots','side-panel-adjust-quantity',true]);
  await evaluate(`document.querySelector('#side-panel-reduce').click()`);
  assert.equal(await evaluate(`document.querySelector('label[for=side-panel-adjust-quantity]').textContent`),'Sell: quantity in lots','choosing the other adjustment renames the one row');
  // What the person typed stays while the desk is read again and the head is drawn anew; a newer revision of the row does not change what was asked.
  await evaluate(`(document.querySelector('#side-panel-adjust-quantity').value=' 1e3 ',panel.update(input({...holding,revision:String(Number(holding.revision)+1)})),tick())`);
  assert.deepEqual(await evaluate(`[document.querySelector('#side-panel-adjust').checkVisibility(),document.querySelector('#side-panel-adjust-quantity').value]`),[true,' 1e3 ']);
  await evaluate(`document.querySelector('#side-panel-adjust').requestSubmit()`);
  assert.deepEqual(await evaluate(`ran`),[['reduce',holdingRevision,' 1e3 ']]);
  assert.equal(await evaluate(`document.querySelector('#side-panel-adjust').checkVisibility()`),false);
  // An empty quantity is not sent, and Cancel gives the row up and returns to its button.
  assert.deepEqual(await evaluate(`(document.querySelector('#side-panel-increase').click(),document.querySelector('#side-panel-adjust-submit').click(),[ran.length,document.querySelector('#side-panel-adjust-quantity').validity.valueMissing])`),[1,true]);
  assert.deepEqual(await evaluate(`(document.querySelector('#side-panel-adjust-cancel').click(),[document.querySelector('#side-panel-adjust').checkVisibility(),document.activeElement.id])`),[false,'side-panel-increase']);
 });
});

test('an action the account may not take is disabled with its reason in view, and a quantity being asked is given up',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page.replace('WIDTH','900'),async evaluate=>{
  await evaluate(setup);
  await evaluate(`document.querySelector('#side-panel-increase').click()`);
  const why='Historical view is read-only. Return to working data to book or amend.';
  await evaluate(`(state.can={book:false,amend:true,risk:false,why:{book:${JSON.stringify(why)},risk:'Your role cannot run scenarios.'}},panel.sync())`);
  assert.deepEqual(await evaluate(`['close-out','increase','reduce','risk'].map(id=>(b=>[b.disabled,b.title,b.getAttribute('aria-describedby')])(document.querySelector('#side-panel-'+id)))`),
   [[true,why,'side-panel-actions-note'],[true,why,'side-panel-actions-note'],[true,why,'side-panel-actions-note'],[true,'Your role cannot run scenarios.','side-panel-actions-note']]);
  assert.equal(await evaluate(`(n=>n.checkVisibility()&&n.textContent)(document.querySelector('#side-panel-actions-note'))`),why+' Your role cannot run scenarios.','each reason is said once');
  assert.equal(await evaluate(`document.querySelector('#side-panel-adjust').checkVisibility()`),false);
  assert.equal(await evaluate(`(document.querySelector('#side-panel-close-out').click(),ran.length)`),0);
  await evaluate(`(state.can={book:true,amend:true,risk:true},state.busy=true,panel.sync())`);
  assert.deepEqual(await evaluate(`['close-out','increase','reduce','risk'].map(id=>document.querySelector('#side-panel-'+id).disabled)`),[true,true,true,true],'nothing is offered while the page is busy');
  assert.equal(await evaluate(`document.querySelector('#side-panel-actions-note').checkVisibility()`),false);
 });
});

test('a short future names its adjustments for its side, and another position gives up a quantity being asked',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page.replace('WIDTH','900'),async evaluate=>{
  await evaluate(setup);
  await evaluate(`(document.querySelector('#side-panel-increase').click(),panel.show(input({...holding,tradeId:'7f000000-0000-4000-8000-000000000001',lots:'-15'}),'terms'),tick())`);
  assert.equal(await evaluate(`document.querySelector('#side-panel-adjust').checkVisibility()`),false);
  assert.deepEqual(await evaluate(`['increase','reduce'].map(id=>document.querySelector('#side-panel-'+id).textContent)`),['Sell more…','Buy back…']);
 });
});

for(const width of [320,900])test(`the action buttons wrap inside the details at ${width}px`,{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page.replace('WIDTH',String(width)),async evaluate=>{
  await evaluate(setup);
  await evaluate(`document.querySelector('#side-panel-increase').click()`);
  assert.equal(await evaluate(`(h=>h.scrollWidth<=h.clientWidth+1)(document.querySelector('#position-details-head'))`),true);
  assert.equal(await evaluate(`[...document.querySelectorAll('.side-panel-actions button,#side-panel-adjust>*')].every(n=>{const b=n.getBoundingClientRect(),h=document.querySelector('#position-details-head').getBoundingClientRect();return b.left>=h.left-1&&b.right<=h.right+1;})`),true);
 });
});

test('a refused adjustment keeps its quantity row, with the typed quantity and the reason, for the person to correct',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page.replace('WIDTH','900'),async evaluate=>{
  await evaluate(setup);
  await evaluate(`(document.querySelector('#side-panel-reduce').click(),document.querySelector('#side-panel-adjust-quantity').value='99',document.querySelector('#side-panel-adjust').requestSubmit())`);
  assert.equal(await evaluate(`document.querySelector('#side-panel-adjust').checkVisibility()`),false,'the row closes once the draft is asked for');
  const why='Sell was not drafted for TRD-7. The position holds 40 lots; reducing it by 99 would reverse it.';
  assert.equal(await evaluate(`panel.refuseAdjustment('reduce','99',${JSON.stringify(why)})`),true);
  assert.deepEqual(await evaluate(`[document.querySelector('#side-panel-adjust').checkVisibility(),document.querySelector('#side-panel-adjust-quantity').value,document.querySelector('label[for=side-panel-adjust-quantity]').textContent,
    (n=>n.checkVisibility()&&n.getAttribute('role')+'|'+n.textContent)(document.querySelector('#side-panel-adjust-note')),document.querySelector('#side-panel-adjust-quantity').getAttribute('aria-describedby'),document.activeElement.id]`),
   [true,'99','Sell: quantity in lots','alert|'+why,'side-panel-adjust-note','side-panel-adjust-quantity']);
  // Asking again, or giving the row up, takes the reason away.
  await evaluate(`(document.querySelector('#side-panel-adjust-quantity').value='10',document.querySelector('#side-panel-adjust').requestSubmit())`);
  assert.deepEqual(await evaluate(`[ran.at(-1)[2],document.querySelector('#side-panel-adjust-note').hidden,document.querySelector('#side-panel-adjust-note').textContent]`),['10',true,'']);
  // Where the adjustment is no longer offered the reason is not shown in a row that cannot be used: the page says it instead.
  await evaluate(`(state.can={book:false,amend:true,risk:true,why:{book:'Historical view is read-only.'}},panel.sync())`);
  assert.equal(await evaluate(`panel.refuseAdjustment('reduce','99',${JSON.stringify(why)})`),false);
  assert.equal(await evaluate(`document.querySelector('#side-panel-adjust').checkVisibility()`),false);
 });
});
