import {openPhysical} from './physical-workspace.mjs';

// Real delayed answer, explicit Go and Back, followed by a frameless facility question.
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,url,provider,fixture}){
 const control=new URL(provider),agent=fixture.agent;
 if(control.protocol!=='http:'||control.hostname!=='127.0.0.1'||!control.port)throw Error('Use the owned synthetic provider.');
 const script=async responses=>{if(!(await fetch(new URL('/respond',control),{method:'POST',body:JSON.stringify(responses)})).ok)throw Error('Synthetic provider refused its script.');};
 const open=page=>openPhysical({click,evaluate,wait},page);
 await command('Page.enable');await command('Runtime.enable');
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('trader');
 await evaluate(`window.__physicalAsks=[];{const send=window.fetch;window.fetch=async(path,options)=>{if(String(path).endsWith('/assistant/turns/ask'))window.__physicalAsks.push(JSON.parse(options.body));return send(path,options);};}`);
 const frames=await evaluate("(async()=>{const w=await(await fetch('/api/workspace/navigation')).json();return w.frames;})()");
 const before=frames.find(f=>f.at.startsWith('2020-12-18')),after=frames.find(f=>f.at.startsWith('2021-01-15T08:'));
 await set('#snapshot',before.id);await wait(idle);await open('gas-setup');
 await click('#assistant-launcher');await wait("!document.querySelector('#assistant-panel').hidden");
 await wait(`[...document.querySelector('#assistant-agent').options].some(o=>o.value===${JSON.stringify(agent.id+':1')})`);
 await set('#assistant-agent',agent.id+':1');
 await script([{hold:'physical_old_navigation',tools:[['physical_nav','navigate',{page:'gas_setup',target:null}]]},{text:'Synthetic earlier setup answer.'}]);
 await set('#assistant-question','Open this gas setup.');await submit('#assistant-composer');await wait("!document.querySelector('#assistant-consent').hidden");await click('#assistant-allow');
 await wait("document.querySelector('#assistant-send').textContent==='Stop'");
 await set('#snapshot',after.id);await wait(idle);await open('facilities');await set('#facility-ledger',fixture.ledgerId);await wait(idle);
 await fetch(new URL('/release/physical_old_navigation',control),{method:'POST'});
 await wait("document.querySelector('#assistant-turns').textContent.includes('Synthetic earlier setup answer.')&&document.querySelector('#assistant-send').textContent==='Send'");
 await check('an older framed answer does not automatically replace the facility operating context',
  "!document.querySelector('#facilities-page').hidden&&document.querySelector('#date-context-on').value==='2021-01-15'&&[...document.querySelectorAll('.assistant-target button')].some(b=>b.textContent==='Go')");
 await evaluate("[...document.querySelectorAll('.assistant-target button')].find(b=>b.textContent==='Go').click()");
 await wait("!document.querySelector('#gas-setup-page').hidden&&"+idle);
 await check('explicit Go opens the earlier setup context',"document.querySelector('#date-context-on').value==='2020-12-18'&&[...document.querySelectorAll('.assistant-target button')].some(b=>b.textContent==='Back')");
 await evaluate("[...document.querySelectorAll('.assistant-target button')].find(b=>b.textContent==='Back').click()");
 await wait("!document.querySelector('#facilities-page').hidden&&"+idle);
 await check('Back restores the operating date and selected ledger without selecting a pricing close',
  `document.querySelector('#date-context-on').value==='2021-01-15'&&document.querySelector('#snapshot').value===''&&document.querySelector('#facility-ledger').value===${JSON.stringify(fixture.ledgerId)}&&document.querySelector('#error').hidden`);
 await check('Looking at describes the facility date as an askable context',"document.querySelector('#assistant-looking-context').textContent.includes('Operating date 2021-01-15')");
 await script([{tools:[['physical_read','read_workspace_page',{page:'facility_quantities'}]]},{text:'Synthetic facility quantities read at the operating context.'}]);
 await set('#assistant-question','Explain this facility allocation.');await wait("!document.querySelector('#assistant-send').disabled");await submit('#assistant-composer');
 await wait("document.querySelector('#assistant-turns').textContent.includes('Synthetic facility quantities read at the operating context.')&&document.querySelector('#assistant-send').textContent==='Send'");
 await check('facility questions send the explicit date and ledger with no hidden pricing frame',
  `(()=>{const ask=window.__physicalAsks.at(-1);return ask.pageReport.businessDate==='2021-01-15'&&ask.pageReport.selection.ledgerId===${JSON.stringify(fixture.ledgerId)}&&ask.knowledge.mode==='current'&&ask.knowledge.frameId===null;})()`);
 await check('the native facility read finishes without a tool refusal',"!document.querySelector('#assistant-turns').textContent.includes('refused')&&document.querySelector('#error').hidden");
 await shot('physical-assistant-operating-context');
}
