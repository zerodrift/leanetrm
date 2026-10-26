import {sideSession} from './side-session.mjs';

export async function run({command,evaluate,wait,click,persona,set,check,idle,url}){
 // A view of the curve panel, opening the panel first where the page had no room to open it by itself.
 const openView=async view=>{await evaluate("document.querySelector('#custom-curves-panel').hidden&&document.querySelector('#custom-curves-toggle-panel').click()");await click('#custom-curves-panel-tabs-'+view);};
 const actor=await sideSession(url,'editor'),directory=await actor.call('workspace/navigation');
 const frame=directory.frames.find(f=>f.at.startsWith('2020-12-18'));
 const template=(await actor.call('custom-curves')).templates.find(t=>t.code==='ng_prompt').definition;
 async function save(code,name,first,prior){
  await actor.call('profile',{profile:'editor'});const definition=structuredClone(template);definition.code=code;definition.name=name;definition.nodes.find(n=>n.kind==='select').parameters.first=String(first);
  const saved=await actor.call('custom-curves/save',{requestId:crypto.randomUUID(),snapshotId:frame.id,definition,layout:{},...(prior?{id:prior.id,version:prior.version,generation:prior.generation,expectedState:prior.state}:{})});
  await actor.call('profile',{profile:'approver'});return actor.call('custom-curves/publish',{requestId:crypto.randomUUID(),id:saved.id,version:saved.version,generation:saved.generation});
 }
 const a=await save('browser_activation_a','Synthetic effective curve A',1),b=await save('browser_activation_b','Synthetic effective curve B',1);
 async function activate(expected,version){return actor.call('custom-curves/activation',{requestId:crypto.randomUUID(),curveId:a.id,expectedRevision:expected,policy:'complete_effective_timeline_v1',reason:'Synthetic browser activation',segments:version===null?[]:[{effectiveFrom:'2020-12-01',effectiveTo:'2021-01-01',version,outputNode:a.definition.output}]});}
 await command('Runtime.enable');await command('Page.enable');
 await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.activationReads=[];{const fetch=window.fetch;window.fetch=async(path,init)=>{const response=await fetch(path,init);if(response.headers.get('content-type')?.includes('application/json'))window.activationReads.push({path:String(path),body:init?.body,data:await response.clone().json()});if(window.dropActivation&&String(path)==='/api/custom-curves/activation'&&init?.method==='POST'){window.dropActivation=false;throw Error('Synthetic lost activation response');}return response;};}`});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await persona('approver');await click('#example');await click('#nav-custom-curves');await click(`#custom-curves-library [data-recipe="${a.id}|1"]`);await openView('versions');
 await check('Effective versions opens as a mode of the curve panel',"(()=>{const panel=document.querySelector('#custom-curves-panel'),versions=document.querySelector('#curve-activation');return !panel.hidden&&panel.contains(versions)&&!versions.hidden&&versions.localName!=='details'&&document.querySelector('#custom-curves-panel-tabs-versions').getAttribute('aria-selected')==='true'&&document.querySelector('#custom-curves-inspector').hidden&&versions.contains(document.querySelector('#custom-curves-effective'));})()");
 await click('#curve-activation-load');
 await click('#curve-activation-add');await set('.activation-from','2020-12-01');await set('.activation-to','2021-01-01');await set('.activation-version','1');await set('#curve-activation-reason','Synthetic initial timeline');
 await check('effective preview preserves an unpublished timeline by requiring explicit publish or reload',"document.querySelector('#custom-curves-effective').disabled&&document.querySelector('.activation-from').value==='2020-12-01'");
 await click('#curve-activation-publish');await click('#custom-curves-effective');
 await check('published activation resolves the exact version through the existing backend preview',"document.querySelector('#curve-activation-state').textContent.includes('revision 1')&&!document.querySelector('#custom-curves-result').hidden&&window.activationReads.filter(r=>r.path==='/api/custom-curves/effective-preview').at(-1).data.recipe.version===1");
 await click('#follow-live');await wait("document.querySelector('#live-status').dataset.state==='live'&&"+idle);
 const a2=await save('browser_activation_a','Synthetic effective curve A',2,a);await activate(1,2);
 await wait("window.activationReads.filter(r=>r.path==='/api/custom-curves/effective-preview').at(-1)?.data.recipe?.version===2&&document.querySelector('#live-status').dataset.state==='live'&&"+idle);
 await check('Live admits a successor published after this tab loaded into both version controls',`document.querySelector('#custom-curves-library [aria-current=true]')?.dataset.recipe===${JSON.stringify(a2.id+'|2')}&&document.querySelector('.activation-version').value==='2'`);
 await click('#custom-curves-preview');const effectiveReads=await evaluate("window.activationReads.filter(r=>r.path==='/api/custom-curves/effective-preview').length");
 const reads=await evaluate("window.activationReads.filter(r=>r.path==='/api/custom-curves/preview').length");
 await activate(2,1);
 await wait(`window.activationReads.filter(r=>r.path==='/api/custom-curves/preview').length>${reads}&&document.querySelector('#live-status').dataset.state==='live'&&${idle}`);
 await check('ordinary Preview exits effective mode and remains on its exact version during Live',`window.activationReads.filter(r=>r.path==='/api/custom-curves/effective-preview').length===${effectiveReads}&&window.activationReads.filter(r=>r.path==='/api/custom-curves/preview').at(-1).data.version===2`);
 await click('#custom-curves-effective');await activate(3,null);
 await wait("document.querySelector('#live-status').dataset.state==='stale'&&"+idle);
 await check('inactive latest timeline makes an effective live preview visibly stale',"!document.querySelector('#custom-curves-result').hidden&&document.querySelector('#live-status').textContent.includes('last refreshed')&&!document.querySelector('#live-retry').hidden");
 await click('#return-working');await click(`#custom-curves-library [data-recipe="${a.id}|1"]`);await click('#curve-activation-load');
 const origin=await evaluate('performance.timeOrigin');await set('#curve-activation-reason','Synthetic empty timeline reason');await evaluate("document.querySelector('#curve-activation-reason').focus()");
 await command('Input.dispatchKeyEvent',{type:'keyDown',key:'Enter',code:'Enter',windowsVirtualKeyCode:13});await command('Input.dispatchKeyEvent',{type:'keyUp',key:'Enter',code:'Enter',windowsVirtualKeyCode:13});
 await check('an empty activation form cannot implicitly navigate on Enter',`performance.timeOrigin===${origin}`);
 await click('#curve-activation-add');await set('.activation-from','2020-12-01');await set('.activation-to','2021-01-01');await set('.activation-version','1');
 await evaluate('window.dropActivation=true');await click('#curve-activation-publish');
 await check('lost activation confirmation retains the exact retry request',"!document.querySelector('#custom-curves-pending').hidden");
 const b2=await save('browser_activation_b','Synthetic effective curve B',2,b);
 await actor.call('custom-curves/activation',{requestId:crypto.randomUUID(),curveId:b.id,expectedRevision:0,policy:'complete_effective_timeline_v1',reason:'Synthetic late publication',segments:[{effectiveFrom:'2020-12-01',effectiveTo:'2021-01-01',version:b2.version,outputNode:b2.definition.output}]});
 await click(`#custom-curves-library [data-recipe="${b.id}|1"]`);await click('#curve-activation-load');
 await check('loading a timeline admits its newly published version without changing the selected exact recipe',`document.querySelector('.activation-version').value==='2'&&document.querySelector('#custom-curves-library [aria-current=true]')?.dataset.recipe===${JSON.stringify(b.id+'|1')}`);
 await click('#custom-curves-retry');
 await check('retrying another curve’s accepted timeline cannot install it into the selected curve',`document.querySelector('#custom-curves-library [aria-current=true]')?.dataset.recipe===${JSON.stringify(b.id+'|1')}&&document.querySelector('#curve-activation-rows').children.length===0&&document.querySelector('#curve-activation-state').textContent.includes('original curve')`);
 await click('#curve-activation-load');await click('#curve-activation-add');await set('#curve-activation-reason','Synthetic unpublished draft');
 await click('#profile-open');await set('#profile','viewer');await click('#open');
 await check('switching identity asks before an unpublished timeline draft is given up',"!document.querySelector('#custom-curves-panel-prompt').hidden&&document.querySelector('#curve-activation-reason').value==='Synthetic unpublished draft'");
 await click('#custom-curves-panel-prompt button:not(.secondary)',30000);
 await check('identity changes clear previous activation rows, reason and timeline metadata',"document.querySelector('#curve-activation-rows').children.length===0&&document.querySelector('#curve-activation-reason').value===''&&document.querySelector('#curve-activation-state').textContent===''");
 await click('#nav-custom-curves');await click(`#custom-curves-library [data-recipe="${a.id}|1"]`);await click('#curve-activation-load');
 await check('readers inspect retained timelines with mutation controls disabled',"document.querySelectorAll('#curve-activation-rows .activation-from').length===1&&document.querySelector('#curve-activation-publish').disabled&&document.querySelector('#curve-activation-add').disabled");
}
