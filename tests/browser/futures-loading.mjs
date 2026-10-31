import {chooseProduct,newTrade,startNewTrade} from './new-trade.mjs';
export async function run({command,evaluate,wait,click,set,persona,check,idle,url}){
  // A panel left on the choice of a product: asking for one, with neither chosen, and with no form of either in it.
  const choice="(()=>{const panel=document.querySelector('#position-details');return !panel.hidden&&panel.dataset.mode==='new'&&panel.getAttribute('aria-label')==='New trade'&&[...document.querySelectorAll('#new-trade-product button')].every(b=>b.getAttribute('aria-pressed')==='false')&&document.querySelector('#position-details-form').hidden&&document.querySelector('#futures-new-trade').hidden&&document.querySelector('#swap-editing').hidden&&document.querySelector('#position-details-view').textContent.includes('Choose Future or Swap');})()";
  await command('Page.enable');await command('Runtime.enable');await command('Page.navigate',{url});
  await wait(idle+"&&document.querySelectorAll('.curve-button').length>0");
  await evaluate(`(()=>{const send=window.fetch;window.__loadingOriginalFetch=send;window.__loadingReads=[];window.fetch=async(input,init)=>{
    if(String(input)!=='/api/trading/workspace')return send(input,init);
    const body=JSON.parse(init.body);window.__loadingReads.push({body});
    if(body.bookingCatalog==='priced_contracts'&&window.__failFullBooking){window.__failFullBooking=false;return new Response(JSON.stringify({code:'request_failed'}),{status:503,headers:{'Content-Type':'application/json'}});}
    if(body.bookingCatalog==='priced_contracts'&&window.__denyFullBooking){window.__denyFullBooking=false;return new Response(JSON.stringify({code:'access_denied'}),{status:403,headers:{'Content-Type':'application/json'}});}
    const response=await send(input,init),data=await response.clone().json();
    if(body.bookingCatalog==='priced_contracts'&&window.__holdFullBooking){window.__holdFullBooking=false;await new Promise(resolve=>{window.__releaseFullBooking=resolve;});}
    if(body.bookingCatalog==='priced_contracts'&&window.__mismatchFullBooking&&response.ok){window.__mismatchFullBooking=false;data.blotter.context.contextId='00000000-0000-0000-0000-000000000002';return new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});}
    window.__loadingReads.at(-1).data=data;return response;
  };})()`);
  try{
    await persona('trader');
    const selected=await evaluate("[...document.querySelector('#snapshot').options].at(-1).value");
    await set('#snapshot',selected);await click('#nav-futures');
    await wait("!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')&&!document.querySelector('#blotter-export').disabled");
    await check('Blotter reads a directory and usable holdings without pricing booking choices',"(()=>{const read=window.__loadingReads.at(-1);return read.body.bookingCatalog==='directory'&&read.data.catalog.kind==='directory'&&!Object.hasOwn(read.data.catalog,'contracts')&&document.querySelector('#futures-contract').options.length===1&&!document.querySelector('#futures-new').disabled;})()");
    const count=await evaluate('window.__loadingReads.length');
    await startNewTrade({click,wait});
    await check('New trade opens its Future | Swap switch in the panel, with neither chosen, without requesting futures prices',`document.querySelector('#position-details').dataset.mode==='new'&&[...document.querySelectorAll('#new-trade-product button')].every(b=>b.getAttribute('aria-pressed')==='false')&&document.querySelector('#position-details-form').hidden&&window.__loadingReads.length===${count}`);await click('#details-collapse');
    await newTrade({click,wait},'swaps');
    await check('choosing a swap does not load a futures booking catalog',`!document.querySelector('#swap-editing').hidden&&!document.querySelector('#position-details').hidden&&window.__loadingReads.length===${count}`);await click('#details-collapse');
    await newTrade({click,wait},'futures');
    await check('choosing futures reloads catalog and holdings under one fresh matching context',`(()=>{const read=window.__loadingReads.at(-1),c=read.data.catalog,b=read.data.blotter;return window.__loadingReads.length===${count+1}&&read.body.bookingCatalog==='priced_contracts'&&read.body.context.snapshotId===${JSON.stringify(selected)}&&c.kind==='priced_contracts'&&c.contracts.length>0&&c.context.contextId===b.context.contextId&&c.context.knownAt===b.context.knownAt&&c.asOf===b.asOf&&!document.querySelector('#futures-new-trade').hidden&&document.querySelector('#position-details-form').contains(document.querySelector('#futures-new-trade'))&&document.querySelector('#futures-contract').options.length===1&&document.querySelector('#futures-contract-commodity').options.length>1;})()`);
    await click('#details-collapse');await startNewTrade({click,wait});
    await evaluate("window.__holdFullBooking=true;document.querySelector('#new-trade-futures').click()");await wait("typeof window.__releaseFullBooking==='function'");
    await evaluate("(()=>{const snapshot=document.querySelector('#snapshot');snapshot.value=[...snapshot.options].find(option=>option.value&&option.value!==snapshot.value).value;window.__releaseFullBooking();})()");await wait(idle);
    await check('late full catalog cannot open a form for a different selected snapshot, and leaves no panel on a product with nothing in it',"!document.querySelector('#error').hidden&&document.querySelector('#futures-new-trade').hidden&&document.querySelector('#position-details').hidden&&document.querySelector('#desk-positions').children.length===0");
    await set('#snapshot',selected);await wait(idle);
    await click('#futures-refresh');await newTrade({click,wait},'futures');await click('#futures-refresh');
    await check('refresh returns to directory mode and discards prior booking choices, closing the panel and its form',"window.__loadingReads.at(-1).body.bookingCatalog==='directory'&&document.querySelector('#futures-contract').options.length===1&&document.querySelector('#futures-new-trade').hidden&&document.querySelector('#position-details').hidden");
    // A futures catalog that cannot be read leaves the panel on the choice of a product, as a swap's does, and never on a product with nothing in it.
    await evaluate('window.__failFullBooking=true');await startNewTrade({click,wait});await click('#new-trade-futures');
    await check('a futures catalog that cannot be read leaves the panel asking for a product, with the failure said, and never an empty New future',`!document.querySelector('#error').hidden&&${choice}`,"[document.querySelector('#position-details').dataset.mode,document.querySelector('#error').textContent,document.querySelector('#position-details-view').textContent]");
    await chooseProduct({click,wait},'futures');
    await check('and the product can then be chosen again',"document.querySelector('#side-panel-title').textContent==='New future'&&!document.querySelector('#futures-new-trade').hidden&&document.querySelector('#error').hidden");
    await click('#futures-refresh');
    await evaluate('window.__mismatchFullBooking=true');await startNewTrade({click,wait});await click('#new-trade-futures');
    await check('mismatched full booking context clears rows, keeps the form closed and leaves the panel on the choice of a product',`!document.querySelector('#error').hidden&&document.querySelector('#desk-positions').children.length===0&&document.querySelector('#futures-contract').options.length===1&&${choice}`);
    await click('#futures-refresh');await evaluate('window.__denyFullBooking=true');await startNewTrade({click,wait});await click('#new-trade-futures');
    await check('revoked full booking read clears both workspace data and booking controls, and leaves the panel on the choice of a product',`!document.querySelector('#error').hidden&&document.querySelector('#desk-positions').children.length===0&&document.querySelector('#futures-form').hidden&&document.querySelector('#futures-new').disabled&&${choice}`);
  }finally{await evaluate('window.fetch=window.__loadingOriginalFetch');}
}
