import {type CurveResult,type Point,deliveryLabel,displayDate,pointDate,publicText} from './market-view.js';
import {type PricingNode,pricingGraph,pricingEquation} from './pricing-view.js';

export type WorkbookRow={id:string;cells:string[];formula:string};
export type WorkbookTable={id:string;title:string;subtitle:string;columns:string[];rows:WorkbookRow[];column:number;output:boolean};
export type WorkbookLink={source:string;target:string};
export type WorkbookConnection={source:string;target:string;label:string;kind:'base'|'spread';uniform:boolean;links:WorkbookLink[]};
export type CurveWorkbook={tables:WorkbookTable[];connections:WorkbookConnection[]};
const unavailable=()=>Error('The table connections are missing or inconsistent. Refresh and try again.');

export function contractPeriod(from:string,to:string){
  return deliveryLabel(from,to);
}

export function traceWorkbook(selected:string,links:WorkbookLink[]){
  const rows=new Set<string>([selected]),edges=new Set<string>();
  function walk(reverse:boolean){const seen=new Set<string>(),queue=[selected];while(queue.length){const id=queue.pop()!;if(seen.has(id))continue;seen.add(id);
    for(const link of links)if((reverse?link.target:link.source)===id){edges.add(JSON.stringify([link.source,link.target]));const next=reverse?link.source:link.target;rows.add(next);queue.push(next);}}}
  walk(true);walk(false);return {rows,edges};
}

// Group evaluated rows into curve tables. Compression is visual only: every
// actual source/target row pair is retained for inspection and highlighting.
export function curveWorkbook(result:CurveResult,quoteLabel:(code:string)=>string,curveName:(code:string)=>string):CurveWorkbook {
  if(result.kind==='rates'||!result.points.length)throw unavailable();
  const graphs=result.points.map(p=>pricingGraph(result,p));
  const nodes=new Map<string,PricingNode>(),formulas=new Map<string,string>();
  for(const graph of graphs)for(const node of graph.nodes){nodes.set(node.id,node);formulas.set(node.id,pricingEquation(node,graph));}
  const roots=new Set(graphs.map(g=>g.root.id));
  const grouped=new Map<string,PricingNode[]>();
  for(const node of nodes.values()){const key=JSON.stringify([node.curve,node.quote]);if(!grouped.has(key))grouped.set(key,[]);grouped.get(key)!.push(node);}
  const tableFor=new Map<string,string>(),rowsFor=new Map<string,PricingNode[]>();
  const tables:WorkbookTable[]=[];
  for(const rows of grouped.values()){
    rows.sort((a,b)=>a.deliveryFrom.localeCompare(b.deliveryFrom)||a.deliveryTo.localeCompare(b.deliveryTo)||a.id.localeCompare(b.id));
    const id='sheet_'+tables.length,first=rows[0],hasFactor=rows.some(n=>n.multiplier!==null);
    const rules=new Set(rows.map(n=>n.rule)),rule=rules.size===1?first.rule:'mixed';
    const description=rule==='direct'?'Supplied prices':rule==='add'?'Base + spread':rule==='scale_add'?'Base × factor + spread':'Varies by contract';
    tables.push({id,title:curveName(first.curve),subtitle:`${quoteLabel(first.quote)} · ${description}`,
      columns:['Contract','Price',...(hasFactor?['Factor']:[])],column:0,output:rows.some(n=>roots.has(n.id)),
      rows:rows.map(n=>({id:n.id,cells:[`${contractPeriod(n.deliveryFrom,n.deliveryTo)}${n.use==='frozen'?' · Final':''}`,n.value,...(hasFactor?[n.multiplier?.value??'—']:[])],formula:formulas.get(n.id)!}))});
    const labels=new Map<string,number>();for(const row of tables.at(-1)!.rows){const label=row.cells[0];labels.set(label,(labels.get(label)??0)+1);}
    const counts=new Map<string,number>();for(const row of tables.at(-1)!.rows){const label=row.cells[0],index=(counts.get(label)??0)+1;counts.set(label,index);if(labels.get(label)!>1)row.cells[0]=`${label} · contract ${index}`;}
    rowsFor.set(id,rows);for(const row of rows)tableFor.set(row.id,id);
  }
  const groups=new Map<string,WorkbookConnection>();
  for(const target of nodes.values())for(const input of target.inputs){
    const source=tableFor.get(input.id)!,destination=tableFor.get(target.id)!;
    if(source===destination)throw unavailable();
    const key=JSON.stringify([source,destination,input.slot]);
    if(!groups.has(key))groups.set(key,{source,target:destination,label:input.slot==='base'?'Base':'Spread',kind:input.slot,uniform:false,links:[]});
    groups.get(key)!.links.push({source:input.id,target:target.id});
  }
  function logic(node:PricingNode){return JSON.stringify({rule:node.rule,use:node.use,multiplier:node.multiplier,
    inputs:node.inputs.map(i=>{const n=nodes.get(i.id)!;return {slot:i.slot,table:tableFor.get(i.id),use:n.use,source:n.source,role:n.role};}).sort((a,b)=>a.slot.localeCompare(b.slot))});}
  for(const connection of groups.values()){
    const source=rowsFor.get(connection.source)!,target=rowsFor.get(connection.target)!;
    connection.uniform=connection.links.length===source.length&&connection.links.length===target.length&&
      new Set(connection.links.map(l=>l.source)).size===source.length&&new Set(connection.links.map(l=>l.target)).size===target.length&&
      new Set(target.map(logic)).size===1&&connection.links.every(l=>{const a=nodes.get(l.source)!,b=nodes.get(l.target)!;
        return a.deliveryFrom===b.deliveryFrom&&a.deliveryTo===b.deliveryTo;});
  }
  // Place each source just before its furthest consumer. This keeps spread
  // tables near their calculation instead of stacking every leaf in column A.
  const distance=new Map<string,number>();
  function place(id:string,value:number,path:Set<string>){if(path.has(id))throw unavailable();if((distance.get(id)??-1)>=value)return;
    distance.set(id,value);const next=new Set(path).add(id);for(const edge of groups.values())if(edge.target===id)place(edge.source,value+1,next);}
  for(const table of tables)if(table.output)place(table.id,0,new Set());
  const maximum=Math.max(...distance.values());
  for(const table of tables){if(!distance.has(table.id))throw unavailable();table.column=maximum-distance.get(table.id)!;}
  return {tables,connections:[...groups.values()]};
}

