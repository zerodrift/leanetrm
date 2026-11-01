import type {ScheduleView,ScheduleViewCell,TicketColumn,TicketDescription} from './ticket-types.js';

// Draws a backend schedule view as grid data. Every value, aggregate and total is the backend's; the browser only picks the rows and columns
// of a tab and level and formats exact text for display.
export type ScheduleTab={id:string;label:string;colour:string};
export type ScheduleSide={kind:string;code:string;name:string};
export type CellDecoration={sources:string[];changed:boolean;delivered:boolean};
export type HeaderGroup={title:string;colour:string;columns:number[]};
export const sourceLabels:Readonly<Record<string,string>>=Object.freeze({contract:'Contract',fwd:'Fwd',fixed:'Fixed',actual:'Actual',meter:'Meter',forecast:'Forecast',estimated:'Estimated',modeled:'Modeled'});
const neutral='neutral',numericMeasures=['quantity','price','money'];

/** The symbol of a field's or column's declared unit (TicketField.unit), from the units the latest preview resolved; null when it declares
 *  none, or the preview has not resolved it yet. */
export function resolvedUnit(unit:string|null,quantityUnit:string|null){
 if(unit===null)return null;
 if(unit!=='quantity')throw Error(`The ticket does not resolve the unit '${unit}'.`);
 return quantityUnit;
}

export const sideKind=(d:TicketDescription,kind:string)=>d.sides.find(k=>k.kind===kind)??(()=>{throw Error(`The ${d.product} description has no '${kind}' side kind.`);})();

export function scheduleTabs(d:TicketDescription,sides:ScheduleSide[]):ScheduleTab[]{
 return [{id:'instrument',label:'Instrument',colour:neutral},...sides.map(s=>({id:s.code,label:s.name,colour:sideKind(d,s.kind).colour})),...d.tabs.map(t=>({id:t.id,label:t.label,colour:neutral}))];
}

// Groups the digits of an exact decimal, or of each part of an exact a/b ratio, without rounding it.
const grouped=(digits:string)=>digits.replace(/\B(?=(\d{3})+$)/g,',');
export function numberText(value:string){
 const m=/^(-?)(\d+)(\.\d+)?(?:\/(\d+))?$/.exec(value);
 if(!m)throw Error(`A schedule value is not an exact number: ${value}`);
 return m[1]+grouped(m[2])+(m[3]??'')+(m[4]?'/'+grouped(m[4]):'');
}
export function instantText(value:string){
 const m=/^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})(?::\d{2}(?:\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/.exec(value);
 if(!m)throw Error(`A schedule time is not an ISO-8601 instant: ${value}`);
 return `${m[1]} ${m[2]} ${m[3]==='Z'?'+00:00':m[3]}`;
}
const displayText=(column:TicketColumn,value:string)=>value===''?'':column.measure==='time'?instantText(value):numberText(value);

export function scheduleGridData(view:ScheduleView,d:TicketDescription,sides:ScheduleSide[],tab:string,level:string,quantityUnit:string,previous?:ScheduleView){
 const depth=view.levels.indexOf(level);
 if(depth<0)throw Error(`The schedule has no ${level} rows.`);
 const groups:{title:string;colour:string;columns:{key:string;column:TicketColumn}[]}[]=[];
 const sideGroup=(s:ScheduleSide)=>{const kind=sideKind(d,s.kind);return {title:s.name,colour:kind.colour,columns:kind.columns.map(column=>({key:`${s.code}.${column.id}`,column}))};};
 if(tab==='instrument')groups.push(...sides.map(sideGroup),{title:'Trade',colour:neutral,columns:d.tradeColumns.map(column=>({key:`trade.${column.id}`,column}))});
 else{const side=sides.find(s=>s.code===tab);if(!side)throw Error(`Tab '${tab}' does not show a schedule.`);groups.push(sideGroup(side));}
 const columns=groups.flatMap(g=>g.columns);
 const headers=['Period',...columns.map(({column})=>{const unit=resolvedUnit(column.unit,quantityUnit);return unit?`${column.label} (${unit})`:column.label;})];
 let at=1;
 const headerGroups:HeaderGroup[]=tab==='instrument'?groups.map(g=>({title:g.title,colour:g.colour,columns:g.columns.map(()=>at++)})):[];
 const numeric=columns.flatMap(({column},i)=>numericMeasures.includes(column.measure)?[i+1]:[]);
 const valueOf=(cells:Record<string,ScheduleViewCell>,key:string)=>{
  const found=cells[key];if(!found)throw Error(`The schedule view has no '${key}' value.`);
  for(const source of found.sources)if(!Object.hasOwn(sourceLabels,source))throw Error(`Unknown value source: ${source}`);
  return found;
 };
 const before=new Map(previous?.rows.map(r=>[r.key,r.cells]));
 const shown=view.rows.filter(r=>{const n=view.levels.indexOf(r.level);if(n<0)throw Error(`Schedule row ${r.key} has the unknown level ${r.level}.`);return n<=depth;});
 const rows:string[][]=[],displayRows:string[][]=[],decorations:(CellDecoration|null)[][]=[];
 for(const r of shown){
  const found=columns.map(({key})=>valueOf(r.cells,key)),prior=before.get(r.key);
  const values=found.map(c=>c.value??'');
  rows.push([r.key,...values]);
  displayRows.push([r.key,...values.map((v,i)=>displayText(columns[i].column,v))]);
  decorations.push([null,...found.map((c,i)=>({sources:c.sources.map(s=>sourceLabels[s]),changed:previous!==undefined&&(prior?.[columns[i].key]?.value??null)!==c.value,delivered:r.delivered}))]);
 }
 // In delivery the backend splits its totals into delivered and remaining; the grid pins them above the total, in that order.
 if((view.delivered===null)!==(view.remaining===null))throw Error('The schedule view reports delivered and remaining together or not at all.');
 const totalRow=(label:string,cells:Record<string,ScheduleViewCell>)=>[label,...columns.map(({key,column})=>displayText(column,valueOf(cells,key).value??''))];
 const totals=[...view.delivered&&view.remaining?[totalRow('Delivered',view.delivered),totalRow('Remaining',view.remaining)]:[],totalRow('Total',view.totals)];
 return {headers,rows,displayRows,parents:shown.map(r=>r.parent),keys:shown.map(r=>r.key),columns:['period',...columns.map(c=>c.key)],numeric,headerGroups,totals,decorations};
}
