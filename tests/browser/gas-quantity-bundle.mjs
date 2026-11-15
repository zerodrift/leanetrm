import {withOwnedBrowser} from './owned-browser.mjs';
const q=JSON.stringify,panel='[data-lifecycle=panel]';

async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,fixture:f}){
 await command('Page.enable');await command('Runtime.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);await persona('trader');
 await evaluate(`window.calls=[];const send=window.fetch;window.fetch=async(input,init)=>{const r=await send(input,init);if(String(input).startsWith('/api/lifecycle/'))calls.push({path:String(input),body:init?.body?JSON.parse(init.body):null,status:r.status,result:r.headers.get('content-type')?.includes('application/json')?await r.clone().json():null});if(window.loseReply&&String(input)==='/api/lifecycle/execute'&&r.ok){window.loseReply=false;throw new TypeError('Synthetic reply lost after atomic quantity correction');}return r;};`);
 await click('#nav-lifecycle');await set('#close-session','synthetic_mixed_quantity_close');await wait(idle);await set('#snapshot',f.closeId);
 await wait(`document.querySelector('#snapshot').value===${q(f.closeId)}&&${idle}`,60000);
 await set('[data-lifecycle=rows] .table-search','Correct linked title and futures delivery');await wait(idle);
 const row=`[...document.querySelectorAll('[data-lifecycle=rows] .tabulator-row[data-row-key]')].find(r=>{const k=JSON.parse(r.dataset.rowKey);return k[0]==='gas_quantity_bundle'&&k[1]===${q(f.accountId)};})`;
 await wait(`!!(${row})&&${idle}`,60000);await evaluate(`(${row}).click()`);await wait(`!!document.querySelector('${panel} [name=positions]')&&${idle}`,60000);
 await evaluate(`window.pick=(host,name,value)=>{const n=host.querySelector('[name="'+name+'"]');if(!n)throw Error('Missing typed gas field '+name);n.value=value;if(n.value!==value)throw Error('Missing explicit gas choice '+name+' '+value);n.dispatchEvent(new Event('input',{bubbles:true}));n.dispatchEvent(new Event('change',{bubbles:true}));};window.add=(host,family,id)=>{pick(host,family,'entries');const select=host.querySelector('[name="'+family+'"]');select.parentElement.querySelector(':scope > button').click();if(id)pick(host,family+'-member',id);};`);
 const title=f.selection.ordinary.positions[0],physical=f.selection.physical.corrections[0];
 await evaluate(`(()=>{const form=document.querySelector('${panel} form');pick(form,'policyId',${q(f.policyId+':1')});pick(form,'physicalAccountPolicy',${q(f.selection.physicalAccountId+':'+f.selection.physicalPolicyId+':1')});add(form,'positions',${q(title.positionId)});})()`);
 await wait(`document.querySelector('${panel} [name=observedQuantity]')&&${idle}`,60000);
 await evaluate(`add(document.querySelector('${panel} form'),'corrections',${q(physical.performanceId)})`);
 await wait(`document.querySelector('${panel} [name=observedQuantity]')&&document.querySelector('${panel} [name=observedPerformance]')&&${idle}`,60000);
 await check('linked native member forms require explicit quantities and allocation choices',`[...document.querySelectorAll('${panel} [name=quantity],${panel} [name=sourceAllocation]')].every(n=>n.value==='')&&!document.querySelector('${panel} form textarea')`);
 await evaluate(`(()=>{const form=document.querySelector('${panel} form'),title=form.querySelector('[data-gas-entry=positions]'),delivery=form.querySelector('[data-gas-entry=corrections]');
  pick(title,'observedQuantity',${q(title.positionId)});pick(title,'quantity','0.125');pick(title,'retracted','false');pick(title,'evidenceId',${q(title.evidenceId)});pick(title,'sourceAllocation',${q(title.sourceAllocation)});
  pick(form,'consumptions','empty');pick(form,'evidence',${q(f.selection.ordinary.evidence)});
  pick(delivery,'observedPerformance',${q(physical.performanceId)});pick(delivery,'principalVersion',${q(String(physical.expectedPrincipalVersion))});pick(delivery,'quantity','0.25');pick(delivery,'retracted','false');pick(delivery,'titleEvidenceId','@null');pick(delivery,'sourceAllocation',${q(physical.sourceAllocation)});pick(delivery,'evidence',${q(physical.evidence)});
  add(delivery,'supply');const mode=delivery.querySelector('[name=supply]');mode.parentElement.querySelector(':scope > button').click();
  const rows=[...delivery.querySelectorAll('[data-gas-entry=supply]')],supply=${q(physical.supply)};
  for(let i=0;i<rows.length;i++){const row=rows[i],s=supply[i];pick(row,'stockUse',s.consumptionId??'new');pick(row,'lotId',s.lotId);pick(row,'quantity','0.125');pick(row,'retracted','false');pick(row,'evidenceId',s.evidenceId);pick(row,'reservationId','@null');}
 })()`);
 await submit(panel+' form',60000);await wait(`!!document.querySelector('[data-lifecycle-review]')&&${idle}`,60000);
 await check('the actual mixed review shows both revised owners and both source allocations',`(()=>{const review=document.querySelector('[data-lifecycle-review]').textContent,last=calls.filter(c=>c.path==='/api/lifecycle/preview').at(-1);return review.includes('Owned title')&&review.includes('Observed performance')&&review.includes('0.125')&&last.status===200&&last.result.details.sourceAllocations.length===2&&last.result.details.outcome.physical.corrections.length===1&&last.result.details.outcome.ordinary.positions[0].currentRevision===2;})()`,"({error:document.querySelector('#error').textContent,calls})");
 await evaluate("document.querySelector('[data-lifecycle-review]').scrollIntoView({block:'center'})");await shot('lifecycle-gas-bundle-review');await check('the linked quantity review fits the desktop',fitsWidth);
 await evaluate('window.loseReply=true');await click('[data-lifecycle-execute]',60000);
 await check('a lost mixed correction acknowledgement retains the accepted original command',"!document.querySelector('[data-lifecycle=pending]').hidden&&calls.some(c=>c.path==='/api/lifecycle/execute'&&c.status===200&&c.result.changed)");
 await click('[data-lifecycle=retry]',60000);
 await check('retry returns the complete first outcome for both financial revisions',"(()=>{const c=calls.filter(c=>c.path==='/api/lifecycle/execute');return c.length===2&&JSON.stringify(c[0].body)===JSON.stringify(c[1].body)&&JSON.stringify(c[0].result)===JSON.stringify(c[1].result)&&c[1].result.ordinary.positions[0].currentRevision===2&&c[1].result.physical.corrections[0].currentRevision===2&&document.querySelector('[data-lifecycle=pending]').hidden;})()");
}

if(process.argv.length!==4)throw Error('Use the owned mixed quantity browser qualifier.');
console.log(`${await withOwnedBrowser(process.argv[2],process.argv[3],async driver=>{
 try{await run(driver);}catch(error){
  console.error('Owned linked correction state',JSON.stringify(await driver.evaluate("({error:document.querySelector('#error')?.textContent,panel:document.querySelector('[data-lifecycle=panel]')?.textContent,invalid:[...document.querySelectorAll('[data-lifecycle=panel] :invalid')].map(n=>({name:n.name,value:n.value,message:n.validationMessage})),calls:window.calls?.map(c=>({path:c.path,status:c.status,result:c.status!==200?c.result:undefined}))})")));throw error;
 }
})} full-app linked quantity browser checks passed.`);
