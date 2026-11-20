import type {Editor,Terms} from './ppa-contract.js';
import type {Reference} from './desk-model.js';
import {amountText,rationalText,prepareAmountDisplay} from './amount-display.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Membership={bindingId:string;originRevision:number;instrumentId:string;sideId:string;role:string;ordinal:number};
type Policies={contract:string;evidence:string;rounding:string;approval:string;correction:string;adjustmentDue:string};
export type SettlementCapability={policies:Policies;indexedPolicies:Policies};
type Rounding={currency:Reference;increment:string;tieRule:string;stage:'payment_total'};
type Lineage={id:string;original_binding_id:string;original_side_id:string;rounding:string;rounding_increment:string|null;rounding_tie_rule:string|null;rounding_stage:string|null;currency_code:string;reference_catalog_id:string;reference_revision_id:string};
type Preview={exactCash:{numerator:string;denominator:string};roundedCash:{numerator:string;denominator:string};roundingDifference:{numerator:string;denominator:string};roundingTerms:Rounding};
const originalTerms=(editor:Editor,bindingId:string)=>{
 const b=editor.revisionBindings.find(r=>r.revision===1)?.bindings.find(b=>b.bindingId===bindingId);
 const instrument=editor.instruments.find(i=>i.instrumentId===b?.instrumentId);
 if(!b||!instrument)throw Error('The exact original binding and terms are unavailable.');
 return {binding:b,instrument};
};
export function paymentLineageRequest(editor:Editor,bindingId:string,sideId:string,snapshotId:string,capability:SettlementCapability,rounding:Rounding,retained?:Lineage){
 const {binding,instrument}=originalTerms(editor,bindingId),side=instrument.sideIds.find(s=>s.sideId===sideId);
 if(!side||!['strike_payment','indexed_payment'].includes(side.role))throw Error('Choose the exact original fixed or indexed payment side.');
 const currency={revision:{catalogId:instrument.terms.referenceCatalogId,revisionId:instrument.terms.referenceRevisionId},code:instrument.terms.currency};
 if(JSON.stringify([currency.revision.catalogId,currency.revision.revisionId,currency.code])!==JSON.stringify([rounding.currency.revision.catalogId,rounding.currency.revision.revisionId,rounding.currency.code]))throw Error('Rounding must retain the original payment currency.');
 if(!rounding.increment||!rounding.tieRule)throw Error('Choose the rounding increment and tie rule explicitly.');
 const original:Membership={bindingId:binding.bindingId,originRevision:1,instrumentId:binding.instrumentId,sideId:side.sideId,role:side.role,ordinal:side.ordinal};
 const mappings=editor.bindings.map(b=>{
  const i=editor.instruments.find(i=>i.instrumentId===b.instrument_id),m=i?.sideIds.find(m=>m.role===side.role&&m.ordinal===side.ordinal);
  const revisions=editor.revisionBindings.filter(r=>r.bindings.some(x=>x.bindingId===b.binding_id)).map(r=>r.revision);
  if(!m||!revisions.length)throw Error('A current binding lacks the exact corresponding payment membership.');
  return {bindingId:b.binding_id,originRevision:Math.min(...revisions),instrumentId:b.instrument_id,sideId:m.sideId,role:m.role,ordinal:m.ordinal};
 });
 const policies=side.role==='strike_payment'?capability.policies:capability.indexedPolicies;
 return {snapshotId,tradeId:editor.tradeId,expectedRevision:editor.head.revision,original,mappings,policies:{...policies,rounding:retained?.rounding??policies.rounding},roundingTerms:rounding};
}

