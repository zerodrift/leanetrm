import {type PricingGraph,pricingEquation} from './pricing-view.js';
import {deliveryLabel,displayDate,publicText} from './market-view.js';

function el<K extends keyof HTMLElementTagNameMap>(tag:K,content?:string){const node=document.createElement(tag);if(content!==undefined)node.textContent=publicText(content);return node;}

/** Display the supplied pricing trace; source-only results need no diagram. */
export function renderPricingGraph(target:HTMLElement,graph:PricingGraph,curveName:(code:string)=>string,unit:(quote:string)=>string){
  const single=graph.nodes.length===1&&graph.root.inputs.length===0;target.classList.toggle('single-source',single);
  const summary=el('div');summary.className='flow-summary';summary.append(el('span',`${curveName(graph.root.curve)} · ${deliveryLabel(graph.root.deliveryFrom,graph.root.deliveryTo)}`),el('strong',`${graph.root.value} ${unit(graph.root.quote)}`));
  if(single){const description=el('p',pricingEquation(graph.root,graph));description.className='field-help';target.replaceChildren(summary,description);return;}
  const ns='http://www.w3.org/2000/svg',svg=document.createElementNS(ns,'svg');
  const levels=Math.max(...graph.depth.values())+1,counts=Array.from({length:levels},(_,i)=>graph.nodes.filter(n=>graph.depth.get(n.id)===i));
  const nodeWidth=220,step=246;const width=levels*step+30,height=Math.max(1,...counts.map(g=>g.length))*130+35;
  svg.setAttribute('viewBox',`0 0 ${width} ${height}`);svg.style.minWidth=`${width}px`;svg.setAttribute('role','img');
  svg.setAttribute('aria-label',publicText(`Source contracts leading to ${curveName(graph.root.curve)}. Exact values appear in the tables below.`));
  function shape(tag:string,attrs:Record<string,string>,value?:string,parent:Element=svg){const node=document.createElementNS(ns,tag);Object.entries(attrs).forEach(([k,v])=>node.setAttribute(k,v));if(value!==undefined)node.textContent=publicText(value);parent.append(node);return node;}
  const positions=new Map<string,{x:number;y:number}>();
  counts.forEach((group,level)=>group.forEach((node,i)=>positions.set(node.id,{x:12+level*step,y:20+(height-40-group.length*130)/2+i*130})));
  for(const node of graph.nodes){const end=positions.get(node.id)!;for(const input of node.inputs){const start=positions.get(input.id)!;const x=start.x+nodeWidth,y=start.y+59,ex=end.x,ey=end.y+59;
    shape('path',{d:`M ${x} ${y} C ${x+22} ${y}, ${ex-22} ${ey}, ${ex-7} ${ey}`,class:'flow-edge '+input.slot});
    shape('path',{d:`M ${ex-12} ${ey-4} L ${ex-5} ${ey} L ${ex-12} ${ey+4}`,class:'flow-arrow '+input.slot});
}}
  graph.nodes.forEach((node,index)=>{const p=positions.get(node.id)!,g=shape('g',{});const isOutput=node.id===graph.root.id;
    shape('rect',{x:String(p.x),y:String(p.y),width:String(nodeWidth),height:'119',rx:'8',class:'flow-node'+(isOutput?' output':'')},undefined,g);
    shape('text',{x:String(p.x+12),y:String(p.y+19),class:'flow-step'},`${index+1} · ${node.rule==='direct'?(node.use==='frozen'?'Final settlement':'Source price'):isOutput?'Selected contract':'Calculated price'}`,g);
    shape('text',{x:String(p.x+12),y:String(p.y+40),class:'flow-name'},curveName(node.curve),g);
    shape('text',{x:String(p.x+12),y:String(p.y+57),class:'flow-date'},deliveryLabel(node.deliveryFrom,node.deliveryTo),g);
    shape('text',{x:String(p.x+12),y:String(p.y+80),class:'flow-value'},node.value,g);
    shape('text',{x:String(p.x+12),y:String(p.y+100),class:'flow-unit'},unit(node.quote),g);
    shape('title',{},`${curveName(node.curve)} · ${displayDate(node.deliveryFrom)} to ${displayDate(node.deliveryTo)} (end excluded): ${pricingEquation(node,graph)}; ${node.value} ${unit(node.quote)}`,g);
    if(node.multiplier)shape('text',{x:String(p.x+nodeWidth-5),y:String(p.y+17),'text-anchor':'end',class:'flow-multiplier'},`× ${node.multiplier.value}`,g);
  });
  const legend=el('div');legend.className='flow-legend';for(const [label,kind] of [['Base price','base'],['Spread','spread']]){const item=el('span',label);item.className=kind;legend.append(item);}
  const help=el('p','Arrows show the exact base and spread contracts used. Conversion factors appear on the calculation that uses them.');help.className='field-help';target.replaceChildren(summary,legend,svg,help);
}
