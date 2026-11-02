// The events of a turn as the panel reads them (docs/ai-assistant.md §8): stored events under their fixed names, each
// with its sequence, turn, hidden marker and a payload of one closed shape, and the stream's replaced and refused. The
// panel accepts nothing else: anything it cannot read closes the stream, which then reads the turn again.
export type TargetContext={mode:'current'|'retained'|'operational';contextId:string|null;frameId:string|null};
export type Target={page:string;selection:Record<string,unknown>|null;context:TargetContext;kind:'page'|'record'|'filter'|'proposal'|'draft';label:string};
export type Dimension={count:number;unknownCalls:number};
export type Usage={calls:number;uncachedInput:Dimension;cacheRead:Dimension;cacheWrite:Dimension;output:Dimension;reasoning:Dimension;
  cost:{currency:{catalogId:string;revisionId:string;code:string};amount:string|null;unpricedCalls:number}[]};
export type StepPayload={ordinal:number;tool:string;code?:string}|{withheld:number};
export type StoredKind='accepted'|'step'|'navigation'|'text'|'usage'|'completed'|'failed'|'cancelled'|'interrupted';
export type TurnEvent={sequence:number;turnId:string;kind:StoredKind;hidden:string|null;incomplete:boolean|null;payload:Record<string,unknown>};
export type StreamItem=TurnEvent|{kind:'replaced'}|{kind:'refused';code:string;message:string};

const stored:readonly string[]=['accepted','step','navigation','text','usage','completed','failed','cancelled','interrupted'];
const terminal:readonly string[]=['completed','failed','cancelled','interrupted'];
export const tools:readonly string[]=['read_current_page','read_market_data','read_trades','read_trade_parties','read_schedules','read_reference_data','read_custom_curves','prepare_custom_curve','save_custom_curve_draft','read_jobs','navigate','read_workspace_page','discover_page_operations','read_page_operation','propose_page_action'];
export const readPages:readonly string[]=['curves','contract_definitions','schedules','power_tous','reference_data','parties','blotter','custom_curves','jobs','risk_jobs','delivery_prices','interval_series','interval_curves','users_access','settings_ai','blotter_risk','blotter_risk_lens','blotter_new_trade','blotter_new_future','blotter_new_swap'];
const uuid=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,codePattern=/^[a-z][a-z0-9_]{0,63}$/,decimal=/^-?[0-9]+(\.[0-9]+)?$/;

const unreadable=()=>Error('The assistant’s stream sent an event the panel cannot read.');
type Json=Record<string,unknown>;
const isObject=(value:unknown):value is Json=>typeof value==='object'&&value!==null&&!Array.isArray(value);
// An object with exactly these members.
function exactly(value:unknown,names:string[]):Json{
  if(!isObject(value)||Object.keys(value).length!==names.length||!names.every(n=>Object.hasOwn(value,n)))throw unreadable();
  return value;
}
const require=(condition:boolean)=>{if(!condition)throw unreadable();};
const counted=(value:unknown)=>Number.isSafeInteger(value)&&(value as number)>=0;
const positive=(value:unknown)=>Number.isSafeInteger(value)&&(value as number)>0;
const id=(value:unknown)=>typeof value==='string'&&uuid.test(value);
const code=(value:unknown)=>typeof value==='string'&&codePattern.test(value);
const hidden=(value:unknown)=>value===null||value==='access_changed';

function target(value:unknown):Target{
  const t=exactly(value,['page','selection','context','kind','label']),c=exactly(t.context,['mode','contextId','frameId']);
  require(typeof t.page==='string'&&readPages.includes(t.page)&&(t.kind==='page'||t.kind==='record'||t.kind==='filter'||t.kind==='proposal'||t.kind==='draft')
    &&(t.kind==='page'?t.selection===null:isObject(t.selection))&&(t.kind!=='record'||t.page==='curves'||t.page==='blotter'||t.page==='jobs')
    &&(c.mode==='operational'?c.contextId===null&&c.frameId===null:(c.mode==='current'||c.mode==='retained')&&id(c.contextId))&&(c.frameId===null||id(c.frameId))
    &&typeof t.label==='string'&&t.label.length>=1&&t.label.length<=200&&!/[\u0000-\u001f\u007f-\u009f]/.test(t.label));
  // A proposal opens a preparation step for review, and a draft the curve a save step saved (D260): each names exactly its turn and step.
  if(t.kind==='proposal'||t.kind==='draft'){const p=exactly(t.selection,['turnId','step']);require(t.page==='custom_curves'&&id(p.turnId)&&positive(p.step));}
  return t as Target;
}

function usage(value:unknown):Usage{
  const u=exactly(value,['calls','uncachedInput','cacheRead','cacheWrite','output','reasoning','cost']);
  require(counted(u.calls)&&Array.isArray(u.cost));
  for(const name of ['uncachedInput','cacheRead','cacheWrite','output','reasoning']){const d=exactly(u[name],['count','unknownCalls']);require(counted(d.count)&&counted(d.unknownCalls));}
  for(const entry of u.cost as unknown[]){
    const c=exactly(entry,['currency','amount','unpricedCalls']),currency=exactly(c.currency,['catalogId','revisionId','code']);
    require(id(currency.catalogId)&&id(currency.revisionId)&&typeof currency.code==='string'&&currency.code.length>0&&counted(c.unpricedCalls)
      &&(c.amount===null?(c.unpricedCalls as number)>0:typeof c.amount==='string'&&decimal.test(c.amount)&&c.unpricedCalls===0));
  }
  return u as Usage;
}