export function mountPpaCashPreview(host:HTMLElement,o:{api:Api;act:(work:()=>Promise<void>)=>void;editor:Editor;snapshot:()=>string;known:()=>Promise<string>;capability:SettlementCapability;can:boolean;busy:()=>boolean;useCurrentKnowledge:()=>Promise<void>}){
 host.replaceChildren();if(!o.can)return {sync(){},dispose(){}};
 const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const node=document.createElement(tag);if(text)node.textContent=text;return node;};
 const panel=el('details'),title=el('summary','Monthly cash preview'),fields=el('div');fields.className='schedule-fields';
 const selected=el('select'),increment=el('input'),tie=el('select'),month=el('input'),currency=el('p'),status=el('p'),output=el('div'),evidence=el('pre');
 increment.inputMode='decimal';month.type='month';selected.add(new Option('Choose original payment side…',''));tie.replaceChildren(new Option('Choose tie rule…',''),new Option('Half to even','to_even'),new Option('Half away from zero','away_from_zero'));
 const currencyLabel=(currency:Reference)=>o.editor.currencyLabels?.find(c=>c.reference.code===currency.code&&c.reference.revision.catalogId===currency.revision.catalogId&&c.reference.revision.revisionId===currency.revision.revisionId)?.label??currency.code;
 const termCurrency=(t:Terms)=>currencyLabel({revision:{catalogId:t.referenceCatalogId,revisionId:t.referenceRevisionId},code:t.currency});
 const originals=o.editor.revisionBindings.find(r=>r.revision===1)?.bindings??[];
 for(const b of originals){const {instrument}=originalTerms(o.editor,b.bindingId);for(const s of instrument.sideIds.filter(s=>['strike_payment','indexed_payment'].includes(s.role)))selected.add(new Option(`${s.role.replace(/_/g,' ')} · ${s.ordinal+1} · ${termCurrency(instrument.terms)}`,JSON.stringify([b.bindingId,s.sideId])));}
 const field=(name:string,input:HTMLElement)=>{const label=el('label',name);label.append(input);fields.append(label);};
 field('Original payment',selected);field('Rounding increment',increment);field('Tie rule',tie);field('Operating month',month);
 let lineages:Lineage[]=[],retained:Lineage|undefined,identity:string|undefined,generation=0;
 const plan=el('button','Save terms and use current knowledge'),preview=el('button','Preview complete month'),read=el('button','Read retained payment terms');for(const b of [plan,preview,read])b.type='button';preview.disabled=true;
 const exact=el('details');exact.append(el('summary','Exact payment terms and evidence'),evidence);
 panel.append(title,el('p','Round once after the complete payment total. This preview requires final meter readings and any selected index prices; it does not accept or pay cash.'),fields,currency,read,plan,preview,status,output,exact);host.append(panel);
 function invalidate(){generation++;output.replaceChildren();sync();}
 function choose(){invalidate();identity=undefined;retained=undefined;increment.value='';tie.value='';currency.textContent='';status.textContent='';evidence.textContent='';if(!selected.value){sync();return;}
  const [bindingId,sideId]=JSON.parse(selected.value),{instrument}=originalTerms(o.editor,bindingId),t=instrument.terms;
  currency.textContent=`Payment currency: ${termCurrency(t)} · rounding stage: complete payment total`;
  retained=lineages.find(l=>l.original_binding_id===bindingId&&l.original_side_id===sideId);
  if(retained){identity=retained.id;increment.value=retained.rounding==='cash_2dp_half_even_per_occurrence_v1'?'0.01':retained.rounding_increment??'';tie.value=retained.rounding==='cash_2dp_half_even_per_occurrence_v1'?'to_even':retained.rounding_tie_rule??'';status.textContent='Retained immutable payment terms.';evidence.textContent=JSON.stringify(retained,null,2);}
  sync();
 }
 function sync(){panel.inert=o.busy();increment.disabled=tie.disabled=o.busy()||!!retained;preview.disabled=o.busy()||!identity;plan.disabled=o.busy()||!selected.value;read.disabled=o.busy();}
 selected.onchange=choose;increment.oninput=tie.onchange=month.oninput=invalidate;
 read.onclick=()=>o.act(async()=>{const token=++generation;const result=await o.api<Lineage[]>('settlement/payment-lineages/read',{snapshotId:o.snapshot(),tradeId:o.editor.tradeId,knownAt:await o.known()});if(token!==generation)return;lineages=result;choose();status.textContent=result.length?'Retained terms loaded. Select an original payment side.':'No payment terms retained. Choose every new rounding term explicitly.';});
 plan.onclick=()=>o.act(async()=>{
  if(!selected.value)throw Error('Choose the original payment side.');const token=++generation,[bindingId,sideId]=JSON.parse(selected.value),t:Terms=originalTerms(o.editor,bindingId).instrument.terms;
  const rounding:Rounding={currency:{revision:{catalogId:t.referenceCatalogId,revisionId:t.referenceRevisionId},code:t.currency},increment:increment.value,tieRule:tie.value,stage:'payment_total'};
  const request=paymentLineageRequest(o.editor,bindingId,sideId,o.snapshot(),o.capability,rounding,retained);
  const result=await o.api<{lineage:Lineage}>('settlement/payment-lineages/plan',request);if(token!==generation)return;await o.useCurrentKnowledge();if(token!==generation)return;lineages=[...lineages.filter(l=>l.id!==result.lineage.id),result.lineage];choose();status.textContent='Exact payment continuity and rounding terms saved. Current knowledge refreshed for preview.';
 });
 preview.onclick=()=>o.act(async()=>{
  if(!identity||!month.value)throw Error('Select retained payment terms and one complete operating month.');const token=++generation;
  const result=await o.api<Preview>('settlement/ppa/preview',{snapshotId:o.snapshot(),lineageId:identity,expectedRevision:o.editor.head.revision,month:month.value+'-01',knownAt:await o.known()});
  await prepareAmountDisplay({cashAmount:result.exactCash,presentValue:result.roundedCash,contribution:result.roundingDifference},body=>o.api<string[]>('amounts/display',body));if(token!==generation)return;
  const text=(v:Preview['exactCash'])=>amountText(rationalText(v)!,'money');output.replaceChildren(el('p',`Exact cash ${text(result.exactCash)} · rounded cash ${text(result.roundedCash)} ${currencyLabel(result.roundingTerms.currency)} · rounding difference ${text(result.roundingDifference)}`));evidence.textContent=JSON.stringify(result,null,2);
 });
 sync();return {sync,dispose(){generation++;panel.inert=true;}};
}
