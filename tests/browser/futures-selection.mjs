// Exercise the visible commodity/exchange/product flow before choosing
// the exact pricing binding. A supplied row comes from the authorized catalog.
export async function chooseFutures({evaluate,set,click},contract,prefix='futures-contract',filterDate=false){
 for(const key of ['commodity','exchange','product']){
  const selector=`#${prefix}-${key}`,value=contract?.[key]??await evaluate(`document.querySelector(${JSON.stringify(selector)}).options[1]?.value`);
  if(!value)throw Error('No available futures '+key);await set(selector,value);
 }
 if(filterDate){await click(`#${prefix}-date`);
 const date=contract?.deliveryFrom;
 await evaluate(`(()=>{const panel=document.querySelector(${JSON.stringify('#'+prefix+'-date')}).parentElement;let button;for(let n=0;n<240;n++){button=panel.querySelector(${JSON.stringify(date?'[data-date="'+date+'"]:not(:disabled)':'[data-date]:not(:disabled)')});if(button)break;const next=panel.querySelector('[aria-label="Next month with data"]');if(next.disabled)break;next.click();}if(!button)throw Error('No available delivery date');button.click();})()`);
 }
 const value=contract?JSON.stringify([contract.curve,contract.id]):await evaluate(`document.querySelector(${JSON.stringify('#'+prefix)}).options[1]?.value`);
 if(!value)throw Error('No available pricing binding');await set('#'+prefix,value);
}
