import type {Target,TargetContext} from './assistant-events.js';
import type {PageReport,PageFocus} from './assistant-pages.js';

export type PresentationStep=
  |{kind:'go';target:Target}|{kind:'say';page:string;text:string}|{kind:'pause';page:string}
  |{kind:'scroll_to';page:string;widget:'page'|'positions'|'details'|'charts'}
  |{kind:'select';page:string;focus:PageFocus}|{kind:'sort';page:string;column:string;direction:'asc'|'desc'}
  |{kind:'filter';page:string;field:string;value:string}
  |{kind:'open';page:string;pane:'details'|'charts'|'new_future'|'review';target:Target|null;proposalStep:number|null}
  |{kind:'fill';page:string;field:'ticket.lots'|'ticket.executionPrice'|'ticket.memo'|'swap.quantity'|'swap.fixedPrice'|'swap.memo'|'swap.structure'|'swap.fixedDirection'|'swap.priceMode';value:string};
export type Walkthrough={ordinal:number;agentLabel:string;plan:{pageReport:PageReport;context:TargetContext;steps:PresentationStep[]}};

const object=(v:unknown):v is Record<string,unknown>=>!!v&&typeof v==='object'&&!Array.isArray(v);
const exact=(v:unknown,keys:string[])=>object(v)&&Object.keys(v).length===keys.length&&keys.every(k=>Object.hasOwn(v,k));
const plain=(v:unknown,max=200)=>typeof v==='string'&&v.length<=max&&!/[\u0000-\u001f\u007f-\u009f]/.test(v);
export function readWalkthrough(value:unknown,readTarget:(v:unknown)=>Target):Walkthrough{
  const fail=()=>{throw Error('The assistant sent an unreadable presentation plan.');};
  if(!exact(value,['ordinal','agentLabel','plan']))return fail();const v=value as Record<string,any>,p=v.plan;
  if(!Number.isSafeInteger(v.ordinal)||v.ordinal<1||!plain(v.agentLabel)||!exact(p,['pageReport','context','steps'])||!object(p.pageReport)||!plain(p.pageReport.page,64)
    ||!Array.isArray(p.steps)||p.steps.length<1||p.steps.length>12)return fail();
  // Reuse the navigation context parser. No page code, URL, selector or markup is accepted.
  readTarget({page:'blotter',selection:null,context:p.context,kind:'page',label:'Page'});
  for(const s of p.steps){
    if(!object(s))return fail();if(s.kind==='go'){if(!exact(s,['kind','target']))return fail();readTarget(s.target);continue;}
    if(!plain(s.page,64))return fail();
    switch(s.kind){
      case 'say':if(!exact(s,['kind','page','text'])||!plain(s.text))return fail();break;
      case 'pause':if(!exact(s,['kind','page']))return fail();break;
      case 'scroll_to':if(!exact(s,['kind','page','widget'])||!['page','positions','details','charts'].includes(s.widget as string))return fail();break;
      case 'select':if(!exact(s,['kind','page','focus'])||!exact(s.focus,['tradeIds','pivotCell'])||!Array.isArray((s.focus as PageFocus).tradeIds))return fail();break;
      case 'sort':if(!exact(s,['kind','page','column','direction'])||!plain(s.column,64)||!['asc','desc'].includes(s.direction as string))return fail();break;
      case 'filter':if(!exact(s,['kind','page','field','value'])||!plain(s.field,64)||!plain(s.value))return fail();break;
      case 'open':if(!exact(s,['kind','page','pane','target','proposalStep'])||!['details','charts','new_future','review'].includes(s.pane as string)
        ||!(s.proposalStep===null||Number.isSafeInteger(s.proposalStep)&&(s.proposalStep as number)>0))return fail();if(s.target!==null)readTarget(s.target);break;
      case 'fill':if(!exact(s,['kind','page','field','value'])||!['ticket.lots','ticket.executionPrice','ticket.memo','swap.quantity','swap.fixedPrice','swap.memo','swap.structure','swap.fixedDirection','swap.priceMode'].includes(s.field as string)||!plain(s.value))return fail();break;
      default:return fail();
    }
  }
  return value as Walkthrough;
}

