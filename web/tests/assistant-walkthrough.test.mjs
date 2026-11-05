import test from 'node:test';
import assert from 'node:assert/strict';
import {stepLine} from '../dist/assistant-pages.js';
import {streamFrame,applyEvent,newTurnView} from '../dist/assistant-events.js';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

test('a walkthrough fill stays unsaved and Stop cancels pending presentation steps',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<form id="ticket"><input id="lots"><button>Book</button></form><div id="position"></div>',async evaluate=>{
    await evaluate(`(async()=>{const {setupWalkthrough}=await import('/assistant-walkthroughs.js');window.effects=[];window.submitted=0;
      document.querySelector('form').onsubmit=e=>{e.preventDefault();submitted++;};window.verified=0;
      window.runner=setupWalkthrough({identity:()=> 'synthetic',context:()=> 'pin',verify:async()=>{verified++;return true;},
        perform:async(step,signal)=>{if(step.kind==='fill'){document.querySelector('#lots').value=step.value;effects.push('fill');return document.querySelector('#lots');}
          if(step.kind==='scroll_to'){await new Promise(resolve=>window.release=resolve);if(signal.aborted)throw Error('Stopped');effects.push('scroll');return document.querySelector('#position');}
          effects.push(step.kind);return document.querySelector('#position');}});
      window.plan={agentLabel:'Synthetic guide',ordinal:1,plan:{pageReport:{page:'blotter',selection:{}},context:{mode:'current',contextId:null,frameId:null},steps:[
        {kind:'say',page:'blotter',text:'Terms remain unsaved.'},{kind:'fill',page:'blotter',field:'ticket.lots',value:'2'},{kind:'pause',page:'blotter'},{kind:'scroll_to',page:'blotter',widget:'positions'},{kind:'say',page:'blotter',text:'Done.'}]}};
      void runner.start(plan);for(let n=0;n<100&&!document.querySelector('.assistant-walkthrough-continue:not([hidden])');n++)await new Promise(r=>setTimeout(r,10));
    })()`);
    assert.equal(await evaluate("document.querySelector('#lots').value"),'2');assert.equal(await evaluate('submitted'),0);
    assert.match(await evaluate("document.querySelector('.assistant-walkthrough-label').textContent"),/Synthetic guide/);
    assert.match(await evaluate("document.querySelector('.assistant-walkthrough-status').textContent"),/Terms remain unsaved/);
    await evaluate("document.querySelector('.assistant-walkthrough-continue').click()");
    await evaluate("new Promise(resolve=>setTimeout(resolve,20))");
    await evaluate("document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));release();");
    await evaluate("new Promise(resolve=>setTimeout(resolve,20))");
    assert.deepEqual(await evaluate('effects'),['fill']);assert.equal(await evaluate('submitted'),0);
    assert.match(await evaluate("document.querySelector('.assistant-walkthrough-status').textContent"),/Stopped/);
  });
});


test('stored walkthrough events are closed, deduplicated and hidden with their answer',()=>{
  const id='71000000-0000-4000-8000-000000000001',context={mode:'operational',contextId:null,frameId:null};
  const data={turnId:id,hidden:null,payload:{ordinal:1,agentLabel:'Synthetic guide',plan:{pageReport:{page:'users_access',selection:{}},context,steps:[{kind:'pause',page:'users_access'}]}}};
  const event=streamFrame('walkthrough','1',JSON.stringify(data));let view=applyEvent(newTurnView({id,ordinal:1,question:'Show me.'}),event);
  assert.equal(view.walkthroughs.length,1);assert.equal(applyEvent(view,event),view);
  assert.throws(()=>streamFrame('walkthrough','2',JSON.stringify({...data,payload:{...data.payload,plan:{...data.payload.plan,steps:[{kind:'book',page:'blotter'}]}}})));
  view=applyEvent(view,streamFrame('walkthrough','2',JSON.stringify({turnId:id,hidden:'access_changed',payload:{hidden:'access_changed'}})));
  assert.deepEqual(view.walkthroughs,[]);
});

test('a walkthrough ending at Review still needs the separate Authorize click',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<div id="page"></div>',async evaluate=>{
    await evaluate(`(async()=>{window.commands=[];
      const actions=(await import('/assistant-actions.js')).setupAssistantActions({identity:()=> 'synthetic',changed:async()=>{},api:async(path,body)=>{
        commands.push(path);if(path.endsWith('/review'))return {operation:'synthetic_command',body:{memo:'Synthetic'},parameters:{},context:{mode:'operational'},status:'awaiting_user_approval'};return {status:'saved'};}});
      window.runner=(await import('/assistant-walkthroughs.js')).setupWalkthrough({identity:()=> 'synthetic',context:()=> 'pin',verify:async()=>true,
        perform:async()=>{await actions.review({turnId:'71000000-0000-4000-8000-000000000001',step:2});return document.querySelector('dialog');}});
      await runner.start({agentLabel:'Synthetic guide',ordinal:3,plan:{pageReport:{page:'blotter',selection:{}},context:{},steps:[{kind:'open',page:'blotter',pane:'review',target:null,proposalStep:2}]}});
    })()`);
    assert.deepEqual(await evaluate('commands'),['assistant/actions/review']);
    assert.equal(await evaluate("document.querySelector('dialog button').disabled"),true);
    await evaluate("document.querySelector('dialog button').click()");
    assert.equal(await evaluate("commands.filter(p=>p.endsWith('/authorize')).length"),0);
    await evaluate("document.querySelector('dialog input').click()");
    assert.equal(await evaluate("commands.filter(p=>p.endsWith('/authorize')).length"),0);
    await evaluate("document.querySelector('dialog button').click();new Promise(resolve=>setTimeout(resolve,20))");
    assert.equal(await evaluate("commands.filter(p=>p.endsWith('/authorize')).length"),1);
  });
});

