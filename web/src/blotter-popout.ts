// The tie between the main window and the Blotter popped out of it (docs/blotter-workbench.md, Pop-out Blotter), over the channel of
// blotter-channel.ts. The main window opens the pop-out for a session of its own, adopts the first window that says it opened, hands it
// the view and the context, and from then on hears only that window: what it shows, a position it asks about and, as it closes, its view.
// The pop-out takes its one hand-off, follows every context the main window sends and ends when told. Neither end acts on what it sends.
import type {openBlotterChannel,BlotterMessage,EndReason} from './blotter-channel.js';
import type {BlotterView} from './blotter-layout.js';
import type {PageState} from './assistant-pages.js';
import {refuse} from './blotter-operations.js';

/** What the pop-out reads at, as the main window's header selects it: request parameters for the pop-out's own reads, never values. */
export type BlotterContext=Extract<BlotterMessage,{kind:'context'}>['context'];
/** What a popped-out Blotter shows, as the page reports it. */
export type BlotterShown=Extract<PageState,{page:'futures'}>;
type OpenChannel=typeof openBlotterChannel;
type Channel=ReturnType<OpenChannel>;
type Example='baseline'|'exchange';
/** The pop-out's window as the main window holds it. */
type Popup={readonly closed:boolean;close():void;focus():void};
// Without each other's windows the two ends could not tell their own closing, nor bring the pop-out to the front.
const features='popup=yes,width=1440,height=900,resizable=yes,scrollbars=yes';

const uuid=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const untied='This Blotter window is not tied to a main window. Close it, and pop the Blotter out from the main window.';
/** The pop-out's address: its session and example in the fragment, which no request or referrer carries. */
export function blotterWindowUrl(source:string,{session,example}:{session:string;example:Example}){
  const url=new URL('/blotter.html',source);url.search='';url.hash=new URLSearchParams({session,example}).toString();return url.href;
}
/** The session and example a pop-out's address names, exactly; anything else is refused. */
export function readBlotterWindowUrl(href:string):{session:string;example:Example}{
  const url=new URL(href),values=new URLSearchParams(url.hash.slice(1)),keys=[...values.keys()];
  if(url.pathname!=='/blotter.html'||url.search||keys.length!==2||!values.has('session')||!values.has('example'))throw Error(untied);
  const session=values.get('session')!,example=values.get('example')!;
  if(!uuid.test(session)||example!=='baseline'&&example!=='exchange')throw Error(untied);
  return {session,example};
}

/**
 * The main window's end. `context` is what the header selects now, or undefined while it states none (an unapplied selection), which
 * leaves the pop-out on the last one sent. `returned` hears the Blotter come back: with the pop-out's view, or with the view it was handed
 * when the pop-out handed back none. `grace` is how long Bring back waits for a closing window to hand back its view.
 */
