// Synthetic display fixtures verify browsing defaults; financial reads remain API checks.
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,errors,url}){
 await command('Runtime.enable');await command('Page.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1000,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await check('initial market date is the latest offered instant',`(()=>{const s=document.querySelector('#snapshot'),dates=[...s.options].filter(o=>o.value).map(o=>o.textContent);return !!s.value&&dates.length>0&&document.querySelector('#date-context-on').value!=='';})()`);
 await set('#snapshot','');await wait(idle);await click('#profile-open');await submit('#preferences-form');
 await check('an explicit empty market selection is retained when saving display preferences',"document.querySelector('#snapshot').value===''");
 await click('#date-context>summary');await set('#date-context-on','2099-01-01');await wait(idle);
 await check('effective-date edits populate a capped cutoff and remain unapplied',"document.querySelector('#date-context-on').value==='2099-01-01'&&document.querySelector('#date-context-view').value!==''&&document.querySelector('#workspace-mode-label').textContent.includes('Unapplied')&&document.querySelector('#error').hidden");
 await click('#current-view-context');
 await check('returning current retains populated controls and identifies current knowledge',"document.querySelector('#date-context-on').value!==''&&document.querySelector('#date-context-view').value!==''&&document.querySelector('#date-context>summary').textContent.includes('current knowledge')");
 await click('#nav-delivery');
 await evaluate(`window.deliveryOriginal=window.fetch;window.deliveryReads=[];window.fetch=async(...args)=>{
  const path=String(args[0]);
  if(path.endsWith('/power-imports')){
   const source=(id,date,commodity,provider)=>({id,name:'Synthetic '+id,commodityKey:commodity,commodityName:commodity,provider,market:'DA',marketName:'Day ahead',locationKey:id,locationName:id,minutes:60,marketClock:'UTC',priceUnit:'USD / MWh',availableDates:[date],availableThrough:{[date]:date}});
   return new Response(JSON.stringify({knownAt:'2026-10-25T12:00:00Z',sources:[source('older','2026-01-01','Power','PJM'),source('latest','2026-02-01','Gas','Synthetic')],jobs:[],attempts:[]}));
  }
  if(path.endsWith('/power-imports/period')){window.deliveryReads.push(JSON.parse(args[1].body));return new Response(JSON.stringify({priceUnit:'USD / MWh',rows:[{start:'2026-02-01T00:00:00Z',end:'2026-02-01T01:00:00Z',formattedPrice:'12.50',knownAt:'2026-10-25T12:00:00Z'}]}));}
  return window.deliveryOriginal(...args);
 };`);
 await click('#nav-market');await click('#nav-delivery');
 await check('delivery browsing opens the newest source and day without manual filters',"window.deliveryReads.length===1&&window.deliveryReads[0].sourceId==='latest'&&window.deliveryReads[0].fromDate==='2026-02-01'&&window.deliveryReads[0].throughDate==='2026-02-01'&&document.querySelectorAll('#delivery-price-results tbody tr').length===1&&document.querySelector('#date-context').hidden&&document.querySelector('#error').hidden");
 await evaluate("document.querySelector('#delivery-price-form').closest('details').open=true");
 await set('#delivery-price-cutoff','2026-10-25T11:00:00');
 await check('editing a delivery cutoff clears old records until applied',"!document.querySelector('#delivery-price-results tbody tr')&&document.querySelector('#delivery-price-known').textContent.includes('Unapplied')");
 await submit('#delivery-price-form');
 await check('delivery reads use the edited cutoff',"window.deliveryReads.at(-1).knownAt.startsWith('2026-10-25T11:00:00')&&document.querySelectorAll('#delivery-price-results tbody tr').length===1");
 await shot('floating-context-desktop');
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await check('floating context fits the mobile viewport',fitsWidth);
 await shot('floating-context-mobile');
 await command('Emulation.clearDeviceMetricsOverride');
 await evaluate("window.fetch=window.deliveryOriginal");
 await click('#profile-open');await evaluate("document.querySelector('#show-assistant').checked=false");await submit('#preferences-form');await click('#profile-open');
 await check('hiding the Assistant is remembered in profile preferences',"!document.querySelector('#show-assistant').checked&&document.querySelector('#assistant-launcher').hidden&&document.querySelector('#assistant-hide').hidden");
 await evaluate("document.querySelector('#show-assistant').checked=true");await submit('#preferences-form');
 await check('no uncaught browser exceptions',JSON.stringify(errors.length===0));
}
