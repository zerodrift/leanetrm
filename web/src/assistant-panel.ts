// The assistant panel (docs/ai-assistant.md §8, §10.1, D191): the agent picker, the conversation with its steps, answers,
// targets and usage, the composer, consent and History. A tab keeps one stream while its conversation has an unfinished
// turn, open or closed; an idle panel holds none. Only a target arriving live for a question this tab asked moves the
// page, and only when it keeps the page's context and leaves nothing unsaved; any other shows Go.
import {publicText,displayInstant} from './market-view.js';
import {PendingCommand} from './pending-command.js';
import type {Vendor} from './agent-grants.js';
import {streamFrame,streamOrder,readEvents,newTurnView,applyEvent,type TurnView,type TurnEvent,type Target,type StreamItem} from './assistant-events.js';
import {lookingAt,askKnowledge,changesContext,targetAction,NavigationHistory,stepLine,endingLine,startsOver,usageLine,consentText,grantExpiry,
  agentChoice,preselect,conversationChoice,panelMessage,keptAsk,isUuid,type Place,type Names,type UsableAgent} from './assistant-pages.js';
import {answerBlocks,renderBlocks} from './assistant-text.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Turn={id:string;conversationId:string;ordinal:number;agentId:string;version:number;question:string;state:string;askedAt:string};
type Read={conversation:{id:string;agentId:string;readThrough:number};turns:Turn[];events:unknown[]};
type Listed={id:string;agentName:string;unread:boolean;latestTurn:Turn};
// A target's notice: whether the page moved there, the place Back restores, why it waits, and the action the person was
// told leaves the unsaved changes of a page, which that page's next click of it then takes.
type Notice={moved:boolean;key?:string;reason?:string;confirm?:{action:'go'|'back';page:string}};
export type PanelOptions={api:Api;root:string;identity:()=>string;zone:()=>string;available:()=>boolean;pageBusy:()=>boolean;
  act:(work:()=>Promise<void>)=>Promise<void>;place:()=>Place;names:()=>Names;trade:()=>string|undefined;unsaved:()=>boolean;
  // Moves the page to a target in the context it shows; selects a target's context, then moves; and restores a place.
  open:(target:Target)=>Promise<void>;go:(target:Target)=>Promise<void>;restore:(place:Place)=>Promise<void>};

const streamed=['accepted','step','navigation','text','usage','completed','failed','cancelled','interrupted','replaced','refused'];
const terminal=['completed','failed','cancelled','interrupted'];
// Answers that say nothing of whether a command was written; any other refusal proves it was not.
const uncertain=['response_unknown','request_cancelled','preview_error','grant_outcome_unknown'];
const definite=(code:string|undefined)=>!!code&&!uncertain.includes(code);
const changesView='Go changes the knowledge or As of date the page shows.',leavesEdits='Go would leave unsaved changes on this page.';
const backLeavesEdits='Back would leave unsaved changes on this page.';
const unconfirmedAsk='We could not confirm that your question was received. Retry sends the same question; Discard forgets it.';