export function tieMainWindow(o:{source:string;example:Example;grace:number;session:()=>string;channel:OpenChannel;
  open:(url:string,target:string,features:string)=>Popup|null;context:()=>BlotterContext|undefined;
  returned:(view:BlotterView,handedBack:boolean)=>void;reported:()=>void;ask:(key:string)=>void}){
  type Tie={session:string;channel:Channel;popup:Popup;handed:BlotterView;instance?:string;sent?:string;shown?:BlotterShown;back?:BlotterView;settled:(()=>void)[]};
  let tie:Tie|undefined;
  const send=(t:Tie,m:Record<string,unknown>)=>t.channel.send({v:1,session:t.session,...m} as BlotterMessage);
  function receive(t:Tie,m:BlotterMessage){
    if(tie!==t)return;
    // The first window to say it opened is the pop-out; any other, a duplicate of it among them, is never heard.
    if(m.kind==='opened'){if(t.instance===undefined){t.instance=m.instance;send(t,{kind:'view',instance:m.instance,view:t.handed});follow();}return;}
    if(m.kind==='context'||m.kind==='ended'||m.instance!==t.instance)return;
    switch(m.kind){
      case 'view':t.back=m.view;return;
      case 'closed':finish(t);return;
      // Only a report of a popped-out Blotter is relayed as the pop-out's.
      case 'pageReport':if(m.state.page==='futures'&&m.state.poppedOut){t.shown=m.state;o.reported();}return;
      case 'askAbout':o.ask(m.key);return;
    }
  }
  function close(t:Tie){tie=undefined;t.channel.close();}
  function finish(t:Tie){
    close(t);
    try{o.returned(t.back??t.handed,t.back!==undefined);}finally{for(const settled of t.settled)settled();}
  }
  function follow(){
    const t=tie,context=o.context();
    if(!t||t.instance===undefined||!context)return;
    const sent=JSON.stringify(context);
    if(sent!==t.sent){t.sent=sent;send(t,{kind:'context',context});}
  }
  return {
    out:()=>tie!==undefined,
    /** Opens the pop-out and hands it `view` once it says it opened. */
    open(view:BlotterView){
      if(tie)throw refuse('the Blotter is already in a window of its own');
      if(!o.context())throw refuse('choose a market date, and apply a changed context, before the Blotter opens in a window of its own');
      const session=o.session(),handed=structuredClone(view);let t:Tie|undefined;
      const channel=o.channel(session,m=>{if(t)receive(t,m);});
      const popup=o.open(blotterWindowUrl(o.source,{session,example:o.example}),'_blank',features);
      if(!popup){channel.close();throw Error('The browser did not open the Blotter’s window. Allow pop-up windows for this site, then press Pop out again.');}
      tie=t={session,channel,popup,handed,settled:[]};
    },
    /** What the pop-out last reported it shows. */
    shown:()=>tie?.shown,
    follow,
    front(){tie?.popup.focus();},
    /**
     * Closes the pop-out, which hands back its view as it goes, and settles once the Blotter is back. A window that stays open, as one asking
     * about edits it holds does, keeps the Blotter; one that closed without a word gives it back as it was handed.
     */
    async bringBack(){
      const t=tie;
      if(!t)throw refuse('the Blotter is not in a window of its own');
      const back=new Promise<void>(resolve=>t.settled.push(resolve));
      if(!t.popup.closed){
        t.popup.close();
        await Promise.race([back,new Promise(resolve=>setTimeout(resolve,o.grace))]);
        if(tie!==t)return back;
        if(!t.popup.closed)throw refuse('the Blotter’s window stayed open, as it may hold edits; bring the Blotter back from that window');
      }
      finish(t);return back;
    },
    /** Tells every pop-out of the session why it ends, and forgets it; its window stays open to say so. */
    end(reason:EndReason){
      const t=tie;if(!t)return;
      try{send(t,{kind:'ended',reason});}finally{close(t);for(const settled of t.settled)settled();}
    }};
}
export type MainWindowTie=ReturnType<typeof tieMainWindow>;

/** The pop-out's end, which says it opened as it is tied. Once ended, or once it has left, it sends and hears nothing. */
export function tiePopoutWindow(o:{session:string;instance:string;channel:OpenChannel;handed:(view:BlotterView)=>void;context:(context:BlotterContext)=>void;ended:(reason:EndReason)=>void}){
  let open=true,handed=false,reported:string|undefined;
  const channel=o.channel(o.session,m=>{
    if(!open)return;
    if(m.kind==='context')o.context(m.context);
    else if(m.kind==='ended'){close();o.ended(m.reason);}
    else if(m.kind==='view'&&m.instance===o.instance&&!handed){handed=true;o.handed(m.view);}
  });
  const send=(m:Record<string,unknown>)=>{if(open)channel.send({v:1,session:o.session,instance:o.instance,...m} as BlotterMessage);};
  function close(){if(open){open=false;channel.close();}}
  send({kind:'opened'});
  return {
    /** Reports what the pop-out shows, when that has changed. */
    report(state:BlotterShown){const next=JSON.stringify(state);if(open&&next!==reported){send({kind:'pageReport',state});reported=next;}},
    ask(key:string){send({kind:'askAbout',key});},
    /** Hands back the view, when the pop-out holds one, and says it closed. */
    leave(view:BlotterView|undefined){
      if(!open)return;
      try{if(view)send({kind:'view',view});}finally{try{send({kind:'closed'});}finally{close();}}
    },
    close};
}
