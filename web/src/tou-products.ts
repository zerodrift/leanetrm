import {currentAuthoringVersion} from './authoring-version.js';
import {renderRegularTable} from './regular-table.js';
import {displayInstant,friendly,publicText} from './market-view.js';

type ClockRef={catalogId:string;revisionId:string;code:string};
export type Clock={reference:ClockRef;kind:string;fixedOffsetSeconds:number|null;ianaZone:string|null;tzdbVersion:string|null};
type Version={id:string;code:string;version:number;generation:number;state:'draft'|'published';name:string;clock:ClockRef;peakStartHour:number;peakEndHour:number;peakSaturday:boolean;publicationOrder:number|null;publishedAt:string|null};
type Library={versions:Version[];contextVersions:Version[];clocks:Clock[];canEdit:boolean;canPublish:boolean};
type Api=<T>(path:string,body?:unknown)=>Promise<T>;

const two=(n:number)=>String(n).padStart(2,'0');
// Stored bounds are hour-beginning [start, end); hour-ending labels are start+1 … end.
export function peakLabel(v:{peakStartHour:number;peakEndHour:number;peakSaturday:boolean}){return `${v.peakSaturday?'Mon–Sat':'Mon–Fri'} HE${two(v.peakStartHour+1)}–HE${two(v.peakEndHour)}`;}
export function clockLabel(c:Clock){
  if(c.kind==='iana'&&c.ianaZone&&c.tzdbVersion)return `${c.ianaZone} · tzdb ${c.tzdbVersion}`;
  if(c.kind==='fixed_offset'&&c.fixedOffsetSeconds!==null){const s=c.fixedOffsetSeconds,m=Math.abs(s)/60;return `Fixed UTC${s<0?'−':'+'}${two(Math.floor(m/60))}:${two(m%60)}`;}
  throw Error('This market clock is incomplete in reference data.');
}
const clockKey=(c:ClockRef)=>[c.catalogId,c.revisionId,c.code].join('|');

// Authored TOU products: everyone sees published definitions; editors draft and approvers publish.
export function setupTouProducts(run:(work:()=>Promise<void>)=>Promise<void>,api:Api,isBusy:()=>boolean,zone:()=>string,published:()=>Promise<void>){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
  const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=publicText(text);return n;};
  const form=get<HTMLFormElement>('tou-form');
  let library:Library={versions:[],contextVersions:[],clocks:[],canEdit:false,canPublish:false},selected:Version|undefined;
  const clockOf=(ref:ClockRef)=>library.clocks.find(c=>clockKey(c.reference)===clockKey(ref));
  function options(id:string,values:{value:string;label:string}[],placeholder:string){
    const first=el('option',placeholder);first.value='';get(id).replaceChildren(first,...values.map(v=>{const o=el('option',v.label);o.value=v.value;return o;}));
  }
  function body(){
    const clock=library.clocks.find(c=>clockKey(c.reference)===field('tou-clock').value);
    if(!clock||!field('tou-start').value||!field('tou-end').value||!field('tou-saturday').value)throw Error('Choose a market clock, the peak window and the peak days.');
    return {code:field('tou-code').value,name:field('tou-name').value,clock:clock.reference,peakStartHour:Number(field('tou-start').value),peakEndHour:Number(field('tou-end').value),peakSaturday:field('tou-saturday').value==='true'};
  }
  function fill(v?:Version){
    selected=v;form.reset();const current=currentAuthoringVersion(v,library.versions);for(const n of form.querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select'))n.disabled=!library.canEdit||!current;
    if(v){field('tou-code').value=v.code;field('tou-name').value=v.name;field('tou-clock').value=clockKey(v.clock);field('tou-start').value=String(v.peakStartHour);field('tou-end').value=String(v.peakEndHour);field('tou-saturday').value=String(v.peakSaturday);}
    get<HTMLInputElement>('tou-code').readOnly=!!v;
    get('tou-version').textContent=v?`${v.code} · version ${v.version} · ${v.state}${!current?' · prior version · read only':v.state==='published'?' · saving creates the next draft':''}`:'New product';
    get('tou-save').hidden=!library.canEdit||!current;get('tou-publish').hidden=!library.canPublish||!current||v?.state!=='draft';
  }
  function render(){
    get('tou-editor').hidden=!library.canEdit&&!library.canPublish;
    renderRegularTable(get('tou-list'),'tou-products',JSON.stringify(library.versions.map(v=>[v.id,v.version,v.generation])),
      ['Product','Market clock','Peak hours','Version','State',...(library.canEdit||library.canPublish?['']:[])],
      library.contextVersions.map(v=>{const clock=clockOf(v.clock);const row:(string|HTMLElement)[]=[v.name,clock?clockLabel(clock):'Unavailable clock',peakLabel(v),String(v.version),v.state==='published'?`Published ${displayInstant(v.publishedAt!,zone())}`:'Draft'];
        if(library.canEdit||library.canPublish){const open=el('button',currentAuthoringVersion(v,library.versions)?'Open':'View');open.dataset.definitionId=v.id;open.dataset.version=String(v.version);open.type='button';open.onclick=()=>{if(!isBusy())fill(v);};row.push(open);}return row;}),[3],isBusy);
  }
  async function load(){
    library=await api<Library>('tou-products');
    options('tou-clock',library.clocks.map(c=>({value:clockKey(c.reference),label:`${friendly(c.reference.code)} · ${clockLabel(c)}`})),'Choose a market clock…');
    options('tou-start',Array.from({length:24},(_,h)=>({value:String(h),label:`${two(h)}:00 · HE${two(h+1)} first`})),'Choose…');
    options('tou-end',Array.from({length:24},(_,i)=>({value:String(i+1),label:`${two(i+1)}:00 · HE${two(i+1)} last`})),'Choose…');
    render();fill(selected&&library.contextVersions.find(v=>v.id===selected!.id));
  }
  form.onsubmit=e=>{e.preventDefault();void run(async()=>{
    const saved=await api<Version>('tou-products/save',{id:selected?.id??null,version:selected?.version??null,generation:selected?.generation??null,expectedState:selected?.state??null,...body()});
    selected=saved;await load();
  });};
  get('tou-publish').onclick=()=>void run(async()=>{
    if(!selected||selected.state!=='draft')throw Error('Open a saved draft first.');
    // Publish the saved draft, never unsaved form edits.
    if(JSON.stringify({code:selected.code,name:selected.name,clock:selected.clock,peakStartHour:selected.peakStartHour,peakEndHour:selected.peakEndHour,peakSaturday:selected.peakSaturday})!==JSON.stringify(body()))throw Error('Save your changes before publishing.');
    selected=await api<Version>('tou-products/publish',{id:selected.id,version:selected.version,generation:selected.generation});await load();await published();
  });
  get('tou-new').onclick=()=>{if(!isBusy())fill();};
  return {load,reset(){library={versions:[],contextVersions:[],clocks:[],canEdit:false,canPublish:false};selected=undefined;form.reset();get('tou-list').replaceChildren();}};
}
