import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

// The real shell/panel, an owned browser, and synthetic retained events. No account or application is attached.
const page=(await readFile(new URL('../src/index.html',import.meta.url),'utf8'))
  .replace('<script type="module" src="/app.js"></script>','').replace('</body>','<div id="host" hidden></div></body>');
async function fixture(work){
  await withBrowserPage(page,async(evaluate,command)=>{
    await evaluate(`(async()=>{
      window.calls=[];window.effects=[];window.streams=[];window.holdVerification=false;window.actor='synthetic-controls';
      const agentId='71000000-0000-4000-8000-000000000001',conversationId='71000000-0000-4000-8000-000000000002',turnId='71000000-0000-4000-8000-000000000003';
      window.context={mode:'current',contextId:'71000000-0000-4000-8000-000000000004',frameId:'71000000-0000-4000-8000-000000000005'};
      window.report={page:'curves',selection:{view:'curves',curve:null}};
      window.turn={id:turnId,conversationId,agentId,version:1,ordinal:1,question:'Show me this page.',state:'running',askedAt:'2026-01-01T00:00:00Z',hidden:null,incomplete:false};
      window.events=[{sequence:1,turnId,kind:'accepted',at:turn.askedAt,payload:{ordinal:1,agentId,version:1}}];
      window.plan={ordinal:1,agentLabel:'Synthetic guide',plan:{pageReport:report,context,steps:[{kind:'pause',page:'curves'},{kind:'say',page:'curves',text:'Opening the screen controls.'}]}};
      window.EventSource=class extends EventTarget{static CLOSED=2;constructor(){super();streams.push(this);}close(){this.readyState=2;}};
      window.emit=(kind,payload)=>{const event={sequence:events.length+1,turnId,kind,payload,at:turn.askedAt};events.push(event);
        if(kind==='completed')turn.state='completed';
        streams.at(-1).dispatchEvent(new MessageEvent(kind,{lastEventId:String(event.sequence),data:JSON.stringify({turnId,hidden:null,payload,...kind==='completed'?{incomplete:false}:{}})}));};
      const agent={id:agentId,name:'Synthetic guide',version:1,draft:false,model:{vendor:'openai',displayName:'Synthetic model'},permissions:[],tools:['present_walkthrough'],grantDays:1,grant:null};
      localStorage.setItem('openetrm.assistant.agent.synthetic-controls',JSON.stringify({agentId,version:1}));
      window.assistant=(await import('/assistant-panel.js')).setupAssistant({root:'/api/',identity:()=>actor,zone:()=> 'UTC',available:()=>true,pageBusy:()=>false,
        api:async(path,body)=>{calls.push({path,body});
          if(path==='assistant/agents')return {agents:[agent],vendors:[{code:'openai',name:'OpenAI'}]};
          if(path==='assistant/turns/ask')return {turn};
          if(path.includes('/conversations/')&&path.includes('?after=')){
            if(holdVerification)await new Promise(resolve=>window.releaseVerification=resolve);
            return {conversation:{id:conversationId,agentId,readThrough:0},turns:window.turns??[turn],events:events.filter(e=>e.sequence>Number(path.split('?after=')[1]))};
          }
          if(path==='assistant/conversations/read'||path==='assistant/turns/stop')return {};
          throw Error('Unexpected operation: '+path);
        },act:async work=>work(),place:()=>({page:'market',report,context:{mode:'current',frameId:context.frameId,frameLabel:'Synthetic close'}}),
        names:()=>({curve:String,table:String}),trade:()=>undefined,unsaved:()=>false,open:async()=>{},go:async()=>{},restore:async()=>{},
        presentationLocate:()=>document.querySelector('#market-board'),presentation:async step=>{effects.push(step.kind);return document.querySelector('#market-board');}});
      assistant.reset();assistant.draft('Show me this page.');
      for(let n=0;n<100&&document.querySelector('#assistant-send').disabled;n++)await new Promise(r=>setTimeout(r,10));
      document.querySelector('#assistant-composer').requestSubmit();
      for(let n=0;n<100&&!streams.length;n++)await new Promise(r=>setTimeout(r,10));
    })()`);
    await work(evaluate,command);
  });
}
const replayControl="document.querySelector('[data-assistant-walkthrough-control]')";
const waitFor=async(evaluate,condition)=>{
  assert.equal(await evaluate(`(async()=>{for(let n=0;n<200;n++){if(${condition})return true;await new Promise(r=>setTimeout(r,10));}return false;})()`),true,condition);
};

