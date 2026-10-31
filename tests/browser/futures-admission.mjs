import {chooseFutures} from './futures-selection.mjs';
import {closeNewTrade,newTrade} from './new-trade.mjs';
import {displayInstant} from '../../web/dist/market-view.js';

export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,url}){
  await command('Page.enable');await command('Runtime.enable');
  await command('Page.navigate',{url});await wait(idle);
  await evaluate(`window.__admissionSend=window.fetch;window.__unknownFirstTrading=true;window.fetch=async(...args)=>{
    const path=String(args[0]);
    if(path==='/api/trading/workspace'&&window.__denyAdmission)return new Response(JSON.stringify({code:'futures_execution_policy'}),{status:422,headers:{'Content-Type':'application/json'}});
    if(path==='/api/futures/book'&&window.__rejectAdmissionBooking){(window.__admissionBookCommands??=[]).push(JSON.parse(args[1].body));return window.__unknownBookingOutcome?new Response('Synthetic unconfirmed response',{status:502}):new Response(JSON.stringify({code:'futures_execution_policy'}),{status:422,headers:{'Content-Type':'application/json'}});}
    const response=await window.__admissionSend(...args);
    if(path!=='/api/trading/workspace'||!response.ok)return response;
    const data=await response.json();if(data.catalog.kind!=='priced_contracts')return new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});window.__sourceFirstTrading=data.catalog.contracts[0].firstTrading;
    if(window.__unknownFirstTrading)for(const contract of data.catalog.contracts){contract.firstTrading=null;contract.firstTradingDate=null;contract.eligibleFrom='2020-12-15T11:00:00Z';contract.eligibilityNote='Sourced contract terms; explicit sample trading eligibility and generated closing marks.';}
    return new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});
  };`);
  await persona('trader');
  await evaluate("(()=>{const snapshot=document.querySelector('#snapshot');snapshot.value=[...snapshot.options].at(-1).value;snapshot.dispatchEvent(new Event('change',{bubbles:true}));})()");await wait(idle);await click('#nav-futures');
  async function choose(){await newTrade({click,wait},'futures');await chooseFutures({evaluate,set,click});}
  await choose();
  await check('unknown actual first trading remains separate from explicit sample eligibility',"(()=>{const terms=[...document.querySelectorAll('#futures-contract-terms .side-term')];const value=label=>terms.find(row=>row.querySelector('dt').textContent.startsWith(label))?.querySelector('dd').textContent;return value('Actual first trading')==='Source time unavailable'&&value('Trading eligibility from')?.includes('Dec 15, 2020')&&value('Eligibility source')?.includes('sample trading eligibility')&&!document.querySelector('#futures-terms').textContent.includes('1970')&&document.querySelector('#error').hidden;})()");
  // A calendar's choice is an edit, so reading the Blotter again with the form open asks first: the form is closed, and its question answered, before each read.
  await closeNewTrade({click,evaluate,wait});await evaluate('window.__unknownFirstTrading=false');await click('#futures-refresh');await choose();
  const source=await evaluate('window.__sourceFirstTrading');
  await check('a supplied actual first-trading instant is displayed as its own source fact',`(()=>{const rows=[...document.querySelectorAll('#futures-contract-terms .side-term')];return rows.find(row=>row.querySelector('dt').textContent.startsWith('Actual first trading'))?.querySelector('dd').textContent===${JSON.stringify(displayInstant(source,'UTC'))}&&rows.some(row=>row.querySelector('dt').textContent.startsWith('Trading eligibility from'));})()`);
  await set('#futures-lots','1');await set('#futures-execution','2.9');
  await evaluate("for(const id of ['futures-book','futures-legal-entity','futures-counterparty']){const choice=document.getElementById(id);choice.selectedIndex=1;choice.dispatchEvent(new Event('input',{bubbles:true}));}window.__rejectAdmissionBooking=true");
  await submit('#futures-form');
  await check('a definitive admission rejection does not create an uncertain booking retry',"window.__admissionBookCommands.length===1&&!document.querySelector('#error').hidden&&document.querySelector('#error').textContent.includes('Trading eligibility')&&document.querySelector('#futures-pending').hidden");
  await evaluate('window.__unknownBookingOutcome=true');await submit('#futures-form');
  await evaluate('window.__unknownBookingOutcome=false');await click('#futures-retry');
  await check('an admission rejection on retry retains the earlier unconfirmed command',"!document.querySelector('#futures-pending').hidden&&window.__admissionBookCommands.length===3&&JSON.stringify(window.__admissionBookCommands[1])===JSON.stringify(window.__admissionBookCommands[2])");
  await closeNewTrade({click,evaluate,wait});await evaluate('window.__denyAdmission=true');await click('#futures-refresh');
  await check('incomplete execution admission keeps the previous rows only marked stale, and removes tradable values and controls',"!document.querySelector('#error').hidden&&document.querySelector('#error').textContent.includes('Trading eligibility')&&document.querySelector('#futures-page').dataset.pricingState==='failed'&&!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')&&document.querySelector('#desk-positions .standard-data-grid').classList.contains('stale')&&document.querySelector('#blotter-load-text').textContent.includes('Trading eligibility')&&[...document.querySelectorAll('#blotter-strip .strip-item')].find(i=>i.querySelector('.strip-label').textContent==='Futures execution P&L')?.querySelector('.strip-stale')?.textContent==='Stale'&&document.querySelector('#futures-new').disabled&&document.querySelector('#futures-form').hidden&&document.querySelector('#blotter-export').disabled");
  // The intercepted sends never reached the backend. Remove only this fixture's
  // retained command so later browser scenarios start independently.
  await evaluate("(async()=>{const boot=await(await window.__admissionSend('/api/bootstrap')).json(),key='openetrm.futures.request.server_booking_utc_v1.'+[boot.runId,boot.tenantId,boot.workspaceId,boot.profile].join(':'),saved=JSON.parse(sessionStorage.getItem(key)??'null');if(saved?.operationId===window.__admissionBookCommands.at(-1).operationId)sessionStorage.removeItem(key);window.fetch=window.__admissionSend;})()");await persona('viewer');
}
