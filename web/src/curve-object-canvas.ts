import {curveLayout,type CurveDefinition,type PublishedSourceReference} from './custom-curve-model.js';
import {curveObjects,curveObjectTitle,curveConnectionLabel,type ObjectConnection} from './curve-object-model.js';
import {publicText} from './market-view.js';

type Source={code:string;name:string;contracts?:{id:string;label:string;deliveryFrom:string;deliveryTo:string}[]};
type Options={definition:CurveDefinition;sources:Source[];published:{label:string;reference:PublishedSourceReference}[];
 operators:{kind:string;label:string}[];selected:string;connection?:string;editable:boolean;busy:boolean;
 select:(id:string)=>void;connect:(connection:ObjectConnection)=>void;connectSource:(source:string)=>void;contract:(source:string,contract:string)=>void;
 terms?:{available:boolean;open:(source:string,contract?:string)=>void};expanded?:Set<string>};
const observers=new WeakMap<HTMLElement,ResizeObserver>();

/** Object cards and selectable operation connections over the existing definition.
 * Backend previews remain the only source of prices; card labels do not resolve terms. */
export function renderCurveObjectCanvas(host:HTMLElement,o:Options){
 observers.get(host)?.disconnect();
 const view=curveObjects(o.definition),el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=publicText(text);return e;};
 const scene=el('div');scene.className='curve-object-scene';
 const projected:CurveDefinition={...o.definition,nodes:view.nodes.map(({node})=>({...node,inputs:Object.fromEntries(view.connections.filter(e=>e.target===node.id).map((e,i)=>['connection_'+i,e.source]))}))};
 const positions=curveLayout(projected),cards=new Map<string,HTMLElement>();
 const choose=(text:string,run:()=>void)=>{const b=el('button',text);b.type='button';b.disabled=o.busy;b.onclick=run;return b;};
 for(const object of view.nodes){const n=object.node,card=el('article');card.className='curve-object-card';card.dataset.objectId=n.id;card.dataset.role=object.role;
  card.classList.toggle('selected',n.id===o.selected);card.classList.toggle('output',n.id===o.definition.output);
  const title=choose(curveObjectTitle(object,o.definition,o.sources,o.published),()=>o.select(n.id));title.className='curve-object-title';title.setAttribute('aria-pressed',String(n.id===o.selected));
  const kind=el('span',object.role==='curve'?(n.kind==='published_source'?'Published curve':'Source curve'):object.role==='contracts'?'Contract selection':object.role==='result'?'Computed curve':'Operation');kind.className='curve-object-kind';
  const identity=el('small',n.id);card.append(kind,title,identity);
  if(n.publishedSource)card.append(el('p',`Version ${n.publishedSource.version} · ${n.publishedSource.outputNode}`));
  if(n.kind==='weighted_contracts')card.append(el('p',`${n.contractWeights?.length??0} exact contract mappings`));
  if(n.id===o.definition.output)card.append(el('p','Selected curve output'));
  if(o.editable){const connect=choose('Connect…',()=>o.connectSource(n.id));connect.className='curve-object-connect';connect.dataset.requiresEdit='true';connect.setAttribute('aria-label','Connect output from '+n.id);card.append(connect);}
  const terms=(contract?:string)=>{const b=choose(contract?'Terms':'Contract terms',()=>o.terms?.open(n.id,contract));b.className='curve-object-terms';b.dataset.requiresSaved='true';b.disabled=o.busy||!o.terms?.available;if(!o.terms?.available)b.title='Save the current graph before configuring contract terms';if(contract)b.dataset.contractTerms=contract;return b;};
  if(n.kind==='source'&&o.terms)card.append(terms());
  const contracts=n.kind==='source'?o.sources.find(s=>s.code===n.parameters.curve)?.contracts??[]:[];
  if(contracts.length){const details=el('details'),summary=el('summary',`Contracts · ${contracts.length}`),list=el('ul');list.className='curve-object-contracts';
   for(const contract of contracts){const item=el('li'),b=choose(contract.label,()=>o.contract(n.id,contract.id));b.disabled=o.busy||!o.editable;b.dataset.requiresEdit='true';b.setAttribute('aria-label','Connect fixed contract '+contract.label);b.dataset.contractId=contract.id;item.append(b,el('small',`${contract.deliveryFrom} → ${contract.deliveryTo}`));if(o.terms)item.append(terms(contract.id));list.append(item);}
   details.open=o.expanded?.has(n.id)??false;details.append(summary,list);details.ontoggle=()=>{if(details.open)o.expanded?.add(n.id);else o.expanded?.delete(n.id);measure();};card.append(details);}
  scene.append(card);cards.set(n.id,card);
 }
 const ns='http://www.w3.org/2000/svg',wires=document.createElementNS(ns,'svg');wires.classList.add('curve-object-wires');wires.setAttribute('aria-hidden','true');scene.prepend(wires);
 const buttons=view.connections.map(connection=>{const b=choose(curveConnectionLabel(o.definition,connection,o.operators),()=>o.connect(connection));b.className='curve-object-operation';b.dataset.objectConnection=connection.id;b.setAttribute('aria-pressed',String(o.connection===connection.id));b.setAttribute('aria-label',`${curveConnectionLabel(o.definition,connection,o.operators)} from ${connection.source} to ${connection.target}`);scene.append(b);return {connection,button:b};});
 function measure(){
  const columns=Math.max(1,...Object.values(positions).map(p=>1+(p.x-24)/288)),cardWidth=Math.max(180,Math.min(224,(host.clientWidth-48-(columns-1)*112)/columns));
  for(const card of cards.values())card.style.width=cardWidth+'px';
  const placed=curveLayout(projected,{},Object.fromEntries([...cards].map(([id,c])=>[id,c.offsetHeight||140])));
  for(const p of Object.values(placed))p.x=24+(p.x-24)/288*(cardWidth+112);
  let width=Math.min(720,host.clientWidth),height=340;for(const [id,c] of cards){const p=placed[id]??positions[id];c.style.left=p.x+'px';c.style.top=p.y+'px';width=Math.max(width,p.x+cardWidth+24);height=Math.max(height,p.y+(c.offsetHeight||140)+28);}
  scene.style.width=width+'px';scene.style.height=height+'px';wires.setAttribute('width',String(width));wires.setAttribute('height',String(height));wires.replaceChildren();
  for(const {connection,button} of buttons){const from=cards.get(connection.source)!,to=cards.get(connection.target)!,a=placed[connection.source],z=placed[connection.target];
   const sx=a.x+from.offsetWidth,sy=a.y+from.offsetHeight/2,tx=z.x,ty=z.y+to.offsetHeight/2,dx=Math.max(36,(tx-sx)/2),path=document.createElementNS(ns,'path');
   path.setAttribute('d',`M ${sx} ${sy} C ${sx+dx} ${sy}, ${tx-dx} ${ty}, ${tx} ${ty}`);wires.append(path);
   button.style.left=(sx+tx)/2+'px';button.style.top=(sy+ty)/2+'px';
  }
 }
 if(!view.nodes.length){const empty=el('p','Add a source curve or a published output, then connect its contracts to a result.');empty.className='curve-object-empty';scene.append(empty);}
 host.replaceChildren(scene);measure();
 // Card sizes can change when its tab is shown. Observers belong to this render.
 const observer=new ResizeObserver(()=>{if(!host.contains(scene)){observer.disconnect();return;}measure();});
 observers.set(host,observer);observer.observe(host);
}
