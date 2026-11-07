import test from 'node:test';
import assert from 'node:assert/strict';
import {stepLine} from '../dist/assistant-pages.js';
import {streamFrame,applyEvent,newTurnView} from '../dist/assistant-events.js';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

test('the cursor reaches the native control before its action and no floating box is created',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<link rel="stylesheet" href="/app.css"><main id="host"><input id="terms"><section id="panel"></section></main>',async evaluate=>{
    await evaluate(`(async()=>{window.acted=false;window.pointed=false;const field=document.querySelector('#terms');
      window.runner=(await import('/assistant-walkthroughs.js')).setupWalkthrough({host:document.querySelector('#panel'),identity:()=> 'synthetic',context:()=> 'pin',verify:async()=>true,
        locate:()=>field,perform:async()=>{const cursor=document.querySelector('.assistant-walkthrough-cursor'),tip=cursor.getBoundingClientRect(),box=field.getBoundingClientRect();pointed=!cursor.hidden&&tip.left>=box.left&&tip.left<box.right&&tip.top>=box.top&&tip.top<box.bottom;field.value='2';acted=true;return field;}});
      await runner.start({agentLabel:'Synthetic guide',ordinal:1,plan:{pageReport:{page:'blotter',selection:{}},context:{},steps:[{kind:'fill',page:'blotter',field:'ticket.lots',value:'2'}]}});
    })()`);
    assert.equal(await evaluate('acted&&pointed'),true,'the user sees where the action will happen');
    assert.equal(await evaluate("!!document.querySelector('aside.assistant-walkthrough')"),false);
    assert.equal(await evaluate("document.querySelector('#panel .assistant-walkthrough')!==null"),true);
    assert.equal(await evaluate("document.querySelector('.assistant-walkthrough-cursor').hidden"),false,'the final target remains visible briefly');
    await evaluate('runner.stop()');
    assert.equal(await evaluate("document.querySelector('.assistant-walkthrough-cursor').hidden"),true);
  });
});

test('Stop while the cursor approaches prevents the native mutation',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<link rel="stylesheet" href="/app.css"><main id="host"><input id="terms"></main>',async evaluate=>{
    await evaluate(`(async()=>{window.effects=0;window.runner=(await import('/assistant-walkthroughs.js')).setupWalkthrough({identity:()=> 'synthetic',context:()=> 'pin',verify:async()=>true,
      locate:()=>{setTimeout(()=>runner.stop(),20);return document.querySelector('#terms');},perform:async()=>{effects++;return document.querySelector('#terms');}});
      await runner.start({agentLabel:'Synthetic guide',ordinal:1,plan:{pageReport:{page:'blotter',selection:{}},context:{},steps:[{kind:'fill',page:'blotter',field:'ticket.lots',value:'2'}]}});})()`);
    assert.equal(await evaluate('effects'),0);
  });
});

test('a missing or hidden native anchor stops before sorting or filtering',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<main id="host"><input id="hidden" hidden></main>',async evaluate=>{
    await evaluate(`(async()=>{window.effects=0;window.runner=(await import('/assistant-walkthroughs.js')).setupWalkthrough({identity:()=> 'synthetic',context:()=> 'pin',verify:async()=>true,locate:step=>step.kind==='sort'?null:document.querySelector('#hidden'),perform:async()=>{effects++;return document.querySelector('#host');}});
      for(const step of [{kind:'sort',page:'blotter',column:'book',direction:'asc'},{kind:'filter',page:'blotter',field:'book',value:'Synthetic'}])await runner.start({agentLabel:'Synthetic guide',ordinal:1,plan:{pageReport:{page:'blotter',selection:{}},context:{},steps:[step]}});})()`);
    assert.equal(await evaluate('effects'),0);
    assert.match(await evaluate("document.querySelector('.assistant-walkthrough-status').textContent"),/Stopped at step 1/);
  });
});

