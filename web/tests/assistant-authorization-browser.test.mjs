import test from 'node:test';
import assert from 'node:assert/strict';
import {writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

test('assistant authorization and page prompts require explicit human controls',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<link rel="stylesheet" href="/app.css"><form id="ai-prompt-form"><select id="ai-prompt-page"></select><p id="ai-prompt-version"></p><textarea id="ai-prompt-instructions"></textarea><button id="ai-prompt-save">Save page prompt</button><button id="ai-prompt-retry" type="button">Retry same save</button><button id="ai-prompt-discard" type="button">Discard saved request</button></form><p id="ai-prompt-status"></p>',async(evaluate,command)=>{
    for(let n=0;n<100&&!await evaluate('!!document.querySelector("#ai-prompt-form")');n++)await new Promise(r=>setTimeout(r,20));
    await evaluate(`(async()=>{
      window.calls=[];window.identity='synthetic_user';window.failSave=true;
      window.api=async(path,body)=>{calls.push({path,body:structuredClone(body)});
        if(path==='assistant/actions/review')return {operation:'admin.update_v1',page:'users_access',body:{name:'<img src=x onerror=alert(1)>',enabled:true},parameters:{},context:{mode:'current',cutoff:'2026-01-01T00:00:00Z',frameId:null},status:'awaiting_user_approval'};
        if(path==='assistant/actions/authorize')return {name:'Synthetic updated account'};
        if(path==='settings/ai/page-prompts')return {pages:[{id:'curves',name:'Curves & prices'}],configuration:{maximumLength:6000,prompts:[{page:'curves',version:3,instructions:'Synthetic existing guidance.'}]}};
        if(failSave){failSave=false;throw Object.assign(Error('Synthetic uncertain response'),{code:'response_unknown'});}return {version:4};
      };
      window.actions=(await import('/assistant-actions.js')).setupAssistantActions({api,identity:()=>identity,changed:async()=>{}});
      await actions.review({turnId:'a1000000-0000-0000-0000-000000000001',step:2});
    })()`);
    assert.equal(await evaluate("calls.filter(c=>c.path.endsWith('/authorize')).length"),0,'opening a proposal performs no save');
    assert.equal(await evaluate('document.querySelector("dialog button").disabled'),true);
    assert.equal(await evaluate('document.querySelectorAll("dialog img").length'),0,'request text cannot execute HTML');
    await evaluate('document.querySelector("dialog button").click()');
    assert.equal(await evaluate("calls.filter(c=>c.path.endsWith('/authorize')).length"),0);
    await evaluate('document.querySelector("dialog input").checked=true;document.querySelector("dialog input").dispatchEvent(new Event("change"));document.querySelector("dialog button").click();new Promise(r=>setTimeout(r,20))');
    assert.deepEqual(await evaluate("calls.find(c=>c.path.endsWith('/authorize')).body"),{turnId:'a1000000-0000-0000-0000-000000000001',step:2},'authorization identifies the retained proposal and cannot replace its payload');
    await evaluate(`(async()=>{actions.reset();window.prompts=(await import('/page-prompts.js')).setupPagePrompts({api,identity:()=>identity,busy:()=>false,act:async work=>{try{await work();}catch{}}});await prompts.load();})()`);
    assert.equal(await evaluate("calls.filter(c=>c.path.endsWith('/save')).length"),0);
    await evaluate('document.querySelector("#ai-prompt-instructions").value="Synthetic revised guidance.";document.querySelector("#ai-prompt-instructions").dispatchEvent(new Event("input"));document.querySelector("#ai-prompt-form").requestSubmit();new Promise(r=>setTimeout(r,20))');
    await evaluate('prompts.reset();prompts.load()');
    assert.equal(await evaluate('document.querySelector("#ai-prompt-instructions").value'),'Synthetic revised guidance.','a restored uncertain save shows the exact guidance its retry will send');
    await evaluate('document.querySelector("#ai-prompt-retry").click();new Promise(r=>setTimeout(r,20))');
    const saves=await evaluate("calls.filter(c=>c.path.endsWith('/save')).map(c=>c.body)");
    assert.equal(saves.length,2);assert.deepEqual(saves[0],saves[1]);assert.equal(saves[0].expectedVersion,3);assert.equal(saves[0].instructions,'Synthetic revised guidance.');
    const shot=await command('Page.captureScreenshot',{format:'png'});
    await writeFile(join(tmpdir(),'openetrm-assistant-prompts-qa.png'),Buffer.from(shot.data,'base64'));
  });
});
