import {actorTransport,PendingCommand,restoreTradeRequest,type RequestCommand} from './pending-command.js';

type Options={
 actor:()=>string; source:()=>{snapshotId:string}; historical:()=>boolean;
 api:<T>(path:string,body:unknown)=>Promise<T>; onBooked:(id:string)=>Promise<void>;
};
type Receipt={operationId:string;tradeId:string};
type Holder={pending:PendingCommand;store:ReturnType<typeof actorTransport<RequestCommand>>;busy:boolean};
type TicketView={actor:string;snapshotId:string;holder:Holder};
type Config={
 storageKey:string;bookPath:string;statusPath:string;knownRejection:(code:string|undefined)=>boolean;
 workingMessage:string;successMessage:string;changed:()=>void;clearEdits:()=>void;status:HTMLElement;
};

// A receipt belongs to its original command even after the visible ticket changes owners or closes.
export function structuredTicketBooking(o:Options,c:Config){
 const holders=new Map<string,Holder>();
 let view:TicketView|undefined;
 function holder(owner:string){
  let h=holders.get(owner);
  if(!h){
   const store=actorTransport<RequestCommand>(()=>c.storageKey+owner,()=>sessionStorage);
   const pending=new PendingCommand(c.knownRejection),saved=restoreTradeRequest(store.read());
   if(saved?.path===c.bookPath)pending.command={...saved,uncertain:true};
   h={pending,store,busy:false};holders.set(owner,h);
  }
  return h;
 }
 function open(){
  const actor=o.actor();
  return view={actor,snapshotId:o.source().snapshotId,holder:holder(actor)};
 }
 function current(v:TicketView){return view===v&&v.actor===o.actor()&&!o.historical()&&v.snapshotId===o.source().snapshotId;}
 function showing(h:Holder){return view?.holder===h&&current(view);}
 async function execute(mode:'book'|'status'){
  const v=view;
  if(!v)return;
  if(!current(v)){c.status.textContent=c.workingMessage;return;}
  const h=v.holder,command=h.pending.command;
  if(!command||h.busy)return;
  const booking=mode==='book';
  h.busy=true;
  const wasUncertain=booking&&h.pending.sending();
  if(booking){h.store.write(command);c.status.textContent='Booking…';}
  c.changed();
  try{
   const receipt=await o.api<Receipt|null>(booking?c.bookPath:c.statusPath,booking?{...command.body,operationId:command.operationId}:{operationId:command.operationId});
   if(h.pending.command?.operationId!==command.operationId)return;
   if(!receipt||receipt.operationId!==command.operationId||typeof receipt.tradeId!=='string'){
    if(current(v))c.status.textContent=!booking&&!receipt?'No completed booking was found. Retry the original booking.':'The booking response could not be reconciled with its request. Check its status.';
    return;
   }
   h.pending.clear();h.store.write(undefined);
   if(current(v)){c.clearEdits();c.status.textContent=c.successMessage;await o.onBooked(receipt.tradeId);}
   else if(showing(h))c.status.textContent='Original booking confirmed.';
  }catch(error){
   const message=error instanceof Error?error.message:String(error);
   if(h.pending.command?.operationId!==command.operationId){
    if(current(v))c.status.textContent=c.successMessage+' '+message;
    return;
   }
   if(booking){h.pending.failed((error as {code?:string}|null)?.code,wasUncertain);h.store.write(h.pending.command);}
   if(showing(h)){
    if(!booking)c.status.textContent='The booking status could not be read: '+message;
    else c.status.textContent=(h.pending.command?'The booking is not confirmed: ':'Booking refused: ')+message;
   }
  }finally{
   h.busy=false;
   if(view?.holder===h)c.changed();
  }
 }
 return {open,current,reset:()=>{view=undefined;},
  pending:()=>!!holder(o.actor()).pending.command,busy:()=>view?.holder.busy??false,
  book:(body:Record<string,unknown>,label:string)=>{
   if(!view||view.holder.pending.command)return;
   if(!current(view)){c.status.textContent=o.historical()?c.workingMessage:'Reopen the ticket under the selected market context.';return;}
   view.holder.pending.begin(c.bookPath,body,undefined,label);void execute('book');
  },retry:()=>execute('book'),check:()=>execute('status')};
}