test('a control covered during the cursor approach receives no mutation',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<link rel="stylesheet" href="/app.css"><main id="host"><input id="terms" style="position:absolute;left:120px;top:150px"></main>',async evaluate=>{
    await evaluate(`(async()=>{window.effects=0;window.runner=(await import('/assistant-walkthroughs.js')).setupWalkthrough({identity:()=> 'synthetic',context:()=> 'pin',verify:async()=>true,
      locate:()=>{setTimeout(()=>{const cover=document.createElement('div');cover.style='position:fixed;inset:0;background:white;z-index:9999';document.body.append(cover);},40);return document.querySelector('#terms');},
      perform:async()=>{effects++;return document.querySelector('#terms');}});await runner.start({agentLabel:'Synthetic guide',ordinal:1,plan:{pageReport:{page:'blotter',selection:{}},context:{},steps:[{kind:'fill',page:'blotter',field:'ticket.lots',value:'2'}]}});})()`);
    assert.equal(await evaluate('effects'),0);
    assert.match(await evaluate("document.querySelector('.assistant-walkthrough-status').textContent"),/Stopped at step 1/);
  });
});

test('an aimed control may disappear during its native read without cancelling the transition',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<link rel="stylesheet" href="/app.css"><main id="host"><button id="source">Open ticket</button><input id="ticket" hidden></main>',async evaluate=>{
    await evaluate(`(async()=>{window.runner=(await import('/assistant-walkthroughs.js')).setupWalkthrough({identity:()=> 'synthetic',context:()=> 'pin',verify:async()=>true,locate:()=>document.querySelector('#source'),perform:async(step,signal)=>{
      document.querySelector('#source').hidden=true;await new Promise(resolve=>window.release=resolve);signal.throwIfAborted();document.querySelector('#ticket').hidden=false;return document.querySelector('#ticket');}});
      window.running=runner.start({agentLabel:'Synthetic guide',ordinal:1,plan:{pageReport:{page:'blotter',selection:{}},context:{},steps:[{kind:'go',target:{}}]}});
      for(let n=0;n<100&&!window.release;n++)await new Promise(r=>setTimeout(r,10));await new Promise(r=>setTimeout(r,60));})()`);
    assert.equal(await evaluate('runner.active()'),true);
    await evaluate('release();running');
    assert.equal(await evaluate("document.querySelector('#ticket').hidden"),false);
    assert.match(await evaluate("document.querySelector('.assistant-walkthrough-status').textContent"),/Walkthrough complete/);
  });
});

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
  await withBrowserPage('<link rel="stylesheet" href="/app.css"><aside id="assistant-panel"><section id="walk-host"></section><input id="chat"></aside><main id="host"><button id="background">Background action</button><section id="contract"><button id="contract-close">Close</button><div id="contract-head"></div><div id="contract-tabs"></div><div id="contract-body"><div id="contract-view"></div><div id="contract-form"></div></div></section></main>',async(evaluate,command)=>{
    await evaluate(`(async()=>{const {setupWalkthrough}=await import('/assistant-walkthroughs.js'),{setupSidePanel}=await import('/side-panel.js');
      window.sheet=false;window.shell=setupSidePanel(document.querySelector('#contract'),{unsaved:()=>false,confirmLeave:async()=>true,docked:()=>false,sheet:()=>sheet});await shell.show({kind:'contract',factorId:'synthetic'});
      window.runner=setupWalkthrough({host:document.querySelector('#walk-host'),identity:()=> 'synthetic',context:()=> 'pin',verify:async()=>true,perform:async()=>document.querySelector('#contract')});
      void runner.start({agentLabel:'Synthetic guide',ordinal:1,plan:{pageReport:{page:'curves',selection:{}},context:{},steps:[{kind:'pause',page:'curves'},{kind:'say',page:'curves',text:'Continued on this sheet.'},{kind:'pause',page:'curves'}]}});
      for(let n=0;n<100&&document.querySelector('.assistant-walkthrough-continue').hidden;n++)await new Promise(resolve=>setTimeout(resolve,10));sheet=true;shell.place();
    })()`);
    assert.equal(await evaluate("document.querySelector('#background').inert"),true);
    assert.equal(await evaluate("document.querySelector('#assistant-panel').inert"),true);
    assert.equal(await evaluate("document.querySelector('#contract .assistant-walkthrough')!==null&&!document.querySelector('.assistant-walkthrough').closest('[inert]')"),true);
    const click=async selector=>{const point=await evaluate(`(()=>{const r=document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2};})()`);
      await command('Input.dispatchMouseEvent',{type:'mousePressed',...point,button:'left',clickCount:1});await command('Input.dispatchMouseEvent',{type:'mouseReleased',...point,button:'left',clickCount:1});};
    await click('.assistant-walkthrough-continue');await evaluate('new Promise(resolve=>setTimeout(resolve,20))');
    assert.match(await evaluate("document.querySelector('.assistant-walkthrough-status').textContent"),/Continued on this sheet/);
    await click('.assistant-walkthrough button:last-child');
    assert.match(await evaluate("document.querySelector('.assistant-walkthrough-status').textContent"),/^Stopped/);
    assert.equal(await evaluate("document.querySelector('#background').inert"),true);
  });
});