test('a live guide exposes Continue and distinct stop controls, then Resume and Replay',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await fixture(async evaluate=>{
    await evaluate("emit('walkthrough',plan)");
    await waitFor(evaluate,"!!document.querySelector('.assistant-walkthrough-continue:not([hidden])')");
    assert.equal(await evaluate(replayControl),null,'a running guide must not also invite the person to start it');
    assert.equal(await evaluate("document.querySelector('#assistant-send').textContent"),'Stop answering');
    assert.equal(await evaluate("document.querySelector('.assistant-walkthrough button:last-child').textContent"),'Stop walkthrough');
    await evaluate("document.querySelector('.assistant-walkthrough button:last-child').click()");
    assert.equal(await evaluate(replayControl+'.textContent'),'Resume walkthrough','Stop updates the existing answer without waiting for another stream event');
    assert.equal(await evaluate("document.querySelector('.assistant-walkthrough button:last-child').hidden"),true);
    await evaluate(replayControl+'.click()');
    await waitFor(evaluate,"!!document.querySelector('.assistant-walkthrough-continue:not([hidden])')");
    await evaluate("emit('completed',{});document.querySelector('.assistant-walkthrough-continue').click()");
    await waitFor(evaluate,"document.querySelector('.assistant-walkthrough-status').textContent.startsWith('Walkthrough complete')");
    assert.equal(await evaluate(replayControl+'.textContent'),'Replay walkthrough');
    assert.equal(await evaluate("document.querySelector('.assistant-walkthrough button:last-child').hidden"),true,'a completed guide cannot still offer Stop');
    assert.match(await evaluate("document.querySelector('.assistant-walkthrough-status').textContent"),/Opening the screen controls/,'the guide retains its final explanation');
    await evaluate("document.querySelector('#assistant-close').click();assistant.draft('')");
    assert.equal(await evaluate(replayControl+'.textContent'),'Replay walkthrough','closing a completed guide must not turn replay into an empty resume');
    assert.match(await evaluate("document.querySelector('.assistant-walkthrough-status').textContent"),/^Walkthrough complete/);
    assert.deepEqual(await evaluate('effects'),[]);
    assert.equal(await evaluate("calls.some(c=>c.path==='assistant/turns/stop')"),false,'stopping presentation does not cancel the answer');
  });
});

test('starting and stopping during verification fences late steps and later automatic plans',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await fixture(async evaluate=>{
    await evaluate("holdVerification=true;plan.plan.steps=[{kind:'scroll_to',page:'curves',widget:'page'},{kind:'pause',page:'curves'}];emit('walkthrough',plan)");
    await waitFor(evaluate,'!!window.releaseVerification');
    assert.equal(await evaluate(replayControl),null,'verification belongs to the running guide');
    await evaluate("document.querySelector('.assistant-walkthrough button:last-child').click();holdVerification=false;releaseVerification()");
    await evaluate("emit('walkthrough',plan)");
    await evaluate('new Promise(r=>setTimeout(r,100))');
    assert.deepEqual(await evaluate('effects'),[]);
    assert.equal(await evaluate("document.querySelector('.assistant-walkthrough-cursor').hidden"),true);
    assert.equal(await evaluate(replayControl+'.textContent'),'Resume walkthrough');
    assert.equal(await evaluate("document.querySelectorAll('[data-assistant-walkthrough-control]').length"),2,'later plans remain available only for explicit activation');
  });
});


test('stopping an older guide enables plans rendered on a later answer',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await fixture(async evaluate=>{
    await evaluate("emit('walkthrough',plan);emit('completed',{})");
    await waitFor(evaluate,"!!document.querySelector('.assistant-walkthrough-continue:not([hidden])')");
    // Admit the next synthetic answer without simulating takeover of the still-paused presentation.
    await evaluate(`window.turns=[turn];window.turn={...turn,id:'71000000-0000-4000-8000-000000000006',ordinal:2,state:'running',question:'A second question'};turns.push(turn);
      document.querySelector('#assistant-question').value=turn.question;document.querySelector('#assistant-composer').requestSubmit();`);
    await waitFor(evaluate,'streams.length===2');
    await evaluate(`for(const [kind,payload] of [['walkthrough',plan],['completed',{}]]){
      const event={sequence:events.length+1,turnId:turn.id,kind,payload,at:turn.askedAt};events.push(event);
      streams.at(-1).dispatchEvent(new MessageEvent(kind,{lastEventId:String(event.sequence),data:JSON.stringify({turnId:turn.id,hidden:null,payload,...kind==='completed'?{incomplete:false}:{}})}));
    }`);
    assert.equal(await evaluate(replayControl+'.disabled'),true);
    await evaluate("document.querySelector('.assistant-walkthrough button:last-child').click()");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('[data-assistant-walkthrough-control]')].map(b=>[b.textContent,b.disabled])"),[['Resume walkthrough',false],['Start walkthrough',false]]);
  });
});

test('a new identity or conversation clears the previous guide display',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await fixture(async evaluate=>{
    await evaluate("emit('walkthrough',plan);emit('completed',{})");
    await waitFor(evaluate,"!!document.querySelector('.assistant-walkthrough-continue:not([hidden])')");
    await evaluate("document.querySelector('.assistant-walkthrough-continue').click()");
    await waitFor(evaluate,"document.querySelector('.assistant-walkthrough-status').textContent.startsWith('Walkthrough complete')");
    await evaluate("actor='another-synthetic-owner';assistant.reset();assistant.draft('Another question')");
    assert.equal(await evaluate("document.querySelector('.assistant-walkthrough').hidden"),true);
    assert.equal(await evaluate("document.querySelector('.assistant-walkthrough-label').textContent"),'');
    assert.equal(await evaluate("document.querySelector('.assistant-walkthrough-status').textContent"),'');
  });
});

test('curve navigation points at visible content rather than the shared page title',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
  await withBrowserPage(page,async evaluate=>{
    await evaluate(`(async()=>{
      window.target=(await import('/assistant-walkthroughs.js')).pagePresentationTarget;
      document.querySelector('#market-board').hidden=false;document.querySelector('#curve-detail').hidden=true;
    })()`);
    assert.equal(await evaluate("target(document.querySelector('main')).id"),'market-board');
    await evaluate("document.querySelector('#market-board').hidden=true;document.querySelector('#curve-detail').hidden=false");
    assert.equal(await evaluate("target(document.querySelector('main')).id"),'curve-detail');
    await evaluate("document.querySelector('#market-page').hidden=true;document.querySelector('#interval-curves-page').hidden=false");
    assert.equal(await evaluate("target(document.querySelector('main')).id"),'interval-curves-page','a different view cannot inherit the old page target');
  });
});