test('competing input after Stop invalidates resume while runner controls do not',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<input id="ticket">',async evaluate=>{
    await evaluate(`(async()=>{window.takeovers=0;window.runner=(await import('/assistant-walkthroughs.js')).setupWalkthrough({identity:()=> 'synthetic',context:()=> 'pin',verify:async()=>true,
      perform:async()=>document.querySelector('#ticket'),takenOver:()=>takeovers++});
      void runner.start({agentLabel:'Synthetic guide',ordinal:1,plan:{pageReport:{page:'blotter',selection:{}},context:{},steps:[{kind:'pause',page:'blotter'}]}});
      await new Promise(resolve=>setTimeout(resolve,20));document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));
    })()`);
    assert.equal(await evaluate('takeovers'),0);
    await evaluate("document.querySelector('#ticket').value='7';document.querySelector('#ticket').dispatchEvent(new Event('input',{bubbles:true}))");
    assert.equal(await evaluate('takeovers'),1,'input is observed even after the plan stopped');
  });
});

test('the stored presentation tool has a panel step label',()=>assert.equal(stepLine({ordinal:2,tool:'present_walkthrough',code:null}),'Present walkthrough'));

test('prose tables without source bindings have no Keep action or store',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<div id="answer"></div>',async evaluate=>{
    await evaluate(`(async()=>{const {answerBlocks,renderBlocks}=await import('/assistant-text.js');document.querySelector('#answer').append(renderBlocks(answerBlocks('| Position | State |\\n| --- | --- |\\n| Synthetic position | Unavailable |'),document));})()`);
    assert.equal(await evaluate("document.querySelectorAll('#answer table').length"),1);
    assert.equal(await evaluate("document.querySelectorAll('#answer button,#answer input,#answer form').length"),0);
  });
});

test('a deferred Review cannot open after its expected page changes',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<div id="page"></div>',async evaluate=>{
    await evaluate(`(async()=>{window.currentPage='blotter';
      const actions=(await import('/assistant-actions.js')).setupAssistantActions({identity:()=> 'synthetic',changed:async()=>{},api:async()=>new Promise(resolve=>window.releaseReview=resolve)});
      window.opening=actions.review({turnId:'71000000-0000-4000-8000-000000000001',step:2},undefined,()=>currentPage==='blotter').catch(()=>{});
      currentPage='curves';releaseReview({operation:'synthetic',body:{},parameters:{},context:{mode:'operational'},status:'awaiting_user_approval'});await opening;
    })()`);
    assert.equal(await evaluate("document.querySelectorAll('dialog').length"),0,'a stale page must not receive the Review dialog');
  });
});

test('a native sheet keeps walkthrough Continue and Stop reachable while the background stays inert',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<link rel="stylesheet" href="/app.css"><main id="host"><button id="background">Background action</button><section id="contract"><button id="contract-close">Close</button><div id="contract-head"></div><div id="contract-tabs"></div><div id="contract-body"><div id="contract-view"></div><div id="contract-form"></div></div></section></main>',async(evaluate,command)=>{
    await evaluate(`(async()=>{const {setupWalkthrough}=await import('/assistant-walkthroughs.js'),{setupSidePanel}=await import('/side-panel.js');
      window.sheet=false;window.shell=setupSidePanel(document.querySelector('#contract'),{unsaved:()=>false,confirmLeave:async()=>true,docked:()=>false,sheet:()=>sheet});await shell.show({kind:'contract',factorId:'synthetic'});
      window.runner=setupWalkthrough({identity:()=> 'synthetic',context:()=> 'pin',verify:async()=>true,perform:async()=>document.querySelector('#contract')});
      void runner.start({agentLabel:'Synthetic guide',ordinal:1,plan:{pageReport:{page:'curves',selection:{}},context:{},steps:[{kind:'pause',page:'curves'},{kind:'say',page:'curves',text:'Continued on this sheet.'},{kind:'pause',page:'curves'}]}});
      for(let n=0;n<100&&document.querySelector('.assistant-walkthrough-continue').hidden;n++)await new Promise(resolve=>setTimeout(resolve,10));sheet=true;shell.place();
    })()`);
    assert.equal(await evaluate("document.querySelector('#background').inert"),true);
    assert.equal(await evaluate("document.querySelector('.assistant-walkthrough').inert"),false);
    const click=async selector=>{const point=await evaluate(`(()=>{const r=document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2};})()`);
      await command('Input.dispatchMouseEvent',{type:'mousePressed',...point,button:'left',clickCount:1});await command('Input.dispatchMouseEvent',{type:'mouseReleased',...point,button:'left',clickCount:1});};
    await click('.assistant-walkthrough-continue');await evaluate('new Promise(resolve=>setTimeout(resolve,20))');
    assert.match(await evaluate("document.querySelector('.assistant-walkthrough-status').textContent"),/Continued on this sheet/);
    await click('.assistant-walkthrough button:last-child');
    assert.match(await evaluate("document.querySelector('.assistant-walkthrough-status').textContent"),/^Stopped/);
    assert.equal(await evaluate("document.querySelector('#background').inert"),true);
  });
});