test('inline controls track their control and Stop releases stale highlights',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<link rel="stylesheet" href="/app.css"><main id="host"><button id="target" style="position:absolute;left:420px;top:140px">Synthetic control</button></main>',async(evaluate,command)=>{
    await command('Emulation.setEmulatedMedia',{features:[{name:'prefers-reduced-motion',value:'reduce'}]});
    await evaluate(`(async()=>{window.runner=(await import('/assistant-walkthroughs.js')).setupWalkthrough({identity:()=> 'synthetic',context:()=> 'pin',verify:async()=>true,perform:async()=>document.querySelector('#target')});
      window.plan={agentLabel:'Synthetic guide',ordinal:1,plan:{pageReport:{page:'blotter',selection:{}},context:{},steps:[{kind:'scroll_to',page:'blotter',widget:'positions'},{kind:'pause',page:'blotter'}]}};
      void runner.start(plan);})()`);
    const pointsAtTarget=()=>evaluate(`(()=>{const c=document.querySelector('.assistant-walkthrough-cursor'),p=c.getBoundingClientRect(),t=document.querySelector('#target').getBoundingClientRect();return !c.hidden&&p.left>=t.left&&p.left<t.right&&p.top>=t.top&&p.top<t.bottom;})()`);
    for(let attempt=0;attempt<100&&!await pointsAtTarget();attempt++)await new Promise(resolve=>setTimeout(resolve,10));
    assert.equal(await pointsAtTarget(),true);
    await evaluate(`(async()=>{document.querySelector('#target').style.top='280px';document.querySelector('#target').style.left='500px';for(let n=0;n<100;n++){await new Promise(r=>setTimeout(r,10));const c=document.querySelector('.assistant-walkthrough-cursor').getBoundingClientRect(),t=document.querySelector('#target').getBoundingClientRect();if(c.left>=t.left&&c.left<t.right&&c.top>=t.top&&c.top<t.bottom)return;}})()`);
    assert.equal(await pointsAtTarget(),true,JSON.stringify(await evaluate("({cursor:document.querySelector('.assistant-walkthrough-cursor').outerHTML,target:document.querySelector('#target').getBoundingClientRect().toJSON(),status:document.querySelector('.assistant-walkthrough-status').textContent})")));
    assert.equal(await evaluate("getComputedStyle(document.querySelector('.assistant-walkthrough-cursor')).animationName"),'none');
    assert.equal(await evaluate("document.querySelectorAll('.assistant-walkthrough-minimize,.assistant-walkthrough-close,aside.assistant-walkthrough').length"),0);
    await evaluate("document.querySelector('.assistant-walkthrough button:last-child').click()");
    assert.equal(await evaluate('runner.active()'),false);
    assert.equal(await evaluate("document.querySelector('.assistant-walkthrough-cursor').hidden"),true);
    assert.equal(await evaluate("document.querySelectorAll('.assistant-walkthrough-target').length"),0);
    await evaluate("void runner.start(plan);new Promise(r=>setTimeout(r,60))");
    await evaluate("(async()=>{document.querySelector('#target').remove();for(let n=0;n<100&&runner.active();n++)await new Promise(r=>setTimeout(r,10));})()");
    assert.equal(await evaluate('runner.active()'),false);
    assert.match(await evaluate("document.querySelector('.assistant-walkthrough-status').textContent"),/control is no longer available/);
  });
});

