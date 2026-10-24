import type {Workspace} from './market-view.js';
import {publicText,displayReferenceRows,type ReferenceRevision} from './market-view.js';
export function setupUnitConverter(options:{api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
 let quantities:{code:string;label:string;commodity:string;basis:string;dimension:string}[]=[];
 function clear(){get('unit-conversion-result').textContent='';}
 function targets(){clear();const source=quantities.find(q=>q.code===field('unit-from').value),previous=field('unit-to').value;const choices=source?quantities.filter(q=>q.commodity===source.commodity&&q.basis===source.basis&&q.dimension===source.dimension):[];get<HTMLSelectElement>('unit-to').replaceChildren(new Option('Choose target unit…',''),...choices.map(q=>new Option(q.label,q.code)));field('unit-to').value=choices.some(q=>q.code===previous)?previous:'';}
 function sources(){const previous=field('unit-from').value,choices=quantities.filter(q=>q.commodity===field('unit-commodity').value);get<HTMLSelectElement>('unit-from').replaceChildren(new Option('Choose source quantity…',''),...choices.map(q=>new Option(q.label,q.code)));field('unit-from').value=choices.some(q=>q.code===previous)?previous:'';targets();}
 field('unit-commodity').onchange=sources;
 field('unit-from').onchange=targets;
 for(const id of ['unit-value','unit-to','unit-rounding','unit-decimals','unit-tie'])field(id).oninput=()=>{clear();get('unit-rounding-fields').hidden=field('unit-rounding').value==='exact';};
 get<HTMLFormElement>('unit-conversion-form').onsubmit=event=>{event.preventDefault();clear();void options.act(async()=>{
  const exact=field('unit-rounding').value==='exact';
  const result=await options.api<{formatted:string;exact:boolean}>('units/convert',{value:field('unit-value').value,from:field('unit-from').value,to:field('unit-to').value,decimalPlaces:exact?null:Number(field('unit-decimals').value),tieRule:exact?null:field('unit-tie').value});
  get('unit-conversion-result').textContent=publicText(result.formatted+(result.exact?' · exact':' · rounded using the selected precision'));
 });};
 return {
  reset(){quantities=[];clear();get('unit-converter').hidden=true;for(const id of ['unit-commodity','unit-from','unit-to'])field(id).replaceChildren();},
  populate(workspace:Workspace,show:boolean){get('unit-converter').hidden=!show;if(!show)return;
   const previous=field('unit-commodity').value;
   const source=workspace.unitCatalog?.revision as ReferenceRevision,units=displayReferenceRows('units',workspace.references.units,workspace.unitReferenceDisplay,source),commodities=displayReferenceRows('commodities',workspace.references.commodities,workspace.unitReferenceDisplay,source);
   quantities=workspace.references.quantity_specifications.map(q=>{const unit=units.find(u=>u.code===q.unit_code);const commodity=commodities.find(c=>c.code===q.commodity_code);if(!unit||!commodity)throw Error('An exact unit reference is unavailable.');return {code:String(q.code),label:`${commodity.name} · ${unit.symbol} · ${String(q.basis_code).replace(/^test_/,'').replaceAll('_',' ')}`,commodity:String(q.commodity_code),basis:String(q.basis_code),dimension:String(q.dimension_code)};});
   const available=commodities.filter(c=>quantities.some(q=>q.commodity===c.code));
   get<HTMLSelectElement>('unit-commodity').replaceChildren(new Option('Choose commodity…',''),...available.map(c=>new Option(publicText(c.name),String(c.code))));field('unit-commodity').value=available.some(c=>c.code===previous)?previous:'';sources();
  }
 };
}