// The browser only performs a server plan through typed page adapters. It never invokes a business control.
export function setupWalkthrough(options:{host?:HTMLElement;locate?:(step:PresentationStep,signal:AbortSignal)=>HTMLElement|null|Promise<HTMLElement|null>;identity:()=>string;context:()=>string;verify:()=>Promise<boolean>;
  perform:(step:PresentationStep,signal:AbortSignal)=>Promise<HTMLElement>;takenOver?:()=>void;progress?:(index:number,status:string)=>void}){
  const root=document.createElement('div');root.className='assistant-walkthrough';root.dataset.ownControls='';root.hidden=true;root.setAttribute('aria-label','Assistant walkthrough');
  const cursor=document.createElement('div');cursor.popover='manual';cursor.className='assistant-walkthrough-cursor';cursor.hidden=true;cursor.setAttribute('aria-hidden','true');
  const label=document.createElement('strong');label.className='assistant-walkthrough-label';
  const status=document.createElement('p');status.className='assistant-walkthrough-status';status.setAttribute('role','status');status.setAttribute('aria-live','polite');
  const next=document.createElement('button');next.type='button';next.className='assistant-walkthrough-continue';next.textContent='Continue';next.hidden=true;
  const stopButton=document.createElement('button');stopButton.type='button';stopButton.textContent='Stop';
  root.append(label,status,next,stopButton);const home=options.host??document.body;home.append(root);document.body.append(cursor);
  // Inline controls join a native modal while it owns input. The cursor itself is in the top layer,
  // so a transformed dialog cannot change its viewport coordinates.
  let modal:Element|null=null;
  const place=()=>{const shown=document.querySelector('dialog:modal'),host=shown??document.querySelector('[data-assistant-presentation-sheet]')??home;if(root.parentElement!==host)host.append(root);
    if(shown!==modal){modal=shown;if(cursor.matches(':popover-open')){cursor.hidePopover();cursor.showPopover();}}};
  const layers=new MutationObserver(place);layers.observe(document.body,{childList:true,subtree:true,attributes:true,attributeFilter:['open','inert','data-assistant-presentation-sheet']});
  let linger:ReturnType<typeof setTimeout>|undefined;
  let watching:ReturnType<typeof setInterval>|undefined;
  let generation=0,abort:AbortController|undefined,continued:(()=>void)|undefined,index=0;
  let target:HTMLElement|undefined,tracking=0;
  function clearTarget(){cancelAnimationFrame(tracking);tracking=0;target?.classList.remove('assistant-walkthrough-target');target=undefined;cursor.hidden=true;if(cursor.matches(':popover-open'))cursor.hidePopover();}
  function point(control:HTMLElement,current:()=>boolean){
    clearTarget();const owner=generation;target=control;control.classList.add('assistant-walkthrough-target');place();
    const track=()=>{
      try{
      if(owner!==generation)return;
      if(!current()){stop('Stopped because the account or context changed.');return;}
      if(!control.isConnected||!control.getClientRects().length||control.closest('[hidden],[inert]')){stop('Stopped because the page control is no longer available.');return;}
      const box=control.getBoundingClientRect(),left=Math.max(0,box.left),top=Math.max(0,box.top),right=Math.min(innerWidth,box.right),bottom=Math.min(innerHeight,box.bottom);
      // The tip stays inside the visible control. An offscreen or occluded control gets no misleading pointer.
      let spot:{x:number;y:number}|undefined;
      if(right>left&&bottom>top)for(const [x,y] of [[left+Math.min(16,(right-left)/2),top+Math.min(16,(bottom-top)/2)],[(left+right)/2,(top+bottom)/2]]){
        const hit=document.elementFromPoint(x,y);if(hit&&(control===hit||control.contains(hit))){spot={x,y};break;}
      }
      cursor.hidden=!spot;if(spot){cursor.style.left=`${spot.x}px`;cursor.style.top=`${spot.y}px`;if(!cursor.matches(':popover-open'))cursor.showPopover();}else if(cursor.matches(':popover-open'))cursor.hidePopover();
      tracking=requestAnimationFrame(track);
      }catch{if(owner===generation)stop('Stopped because the page context or control is unavailable.');}
    };track();
  }
  function stop(reason='Stopped'){clearTimeout(linger);linger=undefined;cursor.classList.remove('is-clicking','is-typing');clearInterval(watching);watching=undefined;generation++;abort?.abort();abort=undefined;continued?.();continued=undefined;next.hidden=true;clearTarget();status.textContent=reason;options.progress?.(index,reason);}
  stopButton.onclick=()=>stop();next.onclick=()=>{const run=continued;continued=undefined;next.hidden=true;run?.();};
  const human=(event:Event)=>{if(event instanceof KeyboardEvent&&event.key==='Escape'){if(abort){event.preventDefault();stop();}return;}if(root.contains(event.target as Node)||(event.target instanceof Element&&event.target.closest('[data-assistant-walkthrough-control]')))return;
    options.takenOver?.();if(!abort){clearTarget();return;}
    if(event.type==='input'||event.type==='pointerdown'||event instanceof KeyboardEvent&&!['Shift','Control','Meta','Alt'].includes(event.key))stop('Stopped because you took control.');};
  for(const kind of ['keydown','pointerdown','input'])document.addEventListener(kind,human,true);
  async function start(value:Walkthrough,from=0){
    stop('Starting walkthrough');root.hidden=false;status.hidden=false;place();label.textContent=value.agentLabel;cursor.dataset.agent=value.agentLabel;const owned=++generation,who=options.identity(),context=options.context(),controller=new AbortController();abort=controller;index=from;
    watching=setInterval(()=>{try{if(who!==options.identity()||context!==options.context())stop('Stopped because the account or context changed.');}catch{stop('Stopped because the page context is unavailable.');}},50);
    let narration=value.plan.steps.slice(0,from).filter((step):step is Extract<PresentationStep,{kind:'say'}>=>step.kind==='say').at(-1)?.text??'';
    const current=()=>owned===generation&&!controller.signal.aborted&&who===options.identity()&&context===options.context();
    try{
      for(;index<value.plan.steps.length;index++){
        if(!current())throw Error('The account or context changed.');
        if(!await options.verify()||!current())throw Error('The walkthrough is no longer available in this context.');
        const step=value.plan.steps[index];status.textContent=`Step ${index+1} of ${value.plan.steps.length}`+(narration?' · '+narration:'');
        if(step.kind==='pause'){
          status.textContent+=' · Continue when you are ready.';next.hidden=false;next.focus();
          await new Promise<void>(resolve=>{continued=resolve;});if(!current())return;
        }else if(step.kind==='say'){narration=step.text;status.textContent=`Step ${index+1} of ${value.plan.steps.length} · ${narration}`;}
        else{
          clearTarget();
          const destination=await options.locate?.(step,controller.signal);if(!current())return;
          if(options.locate&&(!destination||!destination.isConnected||!destination.getClientRects().length||destination.closest('[hidden],[inert]')||destination.matches(':disabled')))throw Error('The native control is unavailable.');
          const reduced=matchMedia('(prefers-reduced-motion: reduce)').matches;
          if(destination){
            destination.scrollIntoView({block:'center',inline:'nearest',behavior:'instant'});
            await new Promise<void>(resolve=>requestAnimationFrame(()=>resolve()));if(!current())return;
            point(destination,current);
            if(cursor.hidden)throw Error('The native control is offscreen or covered.');
            await new Promise(resolve=>setTimeout(resolve,reduced?0:120));if(!current())return;
            cursor.classList.add(step.kind==='fill'?'is-typing':'is-clicking');
            await new Promise(resolve=>setTimeout(resolve,reduced?0:50));if(!current())return;
            if(step.kind==='fill')destination.focus({preventScroll:true});
            if(destination.matches(':disabled'))throw Error('The native control is unavailable.');
            point(destination,current);if(!current())return;
            if(cursor.hidden)throw Error('The native control is offscreen or covered.');
          }
          const performing=options.perform(step,controller.signal);clearTarget();
          const control=await performing;if(!current())return;
          if(!control.isConnected)throw Error('The page control is no longer available.');
          control.scrollIntoView({block:'nearest',inline:'nearest',behavior:'instant'});point(control,current);
          await new Promise(resolve=>setTimeout(resolve,reduced?0:100));cursor.classList.remove('is-clicking','is-typing');if(!current())return;
        }
        options.progress?.(index+1,'Presented');
      }
      if(current()){
        abort=undefined;next.hidden=true;status.textContent='Walkthrough complete. Review and submit any change yourself.';options.progress?.(index,status.textContent);
        linger=setTimeout(()=>{if(owned===generation){clearTarget();clearInterval(watching);watching=undefined;}},2500);
      }
    }catch(error){if(owned===generation)stop(`Stopped at step ${index+1}: ${error instanceof Error?error.message:'The page could not perform it.'}`);}
  }
  return {start,stop,active:()=>!!abort,destroy(){stop();layers.disconnect();for(const kind of ['keydown','pointerdown','input'])document.removeEventListener(kind,human,true);root.remove();cursor.remove();}};
}