test('a native Review dialog leaves guide controls usable without approving a change',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<link rel="stylesheet" href="/app.css"><main id="host"></main>',async(evaluate,command)=>{
    await evaluate(`(async()=>{window.commands=[];const actions=(await import('/assistant-actions.js')).setupAssistantActions({identity:()=> 'synthetic',changed:async()=>{},api:async(path)=>{commands.push(path);return {operation:'synthetic_command',body:{},parameters:{},context:{mode:'operational'},status:'awaiting_user_approval'};}});
      window.runner=(await import('/assistant-walkthroughs.js')).setupWalkthrough({identity:()=> 'synthetic',context:()=> 'pin',verify:async()=>true,perform:async()=>{await actions.review({turnId:'71000000-0000-4000-8000-000000000001',step:2});return document.querySelector('dialog');}});
      void runner.start({agentLabel:'Synthetic guide',ordinal:1,plan:{pageReport:{page:'blotter',selection:{}},context:{},steps:[{kind:'open',page:'blotter',pane:'review',target:null,proposalStep:2},{kind:'pause',page:'blotter'},{kind:'say',page:'blotter',text:'You review this.'},{kind:'pause',page:'blotter'}]}});
      await new Promise(r=>setTimeout(r,450));})()`);
    const click=async selector=>{const point=await evaluate(`(()=>{const r=document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2};})()`);await command('Input.dispatchMouseEvent',{type:'mousePressed',...point,button:'left',clickCount:1});await command('Input.dispatchMouseEvent',{type:'mouseReleased',...point,button:'left',clickCount:1});};
    await click('.assistant-walkthrough-continue');await evaluate('new Promise(r=>setTimeout(r,30))');
    assert.match(await evaluate("document.querySelector('.assistant-walkthrough-status').textContent"),/You review this/);
    await click('.assistant-walkthrough button:last-child');
    assert.equal(await evaluate('runner.active()'),false);
    assert.equal(await evaluate("document.querySelector('dialog').open"),true);
    assert.equal(await evaluate("document.querySelector('dialog input').checked"),false);
    assert.deepEqual(await evaluate('commands'),['assistant/actions/review']);
  });
});

test('stopping a guide fences a late control and a replaced run owns its highlight',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<link rel="stylesheet" href="/app.css"><main id="host"><button id="target">Synthetic control</button></main>',async evaluate=>{
    await evaluate(`(async()=>{window.hold=true;window.runner=(await import('/assistant-walkthroughs.js')).setupWalkthrough({identity:()=> 'synthetic',context:()=> 'pin',verify:async()=>true,perform:async()=>{if(hold)await new Promise(resolve=>window.release=resolve);return document.querySelector('#target');}});
      window.plan={agentLabel:'Synthetic guide',ordinal:1,plan:{pageReport:{page:'blotter',selection:{}},context:{},steps:[{kind:'scroll_to',page:'blotter',widget:'positions'},{kind:'pause',page:'blotter'}]}};
      window.first=runner.start(plan);for(let n=0;n<100&&!window.release;n++)await new Promise(r=>setTimeout(r,10));runner.stop();release();await first;})()`);
    assert.equal(await evaluate("!runner.active()&&document.querySelector('.assistant-walkthrough-cursor').hidden"),true);
    await evaluate("hold=false;void runner.start(plan);new Promise(r=>setTimeout(r,40))");
    await evaluate("runner.stop();void runner.start(plan);new Promise(r=>setTimeout(r,450))");
    assert.equal(await evaluate("runner.active()&&document.querySelector('#target').classList.contains('assistant-walkthrough-target')"),true);
    await evaluate('runner.destroy()');
    assert.equal(await evaluate("document.querySelectorAll('.assistant-walkthrough,.assistant-walkthrough-cursor,.assistant-walkthrough-target').length"),0);
  });
});

