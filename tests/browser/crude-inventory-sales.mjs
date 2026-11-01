// Full inventory-to-sale workflow over native operations in the owned crude fixture.
export async function run(actions){
 const {command,evaluate,wait,click,set,submit,check,shot,idle,fitsWidth,url}=actions;
 const visible="document.querySelector('#crude-operations').getClientRects().length>0&&document.querySelector('#crude-operation-kind').getClientRects().length>0";
 // Independent bookings supply explicit purchase/sale terms; all operational actions below use the UI.
 const trades=await evaluate(`(async()=>{
  const token=(await(await fetch('/api/bootstrap')).json()).csrfToken;
  const post=async(path,body)=>{const r=await fetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token},body:JSON.stringify(body)});if(!r.ok)throw Error(await r.text());return r.json();};
  const original=JSON.parse(sessionStorage.getItem('crude-browser-original'));
  const purchase=await post('crude/book',{...original,operationId:crypto.randomUUID(),quantity:'1000',handlingFee:null,handlingFeeDirection:null});
  const sale=await post('crude/book',{...original,operationId:crypto.randomUUID(),direction:'deliver',considerationDirection:'receive',quantity:'600',fixedPrice:'80',handlingFee:null,handlingFeeDirection:null});
  sessionStorage.setItem('inventory-browser-trades',JSON.stringify({purchase:purchase.tradeId,sale:sale.tradeId}));
  return {purchase:purchase.tradeId,sale:sale.tradeId};
 })()`);
 const open=async id=>{
  await command('Page.navigate',{url:url+'/'});await wait("document.querySelector('#snapshot')&&"+idle);
  const january=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2021-01-15T08:')).id;})()");
  await set('#snapshot',january);await wait(idle);await click('#nav-futures');await wait(idle);
  for(let page=0;page<20;page++){
   if(await evaluate(`[...document.querySelectorAll('#crude-operation-facts button')].some(b=>b.dataset.tradeId===${JSON.stringify(id)})`))break;
   await click('#crude-facts-more');await wait(idle);
  }
  await evaluate(`[...document.querySelectorAll('#crude-operation-facts button')].find(b=>b.dataset.tradeId===${JSON.stringify(id)}).click()`);await wait(visible+'&&'+idle);
 };
 const common=async(kind,key,at,profile='physical_crude_receipt_cash_v1')=>{
  await set('#crude-operation-kind',kind);await set('#crude-operation-profile',profile);
  await set('#crude-operation-businessKey',key);await set('#crude-operation-effectiveAt',at);await set('#crude-operation-evidence','Synthetic certified inventory and cash evidence');
 };
 const option=async(field,text)=>set('#crude-operation-'+field,await evaluate(`[...document.querySelector('#crude-operation-${field}').options].find(o=>o.textContent.includes(${JSON.stringify(text)})).value`));
 const measure=(label,value)=>`[...document.querySelectorAll('#crude-operation-ledger section')].find(s=>s.querySelector('h4').textContent==='Certified volume at 60°F')?.querySelector('tbody')&&[...document.querySelectorAll('#crude-operation-ledger section:first-child tbody tr')].some(r=>r.children[0].textContent===${JSON.stringify(label)}&&r.children[1].textContent===${JSON.stringify(value)})`;
 await open(trades.purchase);
 await check('inventory-to-sale operations require an explicit native profile and reservation selection',"document.querySelector('#crude-operation-profile').value===''&&[...document.querySelector('#crude-operation-kind').options].some(o=>o.value==='allocation')");
 await common('receipt','inventory-owned-lot','2021-01-04T12:00:00Z');await set('#crude-operation-quantity','1000');await set('#crude-operation-disposition','owned_inventory');await click('#crude-operation-title');
 await set('#crude-operation-kind','allocation');await set('#crude-operation-kind','receipt');
 await check('changing the physical operation clears its prior title confirmation',"!document.querySelector('#crude-operation-title').checked");
 await click('#crude-operation-title');await set('#crude-operation-quantity','999');
 await check('editing the confirmed physical quantity requires fresh title confirmation',"!document.querySelector('#crude-operation-title').checked");
 await set('#crude-operation-quantity','1000');await click('#crude-operation-title');await submit('#crude-operation-form');await wait(idle);
 await check('accepted owned lot exposes the native available quantity',measure('Available owned inventory','1000')+'&&'+measure('Total owned','1000'),"({error:document.querySelector('#error').textContent,status:document.querySelector('#crude-operations').textContent,visible:document.querySelector('#crude-operations').getClientRects().length})");
 await common('invoice','inventory-purchase-invoice','2021-01-04T12:05:00Z');await option('receipt','inventory-owned-lot');await set('#crude-operation-component','consideration');await set('#crude-operation-precision','2');await set('#crude-operation-rounding','crude_cumulative_receipt_cash_v1');await submit('#crude-operation-form');await wait(idle);await click('#crude-operation-issue');await wait(idle);
 await common('allocation','inventory-sale-reservation','2021-01-04T12:10:00Z');await option('receipt','inventory-owned-lot');await set('#crude-operation-quantity','600');await set('#crude-operation-sale',trades.sale+':1');
 await check('reservation selects an exact sale but does not claim a title transfer',"document.querySelector('#crude-operation-title').parentElement.hidden&&document.querySelector('#crude-operation-allocation').parentElement.hidden&&document.querySelector('#crude-operation-sale').value.endsWith(':1')");
 await evaluate(`{const send=window.fetch;window.fetch=async(...args)=>{const response=await send(...args);if(String(args[0]).endsWith('/crude/operations/allocation')&&response.ok){sessionStorage.setItem('inventory-browser-allocation',JSON.stringify(JSON.parse(args[1].body)));throw new TypeError('Synthetic lost allocation response');}return response;};}`);
 await submit('#crude-operation-form');await wait(idle);
 await check('lost reservation response retains the exact command and disables replacement terms',"!document.querySelector('#futures-pending').hidden&&document.querySelector('#crude-operation-quantity').disabled");
 await open(trades.purchase);await click('#futures-reconcile');await wait(idle);await open(trades.purchase);
 await check('reservation recovery leaves title owned and reserves exactly once',measure('Available owned inventory','400')+'&&'+measure('Total owned','1000')+'&&'+measure('Reserved for onward sale','600'));
 for(const [quantity,key,at] of [['250','inventory-dispatch-first','2021-01-04T13:00:00Z'],['350','inventory-dispatch-last','2021-01-04T14:00:00Z']]){
  await common('dispatch',key,at);await option('receipt','inventory-owned-lot');await option('allocation','inventory-sale-reservation');await set('#crude-operation-quantity',quantity);await click('#crude-operation-title');await submit('#crude-operation-form');await wait(idle);
 }
 await check('partial dispatches exhaust the reservation and leave the unallocated lot owned',measure('Available owned inventory','400')+'&&'+measure('Total owned','400')+'&&'+measure('Reserved for onward sale','0')+'&&'+measure('Dispatched','600'));
 await common('dispatch','inventory-no-more-dispatch','2021-01-04T15:00:00Z');
 await check('fully dispatched reservations are not offered as another dispatch',"document.querySelector('#crude-operation-receipt').options.length===1&&document.querySelector('#crude-operation-allocation').options.length===1");
 await open(trades.sale);
 await check('sale view exposes only implemented sale cash actions and dispatched evidence',"[...document.querySelector('#crude-operation-kind').options].map(o=>o.value).join()===',invoice,payment,payment_reverse,invoice_void'&&document.querySelector('#crude-operation-profile').value===''&&document.querySelector('#crude-operations h3').textContent==='Crude sale delivery and cash'&&document.querySelector('#crude-operation-component').options.length===2");
 await check('sale fulfillment reconciles both certified dispatches against its contract',measure('Delivered against sale','600')+'&&'+measure('Remaining sale contract','0'));
 for(const [source,key,amount] of [['inventory-dispatch-first','inventory-sale-first','20000'],['inventory-dispatch-last','inventory-sale-last','28000']]){
  await common('invoice',key,'2021-01-05T12:00:00Z','physical_crude_sale_cash_v1');await option('dispatch',source);await set('#crude-operation-component','consideration');await set('#crude-operation-precision','2');await set('#crude-operation-rounding','crude_cumulative_receipt_cash_v1');await submit('#crude-operation-form');await wait(idle);
  await check('sale invoice preview uses its booked selling price and cash direction for '+source,`!document.querySelector('#crude-operation-issue').hidden&&document.querySelector('#crude-operations').textContent.includes('receive ${amount} usd')&&document.querySelector('#crude-operation-receipt').parentElement.hidden`,"({error:document.querySelector('#error').textContent,review:document.querySelector('#crude-operations').textContent})");
  if(source==='inventory-dispatch-first'){
   await option('dispatch','inventory-dispatch-last');await check('changing dispatched evidence invalidates the sale invoice review',"document.querySelector('#crude-operation-issue').hidden");await option('dispatch',source);await submit('#crude-operation-form');await wait(idle);
  }
  await click('#crude-operation-issue');await wait(idle);
 }
 for(const [invoice,key,amount]of[['inventory-sale-first','inventory-sale-part-payment','10000'],['inventory-sale-first','inventory-sale-final-payment','10000'],['inventory-sale-last','inventory-sale-other-payment','28000']]){
  await common('payment',key,'2021-01-06T12:00:00Z','physical_crude_sale_cash_v1');await option('invoice',invoice);await set('#crude-operation-amount',amount);await set('#crude-operation-direction','receive');await submit('#crude-operation-form');await wait(idle);
  if(key==='inventory-sale-part-payment')await check('sale partial collection leaves a positive native open receivable',"[...document.querySelectorAll('#crude-operation-ledger section')].find(s=>s.querySelector('h4').textContent==='Invoice balances').textContent.includes('PartPaid')&&[...document.querySelectorAll('#crude-operation-ledger section')].find(s=>s.querySelector('h4').textContent==='Invoice balances').querySelector('tbody tr').children[4].textContent==='10000'");
 }
 await check('sale collections reconcile original, invoiced and paid cash without outstanding balance',"[...document.querySelectorAll('#crude-operation-ledger section')].find(s=>s.querySelector('h4').textContent==='Contract cash · signed amounts').querySelector('tbody tr').textContent==='Consideration48000480004800000'");
 await check('sale operation forms and ledger stay visible and fit the viewport',visible+'&&'+fitsWidth);await shot('physical-crude-inventory-sale');
 await open(trades.purchase);
 await check('sale cash never changes the original purchase invoice or remaining ownership',measure('Total owned','400')+"&&[...document.querySelectorAll('#crude-operation-ledger section')].find(s=>s.querySelector('h4').textContent==='Contract cash · signed amounts').querySelector('tbody tr').textContent==='Consideration-70000-700000-700000'");
}
