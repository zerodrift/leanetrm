import {withOwnedBrowser} from './owned-browser.mjs';
const q=JSON.stringify,panel='[data-lifecycle=panel]';

async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,fixture:f}){
 await command('Page.enable');await command('Runtime.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);await persona('trader');
 await evaluate(`window.calls=[];const send=window.fetch;window.fetch=async(input,init)=>{const r=await send(input,init);if(String(input).startsWith('/api/lifecycle/'))calls.push({path:String(input),body:init?.body?JSON.parse(init.body):null,status:r.status,result:r.headers.get('content-type')?.includes('application/json')?await r.clone().json():null});if(window.loseFeeReply&&String(input)==='/api/lifecycle/execute'&&r.ok){window.loseFeeReply=false;throw new TypeError('Synthetic reply lost after loan fee commit');}return r;};`);
 await click('#nav-lifecycle');await set('#close-session',f.session);await wait(idle);await set('#snapshot',f.closeId);
 await wait(`document.querySelector('#snapshot').value===${q(f.closeId)}&&${idle}`,60000);
 await click('#profile-open');await set('#zone','America/Chicago');await click('#profile-save',30000);
 await set('[data-lifecycle=rows] .table-search','Amend loan fee');await wait(idle);
 const row=`[...document.querySelectorAll('[data-lifecycle=rows] .tabulator-row[data-row-key]')].find(r=>{const k=JSON.parse(r.dataset.rowKey);return k[0]==='gas_loan_fee_amendment'&&k[1]===${q(f.accountId)}&&k[2]===${q(f.loanId)};})`;
 await evaluate(`window.pick=(name,value)=>{const n=document.querySelector('${panel} [name="'+name+'"]');if(!n)throw Error('Missing explicit fee field '+name);let expected=value;if(n.type==='datetime-local'){const normalized=n.cloneNode(false);normalized.value=value;expected=normalized.value;if(!expected)throw Error('Invalid explicit fee time '+name);}n.value=value;if(n.value!==expected)throw Error('Missing fee choice '+name+' '+value);n.dispatchEvent(new Event('input',{bubbles:true}));n.dispatchEvent(new Event('change',{bubbles:true}));};`);
 const amend=async(revision,amount,dueAt,effectiveAt)=>{
  await wait(`!!(${row})&&${idle}`,60000);await evaluate(`(${row}).click()`);await wait(`!!document.querySelector('${panel} [name=feeRevision]')&&${idle}`,60000);
  await check('the native fee revision '+revision+' form leaves its single cash-adjustment rule explicit',`document.querySelector('${panel} [name=implementation]').value===''&&document.querySelector('${panel} [name=dueAt-disposition]').value===''`);
  await evaluate(`pick('policyId',${q(f.policyId+':'+f.policyRevision)});pick('feeRevision',${q(f.loanId+':'+revision)});pick('amount',${q(amount)});pick('dueAt-disposition',${q(dueAt?'value':'none')});${dueAt?`pick('dueAt',${q(dueAt)});`:''}pick('effectiveAt',${q(effectiveAt)});pick('implementation','gross_timed_fixed_fee_adjustments_v1');pick('evidence','Synthetic browser reviewed fixed fee terms');`);
  await submit(panel+' form',60000);await wait(`!!document.querySelector('[data-lifecycle-review]')&&${idle}`,60000);
  await check('the real fee revision '+revision+' review retains original cash, every bank revision and source terms',`(()=>{const c=calls.filter(c=>c.path==='/api/lifecycle/preview').at(-1),d=c?.result.details,t=document.querySelector('[data-lifecycle-review]').textContent;return c?.status===200&&c.body.target.bindingId===${q(f.loanId)}&&d.terms.revision===${revision}&&d.rootFee.id===${q(f.originalFeeId)}&&d.rootFee.amount==='-2'&&d.payments.length===2&&d.currentPaid==='-0.5'&&d.payments.every(p=>typeof p.amount==='string')&&t.includes('Original cash fee')&&t.includes('Recorded bank payment history')&&t.includes('USD')&&t.includes(${q(f.reference)});})()`,"({error:document.querySelector('#error').textContent,calls:calls.map(c=>({path:c.path,status:c.status,code:c.result?.code}))})");
 };
 await amend(4,'-1.125','2020-12-24T06:00:00','2020-12-20T09:52:00');
 await check('an explicit new fee on a zero total adds only its dated debit',"(()=>{const d=calls.filter(c=>c.path==='/api/lifecycle/preview').at(-1).result.details;return d.outcome.cashChanges.length===1&&d.outcome.cashChanges[0].kind==='revised_fee'&&d.outcome.cashChanges[0].amount==='-1.125';})()");
 await click('[data-lifecycle-execute]',60000);await wait(idle);
 await amend(5,'-1.375','2020-12-25T06:00:00','2020-12-20T09:55:00');
 await check('rescheduling a fee displays the separate old-due credit and new-due debit',"(()=>{const d=calls.filter(c=>c.path==='/api/lifecycle/preview').at(-1).result.details;return d.outcome.cashChanges.length===2&&d.outcome.cashChanges[0].kind==='reverse_prior_fee'&&d.outcome.cashChanges[0].amount==='1.125'&&d.outcome.cashChanges[0].dueAt.startsWith('2020-12-24')&&d.outcome.cashChanges[1].kind==='revised_fee'&&d.outcome.cashChanges[1].amount==='-1.375'&&d.outcome.cashChanges[1].dueAt.startsWith('2020-12-25');})()");
 await evaluate("document.querySelector('[data-lifecycle-review]').scrollIntoView({block:'center'})");await shot('lifecycle-loan-fee-review');await check('the complete fee review fits its desktop viewport',fitsWidth);
 await evaluate('window.loseFeeReply=true');await click('[data-lifecycle-execute]',60000);
 await check('a lost fee acknowledgement retains the original reviewed command',"!document.querySelector('[data-lifecycle=pending]').hidden&&calls.filter(c=>c.path==='/api/lifecycle/execute').at(-1).status===200");
 await click('[data-lifecycle=retry]',60000);
 await check('retry returns the exact first fee outcome without another adjustment',"(()=>{const c=calls.filter(c=>c.path==='/api/lifecycle/execute').slice(-2);return c.length===2&&JSON.stringify(c[0].body)===JSON.stringify(c[1].body)&&JSON.stringify(c[0].result)===JSON.stringify(c[1].result)&&c[1].result.currentRevision===6&&document.querySelector('[data-lifecycle=pending]').hidden;})()");
 await amend(6,'0',null,'2020-12-20T09:57:00');
 await check('the explicit zero-fee review retains the bank history and only credits the prior fee',"(()=>{const c=calls.filter(c=>c.path==='/api/lifecycle/preview').at(-1),d=c.result.details,t=document.querySelector('[data-lifecycle-review]').textContent;return c.body.selection.dueAt===null&&d.outcome.currentDueAt===null&&d.outcome.cashChanges.length===1&&d.outcome.cashChanges[0].kind==='reverse_prior_fee'&&d.outcome.cashChanges[0].amount==='1.375'&&t.includes('Revised fee dueNone');})()");
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await evaluate("document.querySelector('[data-lifecycle-review]').scrollIntoView({block:'center'})");await check('the zero-fee review fits a narrow screen',fitsWidth);await shot('lifecycle-loan-fee-mobile');
 await click('[data-lifecycle-execute]',60000);
 await check('zero-fee execution confirms the retained current revision',"(()=>{const c=calls.filter(c=>c.path==='/api/lifecycle/execute').at(-1);return c.status===200&&c.result.currentRevision===7&&c.result.currentAmount==='0'&&c.result.currentDueAt===null&&document.querySelector('[data-lifecycle=pending]').hidden;})()");
 await wait(`!!(${row})&&${idle}`,60000);await evaluate(`(${row}).click()`);await wait(`!!document.querySelector('${panel} [name=feeRevision]')&&${idle}`,60000);
 await check('reopening the native fee form retains the new head and requires a fresh explicit decision',`[...document.querySelector('${panel} [name=feeRevision]').options].some(n=>n.value===${q(f.loanId+':7')})&&document.querySelector('${panel} [name=amount]').value===''&&document.querySelector('${panel} [name=implementation]').value===''`);
}

if(process.argv.length!==4)throw Error('Use the owned loan fee browser qualifier.');
console.log(`${await withOwnedBrowser(process.argv[2],process.argv[3],async driver=>{
 try{await run(driver);}catch(error){console.error('Owned fee review state',JSON.stringify(await driver.evaluate("({error:document.querySelector('#error')?.textContent,panel:document.querySelector('[data-lifecycle=panel]')?.textContent,invalid:[...document.querySelectorAll('[data-lifecycle=panel] :invalid')].map(n=>({name:n.name,value:n.value,message:n.validationMessage})),calls:window.calls?.map(c=>({path:c.path,status:c.status,code:c.result?.code}))})")));throw error;}
})} full-app loan fee browser checks passed.`);