export function rateWorkbook(result:CurveResult):CurveWorkbook {
  if(result.kind!=='rates'||result.numericalPolicy!=='simple_actual365fixed_rational_to_even_18_v1'||!result.points.length||!result.convention)throw unavailable();
  const convention=result.convention;
  const points=result.points;
  const formula=(p:Point)=>{if(!p.seriesId||p.annualRate===undefined||p.actualDays===undefined||p.discountFactor===undefined)throw unavailable();
    return `1 ÷ (1 + ${p.annualRate} × ${p.actualDays} / 365) = ${p.discountFactor}`;};
  points.forEach(formula);
  const table=(id:string,title:string,subtitle:string,columns:string[],value:(p:Point)=>string,column:number,output=false):WorkbookTable=>({id,title,subtitle,columns,column,output,
    rows:points.map(p=>({id:id+'_'+p.seriesId,cells:[displayDate(pointDate(p)),value(p)],formula:formula(p)}))});
  const tables=[table('rates','Zero rates','Annual rate (%)',['Maturity','Rate (%)'],p=>p.value,0),
    table('days','Time to maturity',convention.dayCount,['Maturity','Actual days'],p=>String(p.actualDays),0),
    table('discounts','Discount factors',convention.compounding,['Maturity','Discount factor'],p=>p.discountFactor!,1,true)];
  return {tables,connections:['rates','days'].map((id,i)=>({source:id,target:'discounts',label:i===0?'Rate':'Time',kind:i===0?'base':'spread',uniform:true,
    links:points.map(p=>({source:id+'_'+p.seriesId,target:'discounts_'+p.seriesId}))}))};
}