function payload(kind:string,value:unknown):Json{
  if(!isObject(value))throw unreadable();
  const names=Object.keys(value);
  switch(kind){
    case 'accepted':{const p=exactly(value,['ordinal','agentId','version']);require(positive(p.ordinal)&&id(p.agentId)&&positive(p.version));return p;}
    case 'text':
      if(names.length===1&&Object.hasOwn(value,'hidden')){require(value.hidden==='access_changed');return value;}
      require(typeof exactly(value,['text']).text==='string');return value;
    case 'navigation':
      if(names.length===1&&Object.hasOwn(value,'hidden')){require(value.hidden==='access_changed');return value;}
      {const p=exactly(value,['ordinal','target']);require(positive(p.ordinal));target(p.target);return p;}
    case 'step':
      if(names.length===1&&Object.hasOwn(value,'withheld')){require(positive(value.withheld));return value;}
      {const p=exactly(value,Object.hasOwn(value,'code')?['ordinal','tool','code']:['ordinal','tool']);
        require(positive(p.ordinal)&&typeof p.tool==='string'&&tools.includes(p.tool)&&(!Object.hasOwn(p,'code')||code(p.code)));return p;}
    case 'usage':return usage(value);
    default:
      if(names.length)require(code(exactly(value,['code']).code));
      return value;
  }
}

// One frame of the stream: its event name, its id line, if any, and its data line.
export function streamFrame(name:string,eventId:string|null,data:string):StreamItem{
  let value:unknown;
  try{value=JSON.parse(data);}catch{throw unreadable();}
  if(name==='replaced'){require(eventId===null);exactly(value,[]);return {kind:'replaced'};}
  if(name==='refused'){
    require(eventId===null);const r=exactly(value,['code','message']);require(code(r.code)&&typeof r.message==='string'&&r.message.length>0);
    return {kind:'refused',code:r.code as string,message:r.message as string};
  }
  require(stored.includes(name)&&eventId!==null&&/^[1-9][0-9]{0,15}$/.test(eventId)&&Number.isSafeInteger(Number(eventId)));
  const ends=terminal.includes(name),d=exactly(value,ends?['turnId','hidden','incomplete','payload']:['turnId','hidden','payload']);
  require(id(d.turnId)&&hidden(d.hidden)&&(!ends||typeof d.incomplete==='boolean'));
  return {sequence:Number(eventId),turnId:d.turnId as string,kind:name as StoredKind,hidden:d.hidden as string|null,
    incomplete:ends?d.incomplete as boolean:null,payload:payload(name,d.payload)};
}

// A conversation read's events, each with its turn's hidden marker and, for a terminal event, its turn's incompleteness.
export function readEvents(read:{turns:unknown[];events:unknown[]}):TurnEvent[]{
  require(Array.isArray(read.turns)&&Array.isArray(read.events));
  const turns=new Map<string,Json>();
  for(const turn of read.turns){require(isObject(turn)&&id(turn.id)&&hidden(turn.hidden)&&typeof turn.incomplete==='boolean');turns.set((turn as Json).id as string,turn as Json);}
  return read.events.map(raw=>{
    const e=exactly(raw,['sequence','turnId','kind','payload','at']),turn=turns.get(e.turnId as string);
    require(positive(e.sequence)&&turn!==undefined&&typeof e.kind==='string'&&stored.includes(e.kind));
    const ends=terminal.includes(e.kind as string);
    return {sequence:e.sequence as number,turnId:e.turnId as string,kind:e.kind as StoredKind,hidden:turn!.hidden as string|null,
      incomplete:ends?turn!.incomplete as boolean:null,payload:payload(e.kind as string,e.payload)};
  });
}

// Where a stored event read on a stream falls after the last one that stream read: the next sequence, one already read,
// or past a gap an event the panel did not read left, which only a fresh read of the turn fills.
export const streamOrder=(last:number,sequence:number)=>sequence<=last?'repeat':sequence===last+1?'next':'gap';

export type Step={ordinal:number;tool:string;code:string|null}|{withheld:number};
export type Ending={state:'completed'|'failed'|'cancelled'|'interrupted';code:string|null;incomplete:boolean};
export type TurnView={id:string;ordinal:number;question:string;text:string;steps:Step[];targets:{ordinal:number;sequence:number;target:Target}[];
  usage:Usage|null;ending:Ending|null;hidden:string|null;lastSequence:number};

export function newTurnView(turn:{id:string;ordinal:number;question:string}):TurnView{
  return {id:turn.id,ordinal:turn.ordinal,question:turn.question,text:'',steps:[],targets:[],usage:null,ending:null,hidden:null,lastSequence:0};
}

// A turn's view after one more of its events: one already applied, by its sequence, changes nothing. Once an event says
// the answer is hidden, its text and targets are hidden from then on.
export function applyEvent(view:TurnView,event:TurnEvent):TurnView{
  require(event.turnId===view.id);
  if(event.sequence<=view.lastSequence)return view;
  const next:TurnView={...view,lastSequence:event.sequence,hidden:view.hidden??event.hidden};
  const p=event.payload;
  switch(event.kind){
    case 'text':if(!next.hidden&&typeof p.text==='string')next.text=view.text+p.text;break;
    case 'navigation':if(!next.hidden&&isObject(p.target))next.targets=[...view.targets,{ordinal:p.ordinal as number,sequence:event.sequence,target:p.target as Target}];break;
    case 'step':next.steps=[...view.steps,Object.hasOwn(p,'withheld')?{withheld:p.withheld as number}:{ordinal:p.ordinal as number,tool:p.tool as string,code:(p.code as string|undefined)??null}];break;
    case 'usage':next.usage=p as unknown as Usage;break;
    case 'accepted':break;
    default:next.ending={state:event.kind,code:(p.code as string|undefined)??null,incomplete:event.incomplete===true};
  }
  if(next.hidden){next.text='';next.targets=[];}
  return next;
}
