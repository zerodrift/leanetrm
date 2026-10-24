export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,url}){
 await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);
 await evaluate(`(()=>{const send=window.fetch;window.__fixings=[];window.fetch=async(input,init)=>{const response=await send(input,init);try{window.__fixings.push({path:typeof input==='string'?input:input.url,body:init?.body?JSON.parse(init.body):null,data:await response.clone().json()});}catch{}return response;};})()`);
 await persona('trader');const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')).id;})()");
 await set('#snapshot',frame);await click('#nav-futures');
 const reference=await evaluate("window.__fixings.filter(r=>r.path==='/api/trading/blotter').at(-1).data.holdings.find(h=>h.instrumentType==='financial_swap'&&h.quantity==='62'&&h.presentValue==='35.9').tradeReference");
 async function reopen(){await set('#futures-holdings .table-search',reference);await click('#futures-holdings .tabulator-row');await wait("document.querySelector('#swap-editor').open");await wait(idle);}
 await reopen();
 await check('booked actual/projected version restores its source and all literal observation boundaries',"document.querySelector('#swap-fixing-mode').value==='actual_projected'&&document.querySelector('#swap-fixing-source').value==='fixture'&&document.querySelector('#swap-fixing-boundaries').value.split('\\n').length===31&&!document.querySelector('#swap-fixing-terms').hidden&&document.querySelector('#swap-fixing').parentElement.hidden");
 await check('primary review identifies actual and projected prices and exposes retained correction lineage',"(()=>{const v=window.__fixings.filter(r=>r.path==='/api/swaps/editor').at(-1).data;return v.valuation.sides[1].payments[0].resets[0].fixingState==='Actual'&&v.valuation.sides[1].payments[0].resets.slice(1).every(r=>r.fixingState==='Projected')&&!document.querySelector('#swap-receipt-panel').hidden&&document.querySelector('#swap-schedules').textContent.includes('fixingLineage');})()");
 const boundaries=await evaluate("document.querySelector('#swap-fixing-boundaries').value");
 await set('#swap-quantity','93');await set('#swap-reason','Synthetic fixing-aware notional amendment');await submit('#swap-form');
 await check('fixing-aware booking preparation retains actual/projected trade cash and exact terms',"!document.querySelector('#swap-confirmation').hidden&&window.__fixings.filter(r=>r.path==='/api/swaps/preview').at(-1).data.valuation.presentValue==='53.85'");
 await set('#swap-fixing-source','');await check('fixing-source edits invalidate the prepared receipt and confirmation',"document.querySelector('#swap-confirmation').hidden&&document.querySelector('#swap-submit').disabled&&document.querySelector('#swap-receipt-panel').hidden");
 await set('#swap-fixing-source','fixture');await click('#swap-fixing-load');
 await check('loading exact observation intervals does not invent fixing times',"document.querySelector('#swap-fixing-boundaries').value.split('\\n').length===31&&document.querySelector('#swap-fixing-boundaries').value.split('\\n').every(row=>row.endsWith(','))");
 await click('#swaps-draft-price');await check('missing observation boundaries fail before a price request',"document.querySelector('#swap-error').textContent.includes('requires observation start')&&document.querySelector('#swap-result').hidden");
 await set('#swap-fixing-boundaries',boundaries);await submit('#swap-form');
 await check('fixed payment panel does not stretch through inapplicable floating observation controls',"document.querySelector('#swap-fixed-observations').getBoundingClientRect().bottom<document.querySelector('#swap-fixing-terms').getBoundingClientRect().bottom");
 await evaluate("document.querySelector('#swap-fixing-terms').scrollIntoView({block:'start'});new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");await shot('swap-fixing-terms');
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await evaluate("document.querySelector('#swap-fixing-terms').scrollIntoView({block:'start'});new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
 await check('fixing terms stay inside the narrow ticket',"(()=>{const r=document.querySelector('#swap-fixing-boundaries').getBoundingClientRect();return r.left>=0&&r.right<=390;})()");await shot('swap-fixing-terms-mobile');
 await evaluate("document.querySelector('#swap-result').scrollIntoView({block:'start'});new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");await shot('swap-actual-review-mobile');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await click('#swap-confirm');await click('#swap-submit');await wait("!document.querySelector('#swap-editor').open");await reopen();
 await check('amending quantity preserves the booked fixing policy and selected revision lineage',"document.querySelector('#swap-revision').value==='3'&&document.querySelector('#swap-quantity').value==='93'&&document.querySelector('#swap-fixing-mode').value==='actual_projected'&&document.querySelector('#swap-pv').textContent.includes('53.850')");
 await set('#swap-revision','2');await wait(idle);await check('prior fixing-aware revisions remain read only with their original notional',"document.querySelector('#swap-fields').disabled&&document.querySelector('#swap-quantity').value==='62'&&document.querySelector('#swap-pv').textContent.includes('35.900')");
 await click('#swap-close');await check('closing clears scoped fixing-source metadata',"document.querySelector('#swap-fixing-source').options.length===0&&document.querySelector('#swap-fixing-boundaries').value===''");
}