export function renderWorkbook(host:HTMLElement,workbook:CurveWorkbook){
  const node=(tag:string,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=publicText(text);return n;};
  const connected=workbook.connections.length>0;
  host.className='workbook-view'+(connected?'':' workbook-simple');
  if(!workbook.tables.length){host.replaceChildren(node('p','No price sources to display.'));return;}
  const columns=Math.max(...workbook.tables.map(t=>t.column))+1,gap=108,rowHeight=32,headerHeight=104;
  const widths=Array.from({length:columns},(_,column)=>Math.max(252,...workbook.tables.filter(t=>t.column===column).map(t=>Math.max(t.columns.length*98+28,...t.rows.map(r=>r.cells.reduce((n,c)=>n+Math.max(76,c.length*7+22),28))))));
  const xs=widths.map((_,i)=>18+widths.slice(0,i).reduce((n,w)=>n+w+gap,0));
  const positions=new Map<string,{x:number;y:number;width:number;height:number}>(),rowPositions=new Map<string,{x:number;y:number;width:number}>();
  let height=0;
  for(let column=0;column<columns;column++){let y=20;for(const table of workbook.tables.filter(t=>t.column===column)){
    const width=widths[column],h=headerHeight+table.rows.length*rowHeight+1;positions.set(table.id,{x:xs[column],y,width,height:h});
    table.rows.forEach((r,i)=>rowPositions.set(r.id,{x:xs[column],y:y+headerHeight+i*rowHeight+rowHeight/2,width}));y+=h+36;}height=Math.max(height,y);}
  const canvas=node('div');canvas.className=connected?'workbook-canvas':'workbook-sheets';if(connected){canvas.style.width=(xs.at(-1)!+widths.at(-1)!+18)+'px';canvas.style.height=height+'px';}
  const ns='http://www.w3.org/2000/svg',svg=document.createElementNS(ns,'svg');if(connected){svg.classList.add('workbook-wires');svg.setAttribute('width',canvas.style.width);svg.setAttribute('height',String(height));svg.setAttribute('aria-hidden','true');canvas.append(svg);}
  const wireElements:{element:SVGElement;links:WorkbookLink[]}[]=[],rowElements=new Map<string,HTMLElement>(),buttons=new Map<string,HTMLButtonElement>();
  const allLinks=workbook.connections.flatMap(c=>c.links);
  function trace(id:string){const active=traceWorkbook(id,allLinks);
    for(const [key,row] of rowElements)row.classList.toggle('connected',active.rows.has(key));for(const [key,button] of buttons)button.setAttribute('aria-pressed',String(key===id));
    for(const wire of wireElements)wire.element.classList.toggle('active',wire.links.some(l=>active.edges.has(JSON.stringify([l.source,l.target]))));}

  for(const connection of workbook.connections){const source=positions.get(connection.source)!,target=positions.get(connection.target)!;
    const links=connection.uniform?[connection.links]:connection.links.map(l=>[l]);
    links.forEach((members,index)=>{
      const a=connection.uniform?{x:source.x,y:source.y+(connection.kind==='base'?30:48),width:source.width}:rowPositions.get(members[0].source)!;
      const b=connection.uniform?{x:target.x,y:target.y+(connection.kind==='base'?30:48),width:target.width}:rowPositions.get(members[0].target)!;
      const start=a.x+a.width,end=b.x,lane=start+20+(connection.kind==='base'?0:28)+(index%7)*7;
      const path=document.createElementNS(ns,'path');path.setAttribute('d',`M ${start} ${a.y} H ${lane} V ${b.y} H ${end-7}`);path.setAttribute('class',`sheet-connection ${connection.kind} ${connection.uniform?'uniform':'individual'}`);
      const title=document.createElementNS(ns,'title');title.textContent=publicText(`${connection.label}: ${connection.uniform?'same rule and exact period mapping for '+members.length+' contracts':'individual contract mapping'}`);path.append(title);svg.append(path);wireElements.push({element:path,links:members});
      const arrow=document.createElementNS(ns,'path');arrow.setAttribute('d',`M ${end-12} ${b.y-4} L ${end-5} ${b.y} L ${end-12} ${b.y+4}`);arrow.setAttribute('class',`sheet-connection ${connection.kind}`);svg.append(arrow);wireElements.push({element:arrow,links:members});
      if(connection.uniform){const text=document.createElementNS(ns,'text');text.setAttribute('x',String(start+8));text.setAttribute('y',String(a.y-7));text.textContent=connection.label;svg.append(text);}
    });
  }
  for(const table of workbook.tables){const p=positions.get(table.id)!,panel=node('section');panel.className='workbook-table'+(table.output?' output':'');if(connected){panel.style.left=p.x+'px';panel.style.top=p.y+'px';panel.style.width=p.width+'px';}panel.setAttribute('aria-label',publicText(table.title));
    const caption=node('div');caption.className='sheet-caption';caption.append(node('strong',table.title),node('small',table.subtitle));panel.append(caption);
    const grid=node('table'),head=node('thead'),heading=node('tr');heading.append(node('th','#'));
    table.columns.forEach((name,i)=>{const th=node('th'),letter=node('small',String.fromCharCode(65+i));th.append(letter,node('span',name));heading.append(th);});head.append(heading);grid.append(head);
    const body=node('tbody');table.rows.forEach((row,i)=>{const tr=node('tr');tr.title=publicText(row.formula);tr.append(node('th',String(i+1)));
      row.cells.forEach((value,j)=>{const td=node('td');if(j===0&&connected){const button=node('button',value) as HTMLButtonElement;button.type='button';button.setAttribute('aria-pressed','false');button.setAttribute('aria-label',publicText(`Trace ${value} through ${table.title}`));button.onclick=()=>trace(row.id);buttons.set(row.id,button);td.append(button);}else td.textContent=publicText(value);tr.append(td);});body.append(tr);rowElements.set(row.id,tr);});grid.append(body);panel.append(grid);canvas.append(panel);
  }
  const hint=node('p','Select a contract row to trace its connections. One line groups identical rules and period mappings; individual lines show exceptions.');hint.className='field-help';
  host.replaceChildren(...(connected?[canvas,hint]:[canvas]));
}
