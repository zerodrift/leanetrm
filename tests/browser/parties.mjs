import {sideSession} from './side-session.mjs';

export async function run({command,wait,click,set,submit,check,evaluate,idle,url}){
 await command('Runtime.enable');await command('Page.enable');await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await click('#profile-open');await set('#profile','editor');await click('#open');await click('#example');
 await click('#profile-open');await set('#zone','UTC');await submit('#preferences-form');
 await click('#nav-parties');
 await check('party editors load versioned legal entities',"!document.querySelector('#party-form').hidden&&document.querySelector('#party-list').innerText.includes('Synthetic entity')");
 await set('#party-code','browser_entity');await set('#party-name','Synthetic browser entity');await set('#party-effective','2020-12-18T12:00');await submit('#party-form');
 await check('legal entity can be created through the shared business operation',"document.querySelector('#party-list').innerText.includes('Synthetic browser entity')&&document.querySelector('#party-saved').textContent.includes('revision 1')");
 await click('#party-list button[data-party-code="browser_entity"]');
 await check('identity code is immutable while name is editable',"document.querySelector('#party-code').disabled&&!document.querySelector('#party-fields').disabled");
 await set('#party-name','Synthetic browser entity revised');await set('#party-effective','2020-12-18T13:00');await submit('#party-form');
 await check('editing appends history and preserves original party name',"document.querySelector('#party-history').innerText.includes('Synthetic browser entity revised')&&[...document.querySelectorAll('#party-history tbody tr')].length===2&&document.querySelector('#party-saved').textContent.includes('revision 2')");
 await set('#party-kind','counterparty');await click('#party-new');await set('#party-code','browser_party');await set('#party-name','Synthetic browser counterparty');await set('#party-effective','2020-12-18T12:00');await submit('#party-form');
 await check('counterparties have the same versioned editor',"document.querySelector('#party-list').innerText.includes('Synthetic browser counterparty')");
 await click('#party-new');await set('#party-code','browser_unicode_party');const unicodeName='𝛼'.repeat(80);await set('#party-name',unicodeName);await set('#party-effective','2020-12-18T12:00');await submit('#party-form');
 await check('an 80-scalar supplementary party name saves and renders without a UTF16 limit',`document.querySelector('#party-name').maxLength===-1&&document.querySelector('#party-list').innerText.includes(${JSON.stringify(unicodeName)})&&document.querySelector('#party-saved').textContent.includes('revision 1')&&document.querySelector('#error').hidden`);
 // The API refuses a body larger than it accepts before any write, so the page keeps no request.
 await evaluate("window.partySaves=[];{const original=window.fetch;window.fetch=async(...args)=>{const response=await original(...args);if(String(args[0]).endsWith('/parties/save'))window.partySaves.push(response.status+' '+(await response.clone().json()).code);return response;};}");
 await click('#party-new');await set('#party-code','browser_oversized_party');await set('#party-name','Synthetic oversized party '+'x'.repeat(32*1024));await set('#party-effective','2020-12-18T12:00');await submit('#party-form');
 await check('a party name past the request size limit is refused before any write, and no request is kept',"window.partySaves.join()==='413 request_too_large'&&!document.querySelector('#error').hidden&&document.querySelector('#party-pending').hidden&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.parties.request.'))");
 await click('#party-refresh');
 await check('the refused party was not saved',"document.querySelector('#error').hidden&&!document.querySelector('#party-list').innerText.includes('browser_oversized_party')");
 await click('#party-new');await set('#party-code','browser_uncertain_party');await set('#party-name','Synthetic uncertain party');await set('#party-effective','2020-12-18T12:00');
 await evaluate("window.partyRequests=[];{const original=window.fetch;let first=true;window.fetch=async(...args)=>{const response=await original(...args);if(String(args[0]).endsWith('/parties/save')){window.partyRequests.push({body:JSON.parse(args[1].body),status:response.status,payload:await response.clone().json()});if(first){first=false;throw Error('Synthetic lost committed party response');}}return response;};}");
 await submit('#party-form');
 const retained=await evaluate("JSON.parse(sessionStorage.getItem(Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.parties.request.'))))");
 const authority=await sideSession(url),editor=(await authority.call('admin')).members.find(m=>m.login==='editor');
 const restricted=await authority.replaceRoles(editor,['trader']);
 try{await click('#party-retry');await check('a denied retry retains the exact uncertain party change',`!document.querySelector('#party-pending').hidden&&JSON.parse(sessionStorage.getItem(Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.parties.request.')))).operationId===${JSON.stringify(retained.operationId)}`);}
 finally{await authority.replaceRoles(restricted,editor.roles);}
 await click('#party-retry');
 await check('restored party authority reconciles one committed version with the original request',"window.partyRequests.length===3&&window.partyRequests.every(r=>JSON.stringify(r.body)===JSON.stringify(window.partyRequests[0].body))&&window.partyRequests.map(r=>r.status).join(',')==='200,403,200'&&document.querySelector('#party-saved').textContent.includes('revision 1')&&document.querySelector('#party-pending').hidden&&document.querySelector('#error').hidden");
 await evaluate("(()=>{const original=window.fetch;let failed=false;window.confirmedPartyWrites=0;window.fetch=async(...args)=>{const path=String(args[0]);if(path.endsWith('/parties/catalog')&&window.confirmedPartyWrites&&!failed){failed=true;return new Response(JSON.stringify({code:'synthetic_refresh'}),{status:503});}const response=await original(...args);if(path.endsWith('/parties/save')&&response.ok)window.confirmedPartyWrites++;return response;};})()");
 await set('#party-name','Synthetic uncertain party revised');await set('#party-effective','2020-12-18T13:00');await submit('#party-form');
 await check('failed party reads after confirmed save remain visible without retaining the write',"!document.querySelector('#error').hidden&&document.querySelector('#party-pending').hidden&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.parties.request.'))");
 await click('#party-refresh');
 await check('party read recovery does not resubmit the confirmed version',"window.confirmedPartyWrites===1&&document.querySelector('#party-list').innerText.includes('Synthetic uncertain party revised')&&document.querySelector('#error').hidden");
 await click('#profile-open');await set('#profile','trader');await click('#open');await click('#example');await click('#nav-parties');
 await check('trader can inspect parties with read-only fields',"!document.querySelector('#party-read-only').hidden&&document.querySelector('#party-fields').disabled&&document.querySelector('#party-new').disabled");
 await click('#profile-open');await set('#profile','viewer');await click('#open');
 await check('identity change clears cached party records and hides unauthorized navigation',"document.querySelector('#nav-parties').hidden&&document.querySelector('#party-list').children.length===0&&document.querySelector('#party-name').value===''&&document.querySelector('#parties-page').hidden");
}