test('a tracking failure stops cleanly and a later run can start',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<link rel="stylesheet" href="/app.css"><main id="host"><button id="target">Synthetic control</button></main>',async evaluate=>{
    await evaluate(`(async()=>{window.errors=[];window.addEventListener('error',e=>{errors.push(e.message);e.preventDefault();});window.failTracking=false;window.inFrame=false;const frame=requestAnimationFrame;window.requestAnimationFrame=callback=>frame(time=>{inFrame=true;try{callback(time);}finally{inFrame=false;}});
      window.runner=(await import('/assistant-walkthroughs.js')).setupWalkthrough({identity:()=> 'synthetic',context:()=>{if(failTracking&&inFrame){failTracking=false;throw Error('Synthetic context unavailable');}return 'pin';},verify:async()=>true,perform:async()=>document.querySelector('#target')});
      window.plan={agentLabel:'Synthetic guide',ordinal:1,plan:{pageReport:{page:'blotter',selection:{}},context:{},steps:[{kind:'scroll_to',page:'blotter',widget:'positions'},{kind:'pause',page:'blotter'}]}};
      void runner.start(plan);await new Promise(r=>setTimeout(r,450));failTracking=true;await new Promise(r=>setTimeout(r,150));})()`);
    assert.deepEqual(await evaluate('errors'),[]);
    assert.equal(await evaluate('runner.active()'),false);
    assert.equal(await evaluate("document.querySelector('.assistant-walkthrough-cursor').hidden&&document.querySelectorAll('.assistant-walkthrough-target').length===0"),true);
    await evaluate('void runner.start(plan);new Promise(r=>setTimeout(r,450))');
    assert.equal(await evaluate('runner.active()'),true);
  });
});

test('a later page transition releases the previous pointer before awaiting native reads',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage('<link rel="stylesheet" href="/app.css"><main id="host"><button id="first">Positions</button><button id="second" hidden>Ticket</button></main>',async evaluate=>{
    await evaluate(`(async()=>{window.runner=(await import('/assistant-walkthroughs.js')).setupWalkthrough({identity:()=> 'synthetic',context:()=> 'pin',verify:async()=>true,perform:async step=>{if(step.kind==='scroll_to')return document.querySelector('#first');document.querySelector('#first').hidden=true;await new Promise(resolve=>window.release=resolve);document.querySelector('#second').hidden=false;return document.querySelector('#second');}});
      void runner.start({agentLabel:'Synthetic guide',ordinal:1,plan:{pageReport:{page:'blotter',selection:{}},context:{},steps:[{kind:'scroll_to',page:'blotter',widget:'positions'},{kind:'go',target:{}},{kind:'pause',page:'blotter_new_swap'}]}});
      for(let n=0;n<100&&!window.release;n++)await new Promise(r=>setTimeout(r,10));await new Promise(r=>setTimeout(r,100));})()`);
    assert.equal(await evaluate('runner.active()'),true,'a native transition does not count as a vanished target');
    await evaluate('release();new Promise(r=>setTimeout(r,450))');
    assert.equal(await evaluate("runner.active()&&!document.querySelector('.assistant-walkthrough-continue').hidden&&document.querySelector('#second').classList.contains('assistant-walkthrough-target')"),true);
  });
});
