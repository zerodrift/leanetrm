import {openPhysical} from './physical-workspace.mjs';
export async function run({command,evaluate,wait,set,click,submit,persona,check,shot,idle,url,fixture}){
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('trader');
 const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2021-01-15T08:')).id;})()");
 await set('#snapshot',frame);await wait(idle);await openPhysical({click,evaluate,wait},'facilities');
 await set('#facility-ledger',fixture.ledgerId);await wait("!document.querySelector('#facility-editor').hidden&&"+idle);
 await evaluate('window.confirm=()=>true');
 await check('a corrected source keeps final acceptance explicit',"document.querySelector('#facility-adopt').disabled&&document.querySelector('#facility-adoption-state').textContent.includes('withdrawn')");
 await set('#facility-effective','2021-01-15T17:00');
 await set('#facility-source-quantity','100');await set('#facility-source-evidence','Synthetic browser flow correction');await submit('#facility-source-form');
 await set('#facility-plan-evidence','Synthetic browser reconciled plan');await submit('#facility-plan-form');
 await evaluate("const field=document.querySelector('[data-facility-allocation]');field.focus();field.select();");await command('Input.insertText',{text:'50'});
 await check('unsaved allocation edits cannot approve a hidden saved quantity',"document.querySelector('#facility-adopt').disabled");
 await click('#facility-refresh');await set('#facility-effective','2021-01-15T17:00');
 await set('#facility-adoption-evidence','Synthetic browser reviewed final allocations');await submit('#facility-adoption-form');
 await check('the reviewed saved plan can be accepted as final inputs',"document.querySelector('#facility-adoption-state').textContent.includes('accepted')&&!document.querySelector('#facility-retract-adoption').disabled&&document.querySelector('#error').hidden");
 await shot('gas-basis-final-acceptance');
 await set('#facility-adoption-evidence','Synthetic browser withdrawal');await click('#facility-retract-adoption');
 await check('withdrawing final inputs preserves the operational allocation plan',"document.querySelector('#facility-adoption-state').textContent.includes('withdrawn')&&document.querySelector('#facility-retract-adoption').disabled&&document.querySelectorAll('[data-facility-allocation]').length===3&&document.querySelector('#error').hidden");
}