export function setupAssistant(options:PanelOptions){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const text=(id:string,value:string)=>{get(id).textContent=publicText(value);};
  function el<K extends keyof HTMLElementTagNameMap>(tag:K,className:string,content?:string){
    const node=document.createElement(tag);node.className=className;if(content!==undefined)node.textContent=publicText(content);return node;
  }
  function button(label:string,name:string,run:()=>void,className='secondary'){
    const node=el('button',className,label);node.type='button';node.setAttribute('aria-label',publicText(name));node.onclick=run;return node;
  }
  const panel=get('assistant-panel'),launcher=get<HTMLButtonElement>('assistant-launcher'),picker=get<HTMLSelectElement>('assistant-agent');
  const question=get<HTMLTextAreaElement>('assistant-question'),send=get<HTMLButtonElement>('assistant-send'),handle=get('assistant-resize');
  const asking=new PendingCommand(definite),granting=new PendingCommand(definite),history=new NavigationHistory();
  let agents:UsableAgent[]=[],vendors:Vendor[]=[],agentsError='';
  let conversation:{id:string;agentId:string;version:number;readThrough:number}|undefined,views:TurnView[]=[];
  // Each target's notice by its event's sequence; the turns this tab asked since it loaded, whose live targets may move it.
  let notices=new Map<number,Notice>(),askedHere=new Set<string>();
  // The stream this tab follows, the last sequence it read, and the stream an ask opened, the only one whose targets arrive
  // live: a stream resumed or opened again replays targets that were sent while another tab or a read had the turn.
  let source:EventSource|undefined,streamLast=0,live:EventSource|undefined,reopened=false,opened=false,unread=false,working=false,askable=false,lookedAt='';
  let note:{text:string;resume:boolean;reload:boolean}|undefined,consent:UsableAgent|undefined,error='',moving=Promise.resolve();
  // An identity change ends everything this panel started; a conversation change ends what started for the one it left.
  let identity=0,conversationGeneration=0;
  const stepsOpen=new Set<string>(),items=new Map<string,HTMLLIElement>();
  const stored=(kind:'agent'|'panel'|'ask')=>`openetrm.assistant.${kind}.${options.identity()}`;
  const latest=()=>views.at(-1);
  const running=()=>{const view=latest();return !!view&&!view.ending;};
  const lastSequence=()=>Math.max(0,...views.map(v=>v.lastSequence));
  const chosen=()=>agents.find(a=>`${a.id}:${a.version}`===picker.value);
  const message=(caught:unknown,fallback:string)=>panelMessage((caught as {code?:string}).code)??(caught instanceof Error?caught.message:fallback);

  function persist(){try{sessionStorage.setItem(stored('panel'),JSON.stringify({conversationId:conversation?.id??null,open:opened}));}catch{/* This visit keeps the panel in memory. */}}
  // An ask whose outcome is unconfirmed is kept, through reloads too, until Retry confirms it or Discard forgets it.
  function keep(){
    try{const command=asking.command;if(command)sessionStorage.setItem(stored('ask'),JSON.stringify({operationId:command.operationId,path:command.path,body:command.body}));
      else sessionStorage.removeItem(stored('ask'));}
    catch{/* The ask is kept for this visit. */}
  }
  function announce(value:string){text('assistant-status',value);}

  // The usable versions and their vendors; the launcher shows only with one to choose.
  async function readAgents(){
    const started=identity;
    try{
      const read=await options.api<{agents:UsableAgent[];vendors:Vendor[]}>('assistant/agents');
      if(started!==identity)return;
      for(const agent of read.agents)agentChoice(agent,read.vendors);
      const keep=`${picker.value}`;agents=read.agents;vendors=read.vendors;agentsError='';
      let saved:string|null=null;try{saved=localStorage.getItem(stored('agent'));}catch{/* Nothing is preselected. */}
      const listed=agents.find(a=>`${a.id}:${a.version}`===keep);
      renderPicker(listed??(conversation?conversationChoice(agents,conversation.agentId,conversation.version):preselect(agents,saved)));
    }catch(caught){
      if(started!==identity)return;
      agents=[];agentsError=message(caught,'The agents you may use could not be read.');renderPicker(undefined);
    }
    sync();
  }
  function renderPicker(selected:UsableAgent|undefined){
    picker.replaceChildren(new Option('Choose an agent',''),...agents.map(a=>new Option(publicText(agentChoice(a,vendors)),`${a.id}:${a.version}`)));
    picker.value=selected?`${selected.id}:${selected.version}`:'';
  }

  function show(open:boolean,focus=true){
    opened=open;panel.hidden=!open;document.body.classList.toggle('assistant-open',open);launcher.setAttribute('aria-expanded',String(open));
    if(open){hideHistory();unread=false;void markRead();void readAgents();}
    persist();sync();
    if(focus)(open?picker.value?question:picker:launcher).focus();
  }

  // The conversation: questions, collapsible steps, answers, targets, endings and usage.
  function render(){
    items.clear();
    get('assistant-turns').replaceChildren(...views.map(view=>{const item=el('li','assistant-turn');items.set(view.id,item);fill(item,view);return item;}));
    get('assistant-empty').hidden=views.length>0;
  }
  function update(view:TurnView){const item=items.get(view.id);if(item)fill(item,view);else render();}
  function fill(item:HTMLLIElement,view:TurnView){
    const parts:Node[]=[el('p','assistant-question',view.question)];
    if(view.steps.length){
      const details=el('details','assistant-steps'),list=el('ul','');details.open=stepsOpen.has(view.id);
      details.ontoggle=()=>{if(details.open)stepsOpen.add(view.id);else stepsOpen.delete(view.id);};
      for(const step of view.steps)list.append(el('li','',stepLine(step)));
      details.append(el('summary','',`${view.steps.length} step${view.steps.length===1?'':'s'}`),list);parts.push(details);
    }
    if(view.hidden)parts.push(el('p','assistant-hidden','This answer is hidden: what it read can no longer be read for you.'));
    else if(view.text){const answer=el('div','assistant-answer');answer.append(renderBlocks(answerBlocks(publicText(view.text)),document));parts.push(answer);}
    else if(!view.ending)parts.push(el('p','assistant-waiting','Answering…'));
    for(const target of view.targets)parts.push(targetNotice(target));
    if(view.ending?.incomplete)parts.push(el('p','assistant-incomplete','Incomplete answer'));
    const ending=view.ending&&endingLine(view.ending);
    if(ending)parts.push(el('p','assistant-ending',ending));
    if(view.ending&&startsOver(view.ending)&&view===latest())parts.push(button('New conversation','Start a new conversation',startOver));
    if(view.usage)parts.push(el('p','assistant-usage',usageLine(view.usage)));
    item.replaceChildren(...parts);
  }
  function targetNotice(item:{sequence:number;target:Target}){
    const notice=notices.get(item.sequence)??{moved:false},box=el('div','assistant-target'),label=publicText(item.target.label);
    box.append(el('span','',notice.moved?`Took you to ${label}`:label));
    if(notice.reason)box.append(el('span','assistant-target-reason',notice.reason));
    if(!notice.moved)box.append(notice.confirm?.action==='go'?button('Go anyway',`Leave unsaved changes and go to ${label}`,()=>go(item)):button(item.target.kind==='proposal'?'Review proposed curve':'Go',`Go to ${label}`,()=>go(item)));
    // A move that failed once it had changed the page keeps its Back too.
    if(notice.key&&history.origin(notice.key))box.append(notice.confirm?.action==='back'?button('Back anyway',`Leave unsaved changes and go back from ${label}`,()=>back(item))
      :button('Back',`Back from ${label}`,()=>back(item)));
    return box;
  }
  function refresh(turnId:string){const view=views.find(v=>v.id===turnId);if(view)update(view);}
  // Moves the page with `move`, recording the place it leaves for Back once the page has changed, whether or not the move
  // then finished; a place showing an unapplied historical selection, which a move discards, is not restorable. A move that
  // never ran, on a page busy with something else, leaves the notice as it was.
  async function moveFrom(item:{sequence:number},move:()=>Promise<void>){
    const started=conversationGeneration,origin=options.place(),left=JSON.stringify(origin);let moved=false;
    await options.act(async()=>{await move();moved=true;});
    if(started!==conversationGeneration||!moved&&JSON.stringify(options.place())===left)return;
    notices.set(item.sequence,{moved,key:origin.context.mode!=='unapplied'?history.record(origin):undefined});
  }
  // On a page with unsaved changes, Go and Back first say so, keeping the notice's Back, and act once that page's person
  // asks again; whether to ask is judged by the page they would leave when they click.
  function unconfirmed(item:{sequence:number},action:'go'|'back',turnId:string){
    const notice=notices.get(item.sequence)??{moved:false},page=options.place().page;
    if(!options.unsaved()||notice.confirm?.action===action&&notice.confirm.page===page)return false;
    notices.set(item.sequence,{...notice,reason:action==='go'?leavesEdits:backLeavesEdits,confirm:{action,page}});refresh(turnId);return true;
  }
  // Go selects the target's context and opens it.
  function go(item:{sequence:number;target:Target}){
    const view=views.find(v=>v.targets.some(t=>t.sequence===item.sequence));if(!view||unconfirmed(item,'go',view.id))return;
    void moveFrom(item,()=>options.go(item.target)).catch(()=>{notices.set(item.sequence,{moved:false});}).then(()=>refresh(view.id));
  }
  // Back restores the place the move left.
  function back(item:{sequence:number}){
    const view=views.find(v=>v.targets.some(t=>t.sequence===item.sequence)),notice=notices.get(item.sequence),origin=notice?.key&&history.origin(notice.key);
    if(!view||!notice||!origin||unconfirmed(item,'back',view.id))return;
    const started=conversationGeneration;
    void options.act(()=>options.restore(origin)).then(()=>{if(started===conversationGeneration){notices.set(item.sequence,{moved:notice.moved,key:notice.key});refresh(view.id);}});
  }
  // A live target moves the page, in order after any earlier one, when the panel is open on an idle page with no dialog,
  // the target keeps the page's context and nothing on the page is unsaved; otherwise, or once Go has handled it, its
  // notice offers Go.
  function consider(turnId:string,item:{sequence:number;target:Target}){
    const started=conversationGeneration;
    moving=moving.then(async()=>{
      if(started!==conversationGeneration||notices.has(item.sequence))return;
      try{
        const place=options.place(),context=item.target.kind!=='proposal'&&changesContext(item.target.context,place.context),unsaved=options.unsaved();
        const shown=opened&&!options.pageBusy()&&!document.querySelector('dialog[open]');
        if(targetAction({live:shown,changesContext:context,unsaved,proposal:item.target.kind==='proposal'})==='go')
          notices.set(item.sequence,{moved:false,reason:context?changesView:unsaved?leavesEdits:undefined,confirm:!context&&unsaved?{action:'go',page:place.page}:undefined});
        else await moveFrom(item,()=>options.open(item.target));
      }catch{notices.set(item.sequence,{moved:false});}
      if(started===conversationGeneration)refresh(turnId);
    });
  }

  // One event for a turn this panel shows; a repeated sequence changes nothing.
  function apply(event:TurnEvent,live:boolean){
    const index=views.findIndex(v=>v.id===event.turnId);
    if(index<0)throw Error('The assistant’s stream sent an event the panel cannot read.');
    const before=views[index],after=applyEvent(before,event);
    if(after===before)return undefined;
    views[index]=after;
    if(live&&event.kind==='navigation'&&after.targets.length>before.targets.length&&askedHere.has(after.id))consider(after.id,after.targets.at(-1)!);
    return after;
  }
  // A read of the conversation: its turns as their owner reads them, with their events after the panel's last.
  function take(read:Read){
    const latestTurn=read.turns.at(-1);
    conversation={id:read.conversation.id,agentId:read.conversation.agentId,version:latestTurn?.version??conversation?.version??0,
      readThrough:Math.max(read.conversation.readThrough,conversation?.id===read.conversation.id?conversation.readThrough:0)};
    for(const turn of read.turns)if(!views.some(v=>v.id===turn.id))views.push(newTurnView(turn));
    views.sort((a,b)=>a.ordinal-b.ordinal);
    for(const event of readEvents(read))apply(event,false);
    render();
  }
  // A re-read that finds the turn ended finishes it here, as its terminal event would have.
  async function reread(){
    if(!conversation)return;
    const started=conversationGeneration,id=conversation.id,wasRunning=running();
    try{
      const read=await options.api<Read>(`assistant/conversations/${id}?after=${lastSequence()}`);
      if(started===conversationGeneration){take(read);if(wasRunning&&!running())finished();}
    }
    catch(caught){if(started===conversationGeneration)refuse(caught,'The conversation could not be read.');}
    sync();
  }
  function refuse(caught:unknown,fallback:string){
    const code=(caught as {code?:string}).code;
    if(code==='session_ended'){closeStream();note={text:message(caught,fallback),resume:false,reload:true};}
    else error=message(caught,fallback);
  }

  // The stream (§8): fixed names only, each payload validated; terminal, replaced and refused close it, a failure re-reads
  // once, replay_unavailable re-reads and opens one new stream, and session_ended offers a reload instead of retrying.
  function openStream(after:number,asked=false){
    closeStream();if(!conversation)return;
    const stream=new EventSource(`${options.root}assistant/conversations/${conversation.id}/events?after=${after}`);
    source=stream;streamLast=after;if(asked)live=stream;
    for(const name of streamed)stream.addEventListener(name,event=>receive(stream,name,event as MessageEvent<string>));
    stream.onmessage=()=>broken(stream);
    stream.onerror=()=>{if(stream.readyState===EventSource.CLOSED)broken(stream);};
    sync();
  }
  function closeStream(){source?.close();source=undefined;}
  function receive(stream:EventSource,name:string,event:MessageEvent<string>){
    if(stream!==source)return;
    let item:StreamItem;
    try{item=streamFrame(name,name==='replaced'||name==='refused'?null:event.lastEventId||null,event.data);}catch{broken(stream);return;}
    if(item.kind==='replaced'){closeStream();note={text:'This answer is continuing in another tab or window.',resume:true,reload:false};sync();return;}
    if(item.kind==='refused'){refused(item.code,item.message);return;}
    // Every stored event of the turn arrives in sequence; one past a gap means the panel missed one, which a re-read fills.
    const order=streamOrder(streamLast,item.sequence);
    if(order==='repeat')return;
    if(order==='gap'){broken(stream);return;}
    streamLast=item.sequence;
    let view:TurnView|undefined;
    try{view=apply(item,stream===live);}catch{broken(stream);return;}
    const ends=terminal.includes(item.kind);
    if(ends)closeStream();
    if(view)update(view);
    if(ends)finished();
    sync();
  }
  function broken(stream:EventSource){
    if(stream!==source)return;
    closeStream();
    void reread().then(()=>{if(running()){note={text:'Live updates of this answer stopped. Resume follows it again.',resume:true,reload:false};sync();}});
  }
  function refused(code:string,text:string){
    closeStream();
    if(code==='session_ended'){note={text,resume:false,reload:true};sync();return;}
    if(code==='replay_unavailable'&&!reopened){reopened=true;void reread().then(()=>{if(running())openStream(lastSequence());});return;}
    if(code!=='turn_not_running')note={text,resume:false,reload:false};
    void reread().then(()=>{if(running()&&note)note={...note,resume:true};sync();});
  }
  function finished(){
    const view=latest();
    announce(view?.ending&&endingLine(view.ending)||'The answer is complete.');
    if(opened)void markRead();else unread=true;
  }
  // A conversation is marked read once the open panel shows it, not while History covers it.
  async function markRead(){
    if(!opened||!conversation||!get('assistant-history').hidden)return;
    const read=conversation,through=lastSequence();if(through<=read.readThrough)return;
    try{await options.api('assistant/conversations/read',{conversationId:read.id,through});read.readThrough=Math.max(read.readThrough,through);}
    catch{/* The conversation stays marked unread in History until a later read mark lands. */}
  }

  // Asking (§8): a new question has its own request identity; Retry sends the kept one exactly as first sent, whose outcome
  // a repeat answers. grant_required asks for consent; Allow grants and sends the question again as a new request.
  async function ask(retry:boolean){
    if(working||running())return;
    let command=retry?asking.command:undefined,agent:UsableAgent|undefined;
    // A kept request asks the version it named; consent, if the server asks for it, is for that version.
    if(command){const named=command.body as {agentId:string;version:number};agent=agents.find(a=>a.id===named.agentId&&a.version===named.version);}
    else{
      agent=chosen();if(!agent)return;
      const place=options.place(),knowledge=askKnowledge(place.context);
      if(JSON.stringify([place.report,place.context])!==lookedAt){error='The page changed. Check Looking at, then send again.';sync();return;}
      if(!knowledge||!askable||options.pageBusy()||!question.value.trim()||asking.command)return;
      command=asking.begin('assistant/turns/ask',{conversationId:conversation?.id??null,agentId:agent.id,version:agent.version,question:question.value,
        pageReport:place.report,knowledge});
    }
    working=true;error='';consent=undefined;sync();
    const started=identity,previouslyUncertain=asking.sending();keep();
    try{
      const answer=await options.api<{turn:Turn}>(command.path,{...command.body,operationId:command.operationId});
      if(started!==identity)return;
      asking.clear();keep();if(question.value===command.body.question)question.value='';await accept(answer.turn);
    }catch(caught){
      if(started!==identity)return;
      const code=(caught as {code?:string}).code;
      asking.failed(code,previouslyUncertain);keep();
      if(code==='grant_required'&&agent)consent=agent;
      else{refuse(caught,'Your question could not be sent.');if(asking.command)error=unconfirmedAsk;}
    }finally{if(started===identity){working=false;sync();}}
  }
  // An admitted ask is followed live: in the conversation shown, or in any other, a new one or the one a kept ask named,
  // read whole first.
  async function accept(turn:Turn){
    askedHere.add(turn.id);note=undefined;reopened=false;announce('Answering…');
    if(conversation?.id!==turn.conversationId){await load(turn.conversationId,true);return;}
    conversation.version=turn.version;
    const after=lastSequence();
    if(!views.some(v=>v.id===turn.id))views.push(newTurnView(turn));
    persist();render();openStream(after,true);
  }
  async function stop(){
    const view=latest();if(!view||view.ending||working)return;
    working=true;sync();
    const started=identity;
    try{await options.api('assistant/turns/stop',{turnId:view.id});}
    catch(caught){if(started===identity)refuse(caught,'The answer could not be stopped.');}
    finally{if(started===identity){working=false;sync();if(!source)void reread();}}
  }
  async function allow(){
    const agent=consent;if(!agent||working)return;
    const body={agentId:agent.id,version:agent.version};
    if(granting.command&&JSON.stringify(granting.command.body)!==JSON.stringify(body))granting.clear();
    const command=granting.command??granting.begin('assistant/grants/grant',body);
    working=true;error='';sync();
    const started=identity,shown=conversationGeneration,previouslyUncertain=granting.sending();
    try{
      await options.api(command.path,{...command.body,operationId:command.operationId});
      if(started!==identity)return;
      granting.clear();consent=undefined;working=false;await readAgents();
      // The question goes again only to the conversation it was written for.
      if(started===identity&&shown===conversationGeneration)await ask(false);
    }catch(caught){
      if(started!==identity)return;
      granting.failed((caught as {code?:string}).code,previouslyUncertain);
      refuse(caught,'The agent could not be allowed.');
      if(granting.command)error='We could not confirm the grant. Allow sends the same grant again.';
    }finally{if(started===identity){working=false;sync();}}
  }

  // History (§10.1): each conversation with its agent, latest question and markers; a tab learns of unread answers here.
  async function openHistory(){
    get('assistant-history').hidden=false;get('assistant-conversation').hidden=true;get('assistant-history-list').replaceChildren();
    text('assistant-history-status','Reading your conversations…');get('assistant-history-close').focus();
    const started=identity;
    try{
      const read=await options.api<{conversations:Listed[]}>('assistant/conversations');
      if(started!==identity||get('assistant-history').hidden)return;
      text('assistant-history-status',read.conversations.length?running()||working?'Stop or wait for the current answer to open another conversation.':'':'You have no conversations yet.');
      get('assistant-history-list').replaceChildren(...read.conversations.map(c=>{
        const item=el('li',c.unread?'unread':''),open=el('button','assistant-history-item');open.type='button';
        const markers=[displayInstant(c.latestTurn.askedAt,options.zone()),['queued','running','cancel_requested'].includes(c.latestTurn.state)?'Answering':'',
          c.unread?'Unread':''].filter(Boolean).join(' · ');
        open.append(el('strong','',c.agentName),el('span','',c.latestTurn.question),el('small','',markers));
        open.disabled=running()||working;open.onclick=()=>void openConversation(c.id);item.append(open);return item;
      }));
    }catch(caught){if(started===identity)text('assistant-history-status',message(caught,'Your conversations could not be read.'));}
  }
  function hideHistory(){get('assistant-history').hidden=true;get('assistant-conversation').hidden=false;}
  function closeHistory(){hideHistory();void markRead();}
  // Shows a conversation as its owner reads it, following an unfinished turn; `asked` marks the turn as this tab's ask,
  // whose stream is live.
  async function load(id:string,asked:boolean){
    closeStream();conversationGeneration++;
    conversation=undefined;views=[];notices=new Map();note=undefined;error=asking.command?unconfirmedAsk:'';render();sync();
    const started=conversationGeneration;
    try{
      const read=await options.api<Read>(`assistant/conversations/${id}?after=0`);
      if(started!==conversationGeneration)return;
      take(read);persist();
      const keep=chosen();renderPicker(keep?.id===conversation!.agentId?keep:conversationChoice(agents,conversation!.agentId,conversation!.version));
      if(running()){reopened=false;openStream(lastSequence(),asked);}
      if(opened)void markRead();else if(lastSequence()>conversation!.readThrough)unread=true;
    }catch(caught){
      if(started!==conversationGeneration)return;
      if((caught as {code?:string}).code==='conversation_not_found'){conversation=undefined;persist();}
      refuse(caught,'The conversation could not be read.');
    }
    sync();
  }
  async function openConversation(id:string){if(!running()&&!working){hideHistory();await load(id,false);}}
  function startOver(){
    if(running()||working)return;
    closeStream();conversationGeneration++;conversation=undefined;views=[];notices=new Map();note=undefined;error=asking.command?unconfirmedAsk:'';
    persist();render();sync();question.focus();
  }

  function renderLooking(){
    try{
      const place=options.place(),seen=lookingAt(place.report,place.context,options.names(),{trade:options.trade()});
      text('assistant-looking-page',seen.page);text('assistant-looking-shows',seen.shows);text('assistant-looking-context',seen.context);
      lookedAt=JSON.stringify([place.report,place.context]);askable=seen.askable;
    }catch{
      text('assistant-looking-page','This page');text('assistant-looking-shows','The assistant cannot tell what this page shows. Reload it.');
      text('assistant-looking-context','');lookedAt='';askable=false;
    }
  }
  function sync(){
    launcher.hidden=!options.available()||!agents.length;launcher.disabled=false;get('assistant-dot').hidden=!unread;
    launcher.setAttribute('aria-label',unread?'Assistant · a new answer':'Assistant');
    if(panel.hidden)return;
    renderLooking();
    const turn=running(),agent=chosen(),retained=!!asking.command;
    picker.disabled=turn||working;question.disabled=false;
    get<HTMLButtonElement>('assistant-new').disabled=turn||working||!conversation;get<HTMLButtonElement>('assistant-history-open').disabled=false;
    get<HTMLButtonElement>('assistant-close').disabled=false;
    send.type=turn?'button':'submit';send.textContent=turn?'Stop':'Send';
    send.disabled=turn?working:working||!agent||!askable||retained||options.pageBusy()||!question.value.trim();
    get('assistant-retry').hidden=get('assistant-discard').hidden=!retained;
    get<HTMLButtonElement>('assistant-retry').disabled=get<HTMLButtonElement>('assistant-discard').disabled=working||turn;
    text('assistant-composer-note',!agents.length?agentsError||'No agent is available to you.':!agent?'Choose an agent to ask.':'');
    get('assistant-consent').hidden=!consent;
    if(consent)text('assistant-consent-text',consentText(consent,vendors,grantExpiry(new Date(),consent.grantDays,options.zone())));
    get<HTMLButtonElement>('assistant-allow').disabled=get<HTMLButtonElement>('assistant-deny').disabled=working;
    text('assistant-error',error);get('assistant-error').hidden=!error;
    get('assistant-stream').hidden=!note;text('assistant-stream-text',note?.text??'');
    get('assistant-resume').hidden=!note?.resume;get('assistant-reload').hidden=!note?.reload;
    get<HTMLButtonElement>('assistant-resume').disabled=get<HTMLButtonElement>('assistant-reload').disabled=false;
    get('assistant-conversation').setAttribute('aria-busy',String(turn));
  }

  // Resizing (D45): the left edge drags or steps the width the person chose, which this browser remembers; a window too
  // narrow for it shows less of it, and it returns once the window is wide enough again.
  const widthKey='openetrm.assistant.width';
  const bounds=()=>({min:320,max:Math.max(320,Math.min(720,Math.round(window.innerWidth*.6)))});
  let chosenWidth=400;try{chosenWidth=Number(localStorage.getItem(widthKey))||400;}catch{/* The default width is used. */}
  function setWidth(value:number,save:boolean){
    const {min,max}=bounds(),next=Math.min(max,Math.max(min,Math.round(value)));
    document.documentElement.style.setProperty('--assistant-width',`${next}px`);
    handle.setAttribute('aria-valuemin',String(min));handle.setAttribute('aria-valuemax',String(max));handle.setAttribute('aria-valuenow',String(next));
    if(!save)return;
    chosenWidth=next;try{localStorage.setItem(widthKey,String(next));}catch{/* The width holds for this visit. */}
  }
  handle.onpointerdown=event=>{
    event.preventDefault();handle.setPointerCapture(event.pointerId);
    handle.onpointermove=move=>setWidth(window.innerWidth-move.clientX,false);
    handle.onpointerup=handle.onpointercancel=()=>{handle.onpointermove=handle.onpointerup=handle.onpointercancel=null;setWidth(Number(handle.getAttribute('aria-valuenow')),true);};
  };
  handle.onkeydown=event=>{
    const step=event.shiftKey?64:16,{min,max}=bounds(),shown=Number(handle.getAttribute('aria-valuenow'));
    const next=event.key==='ArrowLeft'?shown+step:event.key==='ArrowRight'?shown-step:event.key==='Home'?min:event.key==='End'?max:undefined;
    if(next!==undefined){event.preventDefault();setWidth(next,true);}
  };
  window.addEventListener('resize',()=>setWidth(chosenWidth,false));
  setWidth(chosenWidth,false);

  launcher.onclick=()=>show(!opened);
  get('assistant-close').onclick=()=>show(false);
  panel.addEventListener('keydown',event=>{if(event.key==='Escape'&&!event.isComposing&&!get('assistant-history').hidden)closeHistory();else if(event.key==='Escape'&&!event.isComposing)show(false);});
  picker.onchange=()=>{
    const agent=chosen();
    try{if(agent)localStorage.setItem(stored('agent'),JSON.stringify({agentId:agent.id,version:agent.version}));else localStorage.removeItem(stored('agent'));}
    catch{/* The choice holds for this visit. */}
    consent=undefined;
    if(conversation&&agent&&agent.id!==conversation.agentId)startOver();else sync();
  };
  question.addEventListener('input',()=>{if(error&&!asking.command)error='';sync();});
  question.addEventListener('keydown',event=>{
    if(event.key!=='Enter'||event.shiftKey||event.isComposing)return;
    event.preventDefault();if(!running())get<HTMLFormElement>('assistant-composer').requestSubmit();
  });
  get<HTMLFormElement>('assistant-composer').onsubmit=event=>{event.preventDefault();void ask(false);};
  send.onclick=()=>{if(running())void stop();};
  get('assistant-retry').onclick=()=>void ask(true);
  get('assistant-discard').onclick=()=>{if(!working){asking.clear();keep();error='';sync();}};
  get('assistant-allow').onclick=()=>void allow();
  get('assistant-deny').onclick=()=>{if(!working){consent=undefined;sync();question.focus();}};
  get('assistant-history-open').onclick=()=>void openHistory();
  get('assistant-history-close').onclick=closeHistory;
  get('assistant-new').onclick=startOver;
  get('assistant-resume').onclick=()=>{note=undefined;reopened=false;if(running())openStream(lastSequence());else void reread();sync();};
  get('assistant-reload').onclick=()=>location.reload();

  return {sync,
    // An identity starts with nothing of the last one: its own agents, and the conversation and open panel this tab kept.
    reset(){
      identity++;conversationGeneration++;closeStream();
      agents=[];vendors=[];agentsError='';conversation=undefined;views=[];notices=new Map();askedHere=new Set();history.clear();stepsOpen.clear();
      granting.clear();consent=undefined;note=undefined;error='';unread=false;working=false;moving=Promise.resolve();live=undefined;
      opened=false;panel.hidden=true;document.body.classList.remove('assistant-open');hideHistory();render();renderPicker(undefined);question.value='';
      asking.clear();sync();if(!options.available())return;
      let saved:{conversationId?:unknown;open?:unknown}|null=null,kept:string|null=null;
      try{saved=JSON.parse(sessionStorage.getItem(stored('panel'))??'null');kept=sessionStorage.getItem(stored('ask'));}catch{/* Invalid browser state restores nothing. */}
      asking.command=keptAsk(kept);if(asking.command){question.value=String(asking.command.body.question);error=unconfirmedAsk;}
      const started=identity;
      void readAgents().then(async()=>{
        if(started!==identity||!agents.length)return;
        if(isUuid(saved?.conversationId))await load(saved!.conversationId as string,false);
        if(started===identity&&saved?.open===true)show(true,false);
      });
    }};
}
