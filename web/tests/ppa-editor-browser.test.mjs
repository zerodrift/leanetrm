import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage} from './browser-page.mjs';

// Exercise the real PPA controls against a deterministic API double. Financial
// results are returned by the double; the browser only selects and displays them.
async function fixture(work){
 await withBrowserPage('<div id="host"><section id="ppa-page"></section></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupPpa}=await import('/ppa-editor.js');
   const amount=n=>({numerator:String(n),denominator:'1'}),measures={completePv:amount(12),valueExcludingOverdue:amount(12),overdueKnownBalance:amount(0),overdueEstimated:amount(0)};
   const reference={catalogId:'reference',revisionId:'reference-v1'};
   const catalog={knownAt:'2031-11-01T00:00:00Z',definitions:{revisionId:'definitions-v1'},reference,compositionTemplates:[],cadences:[60],
    breakEven:{compositions:[],selectedRole:'fixed_payment',measures:['CompletePv'],quoteRounding:['half_even_v1']},
    books:[{id:'book-a',name:'Book A',portfolio:'Synthetic'},{id:'book-b',name:'Book B',portfolio:'Synthetic'}],legalEntities:[],counterparties:[],
    references:{quantityGroups:{contract:[],energy:[{code:'energy',unit_code:'mwh'}],certificate:[],capacity:[]},currencies:[{code:'usd'}],quantitySpecs:[{code:'energy',unit_code:'mwh'}],units:[{code:'mwh',symbol:'MWh'}],clocks:[],calendars:[],locations:[],priceIndices:[]},
    definitionsMetadata:{generationAssets:[],assetVersions:[],volumeSources:[],priceCurves:[]},touVersions:[],limits:{requestRows:100},
    policies:{pendingActuals:['require_actuals_v1','estimate_until_actual_v1']},
    authoringPolicies:{discounting:[false],quantityStages:[],hourMemberships:[]}};
   const binding={trade_id:'trade-a',revision:1,binding_id:'binding-a',instrument_id:'instrument-a',coverage_from:'2031-11-01T00:00:00Z',coverage_to:'2031-12-01T00:00:00Z',quantity:'1'};
   const head={trade_id:'trade-a',revision:1,book_id:'book-a',action:'booked'};
   const editor={tradeId:'trade-a',knownAt:catalog.knownAt,head,bindings:[binding],instruments:[],history:[head],revisionBindings:[]};
   const preview={...measures,currency:{code:'usd'},scope:{explicitSubset:false},receipt:{selected:'original'},bindings:[{bindingId:'binding-a',measures,sides:[{sideId:'side-a',coefficient:'1',measures:{...measures,periods:[],rows:[]}}]}]};
   window.calls=[];window.release=null;window.hold=null;window.total=64;window.errors=[];
   const api=async(path,body)=>{
    calls.push({path,body:structuredClone(body)});
    const result=path==='ppa/frames'?[{id:'frame-a',name:'Synthetic frame',at:catalog.knownAt}]:path==='ppa/catalog'?catalog:path==='ppa/editor'?editor:
     path==='ppa/directory'?{heads:[head],bindings:[binding],total:window.total}:path==='ppa/preview'?preview:
     path==='ppa/inspect'?{detail:{total:1,offset:body.offset,hasMore:false,rows:[{interval:{start:'2031-11-01T00:00:00Z',end:'2031-11-01T01:00:00Z'},kind:'Payment',quantity:amount(1),amount:amount(999),quantityState:'Projected',economicClass:'Unrealised',coverage:'Discounted',valueAt:'2031-12-10T00:00:00Z'}]}}:
     path==='time/inspect'?{candidates:[{instant:'2031-11-02T05:00:00Z',offset:'-04:00'},{instant:'2031-11-02T06:00:00Z',offset:'-05:00'}]}:{tradeId:'trade-a',revision:2};
    if(window.hold===path){window.hold=null;await new Promise(resolve=>window.release=resolve);}
    return structuredClone(result);
   };
   let busy=false;window.action=Promise.resolve();let ppa;
   const act=work=>{if(busy)return Promise.resolve();return window.action=(async()=>{busy=true;ppa.sync();try{await work();}catch(e){errors.push(e.message);}finally{busy=false;ppa.sync();}})();};
   ppa=setupPpa({api,act,busy:()=>busy,zone:()=> 'America/New_York',actor:()=> 'synthetic-actor',can:()=>true,historical:()=>false});
   window.ppa=ppa;await ppa.load();
   window.set=(id,value,type='change')=>{const n=document.getElementById(id);n.value=value;n.dispatchEvent(new Event(type,{bubbles:true}));};
   window.click=async id=>{document.getElementById(id).click();await window.action;};
   set('ppa-book-filter','book-a');set('ppa-pending-actuals','require_actuals_v1');
   window.inspectTrade=async()=>{await click('ppa-list');document.querySelector('[data-trade=trade-a]').click();await window.action;};
  })()`);
  await work(evaluate);
 });
}

test('PPA book valuation cannot repaint after its pending-actuals selection changes',{skip:!browserAvailable,timeout:30000},async()=>{
 await fixture(async evaluate=>{
  await evaluate("hold='ppa/preview';document.querySelector('#ppa-book-value').click();new Promise(resolve=>{const poll=()=>window.release?resolve():setTimeout(poll,5);poll();})");
  await evaluate("set('ppa-pending-actuals','estimate_until_actual_v1');release();action");
  assert.equal(await evaluate("document.querySelector('#ppa-result').hidden"),true);
  assert.deepEqual(await evaluate('errors'),[]);
 });
});

test('PPA detail selectors discard an earlier in-flight month response',{skip:!browserAvailable,timeout:30000},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{await inspectTrade();await click('ppa-value-trade');set('ppa-detail-binding','binding-a');set('ppa-detail-side','side-a');set('ppa-detail-month','2031-11');hold='ppa/inspect';document.querySelector('#ppa-detail-show').click();await new Promise(resolve=>{const poll=()=>window.release?resolve():setTimeout(poll,5);poll();});})()");
  await evaluate("set('ppa-detail-month','2031-12');release();action");
  assert.equal(await evaluate("document.querySelector('#ppa-detail').children.length"),0);
  assert.equal(await evaluate("document.querySelector('#ppa-detail-evidence').textContent"),'');
 });
});

test('PPA changing the selected book resets both directory page controls',{skip:!browserAvailable,timeout:30000},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{await click('ppa-list');await click('ppa-list-next');set('ppa-book-filter','book-b');})()");
  assert.deepEqual(await evaluate("['ppa-list-prev','ppa-list-next'].map(id=>document.getElementById(id).disabled)"),[true,true]);
 });
});

test('PPA termination offset choices invalidate an already reviewed boundary',{skip:!browserAvailable,timeout:30000},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{await inspectTrade();await click('ppa-terminate-open');set('ppa-termination-at','2031-11-02T01:00','input');set('ppa-termination-reason','Synthetic correction','input');await click('ppa-terminate-review');const offset=document.querySelector('#ppa-termination-at + select');offset.value='2031-11-02T05:00:00Z';offset.dispatchEvent(new Event('change',{bubbles:true}));await click('ppa-terminate-review');document.querySelector('#ppa-termination-confirm').checked=true;document.querySelector('#ppa-termination-confirm').dispatchEvent(new Event('change'));offset.value='2031-11-02T06:00:00Z';offset.dispatchEvent(new Event('change',{bubbles:true}));})()");
  assert.equal(await evaluate("document.querySelector('#ppa-terminate').disabled"),true);
  assert.equal(await evaluate("document.querySelector('#ppa-termination-review').textContent"),'');
 });
});
