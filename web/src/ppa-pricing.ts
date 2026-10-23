import {exactText,type Exact,type Reference} from './ppa-view.js';
export type Price=Exact&{unit:{currency:Reference;quantity:Reference;multiplier:string}};
export type PricingPeriod={sideRole?:string;slot:{role:number;ordinal:number};month:string;kind:string;quantityState:string;economicClass:string;coverage:string;valueAt:string;coefficient:string;quantity:Exact&{specification:Reference};amount:Exact;presentValue:Exact|null;effectivePrice:Price|null};
export type PricingResult={label:string;measure:string;measureDefinition:string;price:Price;sensitivity:Exact;inputValue:Exact;quote:string;quoteResidual:Exact;quoteIsExact:boolean;exactPriceEnterable:boolean;quantity:string;coverage:{from:string;to:string};profileDefinition:string;profiles:{periods:PricingPeriod[];valueCurve:{label:string;strike:Price;value:Exact}[]};receipt:unknown;exclusions:unknown;assumptions:unknown};
type Point={x:number|null;label:string;value:Exact|null};
type Series={name:string;points:Point[]};
// Conversion positions pixels only. Exact server values remain attached to every
// point and in drill-down tables. Unsupported plotting magnitudes leave a gap.
export function plotNumber(value:Exact|null):number|null{
 if(!value)return null;let n=Number(value.numerator)/Number(value.denominator);
 if(!Number.isFinite(n)){const negative=value.numerator.startsWith('-'),a=value.numerator.replace('-',''),b=value.denominator;
  n=(negative?-1:1)*(Number(a.slice(0,16))/10**Math.min(16,a.length))/(Number(b.slice(0,16))/10**Math.min(16,b.length))*10**(a.length-b.length);}
 return Number.isFinite(n)?n:null;
}
export function plotBounds(values:number[]):[number,number]{let lo=Math.min(...values),hi=Math.max(...values);if(lo===hi){const padding=Math.max(1,Math.abs(lo)*0.05);lo-=padding;hi+=padding;}return [lo,hi];}
export function plotCoordinate(v:number,lo:number,hi:number,width:number):number{const scale=Math.max(1,Math.abs(lo),Math.abs(hi));return ((v/scale-lo/scale)/(hi/scale-lo/scale))*width;}
export function pricingSeries(periods:PricingPeriod[],field:'quantity'|'amount'|'effectivePrice'):Series[]{
 const groups=new Map<string,Series>();
 for(const p of periods){const name=`${p.sideRole??p.kind} ${p.slot.ordinal} · ${p.quantityState} · ${p.economicClass} · ${p.coverage}`;
  let group=groups.get(name);if(!group){group={name,points:[]};groups.set(name,group);}group.points.push({x:Date.parse(p.month+'T00:00:00Z'),label:p.month,value:p[field]});}
 return [...groups.values()];
}
const colors=['#256b52','#416da5','#bf7142','#9671ad','#9a8530','#4b8996'];
function chart(host:HTMLElement,title:string,unit:string,series:Series[],numericX=false){
 const card=document.createElement('section');card.className='ppa-plot';const heading=document.createElement('h4');heading.textContent=title;card.append(heading);
 const ns='http://www.w3.org/2000/svg',svg=document.createElementNS(ns,'svg');svg.setAttribute('viewBox','0 0 680 300');svg.setAttribute('role','img');svg.setAttribute('aria-label',title+' · '+unit);card.append(svg);
 const points=series.flatMap(s=>s.points.map(p=>({...p,y:plotNumber(p.value)}))).filter((p):p is Point&{x:number;y:number}=>p.y!==null&&p.x!==null&&Number.isFinite(p.x));
 if(!points.length){const note=document.createElement('p');note.textContent='No finite plot coordinates. Exact values remain below.';card.append(note);host.append(card);return;}
 const [xmin,xmax]=plotBounds(points.map(p=>p.x)),[ymin,ymax]=plotBounds([0,...points.map(p=>p.y)]);
 const x=(v:number)=>70+plotCoordinate(v,xmin,xmax,540),y=(v:number)=>240-plotCoordinate(v,ymin,ymax,190);
 function el(name:string,attrs:Record<string,string|number>,text?:string){const n=document.createElementNS(ns,name);for(const [key,value] of Object.entries(attrs))n.setAttribute(key,String(value));if(text)n.textContent=text;svg.append(n);return n;}
 el('path',{d:'M70 50 V240 H610',fill:'none',stroke:'#9baaa0'});el('line',{x1:70,y1:y(0),x2:610,y2:y(0),stroke:'#bdc8be','stroke-dasharray':'4 4'});
 el('text',{x:70,y:24,'font-size':13},unit+' · approximate axes');
 for(const v of [ymin,ymax])el('text',{x:65,y:y(v)+4,'text-anchor':'end','font-size':11},v.toPrecision(4));
 const xlabel=(v:number)=>numericX?v.toPrecision(5):new Date(v).toISOString().slice(0,7);
 for(const v of [points[0].x,points.at(-1)!.x])el('text',{x:x(v),y:267,'text-anchor':'middle','font-size':11},xlabel(v));
 series.forEach((s,i)=>{const color=colors[i%colors.length];let path='';for(const p of s.points){const v=plotNumber(p.value);if(v===null||p.x===null||!Number.isFinite(p.x)){path='';continue;}const px=x(p.x),py=y(v);if(!Number.isFinite(px)||!Number.isFinite(py)){path='';continue;}if(path)el('line',{x1:path.split(',')[0],y1:path.split(',')[1],x2:px,y2:py,stroke:color,'stroke-width':2});path=`${px},${py}`;
   const circle=el('circle',{cx:px,cy:py,r:p.label.startsWith('Model break-even')?7:4,fill:color,tabindex:0});const tooltip=document.createElementNS(ns,'title');tooltip.textContent=`${s.name} · ${p.label}: ${exactText(p.value)} ${unit}`;circle.append(tooltip);}
 });
 const legend=document.createElement('p');legend.className='field-help';legend.textContent=series.map(s=>s.name).join(' • ');card.append(legend);host.append(card);
}
function table(host:HTMLElement,headers:string[],rows:string[][]){const wrap=document.createElement('div');wrap.className='table-wrap';const t=document.createElement('table');const head=document.createElement('thead'),body=document.createElement('tbody'),tr=document.createElement('tr');for(const h of headers){const th=document.createElement('th');th.textContent=h;th.scope='col';tr.append(th);}head.append(tr);for(const row of rows){const tr=document.createElement('tr');for(const v of row){const td=document.createElement('td');td.textContent=v;tr.append(td);}body.append(tr);}t.append(head,body);wrap.append(t);host.append(wrap);}
export function renderPricing(host:HTMLElement,p:PricingResult,quantityLabel:(r:Reference)=>string){
 host.replaceChildren();const title=document.createElement('h3');title.textContent=p.label;host.append(title);
 const currency=p.price.unit.currency.code.toUpperCase(),quoteUnit=`${currency} / ${quantityLabel(p.price.unit.quantity)} × ${p.price.unit.multiplier}`;
 const note=document.createElement('p');note.textContent=`Unbooked draft · ${p.quantity} MW · ${p.coverage.from} to ${p.coverage.to} exclusive. ${p.measure}: ${p.measureDefinition} All other terms and inputs held fixed. No commercial risk premium. No trade is changed.`;host.append(note);
 table(host,['Result','Exact server value'],[['Model break-even price',exactText(p.price)+' '+quoteUnit],['Sensitivity per +1 quote unit',exactText(p.sensitivity)+' '+currency],['Entered-strike contract value',exactText(p.inputValue)+' '+currency],['Explicit rounded quote',p.quote+' '+quoteUnit],['Rounded-quote residual',exactText(p.quoteResidual)+' '+currency],['Exact root enterable at 18 decimals',String(p.exactPriceEnterable)],['Quote equals exact root',String(p.quoteIsExact)]]);
 host.querySelector('.table-wrap')!.classList.add('ppa-pricing-summary');
 const help=document.createElement('p');help.textContent=p.profileDefinition+' Quantity states distinguish Forecast, Metered, Contractual and Estimated. Quantity and economic classifications do not classify price source finality. Market profiles are model values; native preview detail retains actual, estimated and projected price evidence. Positive contractual cash is received; negative cash is paid.';host.append(help);
 const plots=document.createElement('div');plots.className='ppa-plots';host.append(plots);
 const periods=p.profiles.periods;
 // Separate exact quantity units before plotting. Never aggregate legs in the browser.
 for(const unit of [...new Set(periods.map(r=>r.quantity.specification.code))]){
  const selected=periods.filter(r=>r.quantity.specification.code===unit),label=quantityLabel(selected[0].quantity.specification);
  chart(plots,selected.every(r=>[1,4,5].includes(r.slot.role))?'Monthly energy volumes · each side':'Monthly quantities · each side',label,pricingSeries(selected,'quantity'));
  chart(plots,'Market and strike profiles · effective weighted value',currency+' / '+label,pricingSeries(selected,'effectivePrice'));
 }
 chart(plots,'Gross contractual receipts / payments',currency,pricingSeries(periods.filter(r=>r.kind==='Payment'),'amount'));
 chart(plots,'Gross physical energy value · separate from cash',currency,pricingSeries(periods.filter(r=>r.kind==='Delivery'&&r.slot.role===1),'amount'));
 const curve=p.profiles.valueCurve;chart(plots,'Contract value versus strike · model break-even marked',currency,[{name:p.measure,points:curve.map(c=>({x:plotNumber(c.strike),label:c.label+' · strike '+exactText(c.strike),value:c.value}))}],true);
 // A separate labelled point guarantees the exact root is visible even when the entered strike overlaps it.
 const mark=document.createElement('p');mark.className='ppa-root-marker';mark.textContent='Model break-even point: '+exactText(p.price)+' '+quoteUnit+' → 0 '+currency;host.append(mark);
 const details=document.createElement('details');const summary=document.createElement('summary');summary.textContent='Exact monthly values and classifications';details.append(summary);host.append(details);
 table(details,['Month','Side','Quantity state','Class / coverage','Quantity','Effective price','Gross amount','PV','Payment / value at'],periods.map(r=>[r.month,`${r.sideRole??r.kind} ${r.slot.ordinal}`,r.quantityState,r.economicClass+' / '+r.coverage,exactText(r.quantity)+' '+quantityLabel(r.quantity.specification),exactText(r.effectivePrice),exactText(r.amount),exactText(r.presentValue),r.valueAt]));
 table(details,['Strike point','Exact strike','Selected value'],curve.map(c=>[c.label,exactText(c.strike),exactText(c.value)]));
 const receipt=document.createElement('details'),summary2=document.createElement('summary'),pre=document.createElement('pre');summary2.textContent='Complete input receipt, assumptions and exclusions';pre.className='ppa-evidence';pre.textContent=JSON.stringify({receipt:p.receipt,assumptions:p.assumptions,exclusions:p.exclusions},null,2);receipt.append(summary2,pre);host.append(receipt);
}
