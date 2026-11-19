// Drawings of a gas storage contract: its ratchets as daily limits against inventory, and a plan's inventory path with its
// daily flows. Every figure drawn is one the page was given; numbers are parsed here only to place marks, never to decide anything.
// Injection is the chart palette's blue, drawn solid; withdrawal its orange, drawn dashed, so the pair never rests on colour alone.
export type RatchetBand={lower:string;upper:string;maximumDailyInjection:string;maximumDailyWithdrawal:string};
export type RatchetDrawing={bands:readonly RatchetBand[];unit:string;opening:string|null;terminal:readonly [string,string]|null};
export type PathDay={gasDay:string;injection:string;withdrawal:string;opening:string;closing:string;band:number};
export type PathDrawing={days:readonly PathDay[];minimum:string;maximum:string;edges:readonly string[];unit:string};

const NS='http://www.w3.org/2000/svg',W=640,left=64,right=92,injection='#5893ee',withdrawal='#e26d28';
const number=(text:string)=>{const value=Number(text);if(!Number.isFinite(value))throw Error('A storage figure could not be drawn.');return value;};
const short=(value:number)=>Math.abs(value)>=1e6?`${+(value/1e6).toFixed(2)}M`:Math.abs(value)>=1e4?`${+(value/1e3).toFixed(1)}k`:`${+value.toFixed(2)}`;
function node<K extends keyof SVGElementTagNameMap>(tag:K,attributes:Record<string,string|number>,text?:string){
  const element=document.createElementNS(NS,tag);for(const [key,value] of Object.entries(attributes))element.setAttribute(key,String(value));
  if(text!==undefined)element.textContent=text;return element;
}
const label=(x:number,y:number,text:string,anchor='start',tone='var(--text-3)')=>node('text',{x,y,'text-anchor':anchor,fill:tone,'font-size':11},text);
function frame(host:HTMLElement,height:number,title:string){
  const svg=node('svg',{viewBox:`0 0 ${W} ${height}`,role:'img','aria-label':title,class:'storage-chart'});
  const tip=document.createElement('div');tip.className='storage-chart-tip';tip.hidden=true;
  host.classList.add('storage-chart-host');host.replaceChildren(svg,tip);
  const show=(event:MouseEvent,lines:string[])=>{
    tip.replaceChildren(...lines.map(line=>{const row=document.createElement('div');row.textContent=line;return row;}));tip.hidden=false;
    const box=host.getBoundingClientRect();tip.style.left=`${Math.min(event.clientX-box.left+12,box.width-190)}px`;tip.style.top=`${event.clientY-box.top+12}px`;
  };
  return {svg,show,hide:()=>{tip.hidden=true;}};
}
function legend(svg:SVGSVGElement,y:number){
  for(const [x,name,color,dash] of [[left,'Injection limit',injection,''],[left+130,'Withdrawal limit',withdrawal,'6 4']] as const){
    svg.append(node('line',{x1:x,x2:x+22,y1:y-4,y2:y-4,stroke:color,'stroke-width':2,'stroke-dasharray':dash}),label(x+28,y,name,'start','var(--text-2)'));
  }
}

/** Daily injection and withdrawal limits against inventory: one step per ratchet band, with the opening and terminal inventory marked. */
export function drawRatchets(host:HTMLElement,drawing:RatchetDrawing){
  const bands=drawing.bands.map(b=>({lower:number(b.lower),upper:number(b.upper),inject:number(b.maximumDailyInjection),withdraw:number(b.maximumDailyWithdrawal)}));
  if(!bands.length||bands.some(b=>!(b.upper>b.lower))){host.replaceChildren();host.classList.remove('storage-chart-host');return;}
  const H=230,top=34,bottom=H-40,low=bands[0].lower,high=bands[bands.length-1].upper,peak=Math.max(...bands.flatMap(b=>[b.inject,b.withdraw]),1);
  const x=(value:number)=>left+(value-low)/(high-low)*(W-left-right),y=(value:number)=>bottom-value/peak*(bottom-top);
  const {svg,show,hide}=frame(host,H,`Daily injection and withdrawal limits by inventory, in ${drawing.unit}`);
  legend(svg,16);
  for(const tick of [0,peak/2,peak])svg.append(node('line',{x1:left,x2:W-right,y1:y(tick),y2:y(tick),stroke:'var(--border)','stroke-width':1}),label(left-8,y(tick)+4,short(tick),'end'));
  svg.append(label(left,H-6,`Inventory · ${drawing.unit}`),label(left-8,top-10,`${drawing.unit}/day`,'end'));
  for(const edge of [low,...bands.map(b=>b.upper)])svg.append(node('line',{x1:x(edge),x2:x(edge),y1:bottom,y2:bottom+5,stroke:'var(--border-strong)'}),label(x(edge),bottom+18,short(edge),'middle'));
  const steps=(pick:(b:typeof bands[number])=>number)=>bands.map((b,i)=>`${i?'L':'M'}${x(b.lower)},${y(pick(b))} L${x(b.upper)},${y(pick(b))}`).join(' ');
  svg.append(node('path',{d:steps(b=>b.inject),fill:'none',stroke:injection,'stroke-width':2,'stroke-linejoin':'round'}),
    node('path',{d:steps(b=>b.withdraw),fill:'none',stroke:withdrawal,'stroke-width':2,'stroke-dasharray':'6 4','stroke-linejoin':'round'}));
  const last=bands[bands.length-1],apart=Math.abs(y(last.inject)-y(last.withdraw))<13?(last.inject>=last.withdraw?-7:7):0;
  svg.append(label(W-right+8,y(last.inject)+4+apart,`In ${short(last.inject)}`,'start','var(--text-2)'),label(W-right+8,y(last.withdraw)+4-apart,`Out ${short(last.withdraw)}`,'start','var(--text-2)'));
  if(drawing.terminal){
    const [from,to]=drawing.terminal.map(number);
    if(from>=low&&to<=high&&to>=from)svg.append(node('rect',{x:x(from),y:bottom-3,width:Math.max(x(to)-x(from),2),height:6,rx:2,fill:'var(--text-3)'}),label((x(from)+x(to))/2,bottom-8,'Terminal','middle'));
  }
  if(drawing.opening!==null){
    const at=number(drawing.opening);
    if(at>=low&&at<=high)svg.append(node('line',{x1:x(at),x2:x(at),y1:top,y2:bottom,stroke:'var(--text-2)','stroke-width':1,'stroke-dasharray':'2 3'}),label(x(at),top-4,`Opening ${short(at)}`,'middle','var(--text-2)'));
  }
  // Each band is one hover target the full height of the plot.
  for(const b of bands){
    const target=node('rect',{x:x(b.lower),y:top,width:x(b.upper)-x(b.lower),height:bottom-top,fill:'transparent'});
    target.addEventListener('mousemove',event=>show(event,[`Inventory ${short(b.lower)} to ${short(b.upper)} ${drawing.unit}`,`Inject up to ${short(b.inject)} a day`,`Withdraw up to ${short(b.withdraw)} a day`]));
    target.addEventListener('mouseleave',hide);svg.append(target);
  }
}

/** A plan's inventory through the term against the contracted range and band edges, and beneath it each gas day's flow. */
export function drawPath(host:HTMLElement,drawing:PathDrawing){
  const days=drawing.days.map(d=>({day:d.gasDay,opening:number(d.opening),closing:number(d.closing),inject:number(d.injection),withdraw:number(d.withdrawal),band:d.band}));
  if(!days.length){host.replaceChildren();host.classList.remove('storage-chart-host');return;}
  const low=number(drawing.minimum),high=number(drawing.maximum),levels=[days[0].opening,...days.map(d=>d.closing)];
  // The inventory panel is scaled to the path with a margin, inside the contracted range, so a small plan in a large contract stays readable.
  const span=Math.max(Math.max(...levels)-Math.min(...levels),(high-low)*0.02),from=Math.max(low,Math.min(...levels)-span*0.25),to=Math.min(high,Math.max(...levels)+span*0.25);
  const H=330,top=30,middle=190,flowTop=226,flowBottom=H-34,flowZero=(flowTop+flowBottom)/2,peak=Math.max(...days.flatMap(d=>[d.inject,d.withdraw]),1);
  const step=(W-left-right)/days.length,x=(index:number)=>left+index*step,y=(value:number)=>middle-(value-from)/(to-from||1)*(middle-top);
  const {svg,show,hide}=frame(host,H,`Planned inventory and daily flows, in ${drawing.unit}`);
  svg.append(label(left,16,`Inventory · ${drawing.unit}`,'start','var(--text-2)'),label(left,flowTop-10,`Daily flow · ${drawing.unit}/day`,'start','var(--text-2)'));
  for(const tick of [from,(from+to)/2,to])svg.append(node('line',{x1:left,x2:W-right,y1:y(tick),y2:y(tick),stroke:'var(--border)'}),label(left-8,y(tick)+4,short(tick),'end'));
  for(const [edge,name] of [[low,'Minimum'],[high,'Capacity'],...drawing.edges.map(number).filter(e=>e>low&&e<high).map(e=>[e,'Ratchet'] as [number,string])] as [number,string][])
    if(edge>=from&&edge<=to)svg.append(node('line',{x1:left,x2:W-right,y1:y(edge),y2:y(edge),stroke:'var(--border-strong)','stroke-dasharray':'4 4'}),label(W-right+8,y(edge)+4,`${name} ${short(edge)}`));
  const line=levels.map((level,i)=>`${i?'L':'M'}${x(i)},${y(level)}`).join(' ');
  svg.append(node('path',{d:line,fill:'none',stroke:'var(--accent-text)','stroke-width':2,'stroke-linejoin':'round'}),
    node('circle',{cx:x(levels.length-1),cy:y(levels[levels.length-1]),r:4,fill:'var(--accent-text)',stroke:'var(--surface)','stroke-width':2}),
    label(Math.min(x(levels.length-1)+8,W-right+8),y(levels[levels.length-1])-8,`Ends ${short(levels[levels.length-1])}`,'start','var(--text-2)'));
  svg.append(node('line',{x1:left,x2:W-right,y1:flowZero,y2:flowZero,stroke:'var(--border-strong)'}),label(left-8,flowTop+8,`In ${short(peak)}`,'end'),label(left-8,flowBottom,`Out ${short(peak)}`,'end'));
  const width=Math.max(Math.min(step-2,18),1),reach=(flowZero-flowTop-2);
  days.forEach((d,i)=>{
    const centre=x(i)+step/2;
    if(d.inject>0)svg.append(node('rect',{x:centre-width/2,y:flowZero-d.inject/peak*reach,width,height:d.inject/peak*reach,rx:Math.min(2,width/2),fill:injection}));
    if(d.withdraw>0)svg.append(node('rect',{x:centre-width/2,y:flowZero,width,height:d.withdraw/peak*reach,rx:Math.min(2,width/2),fill:withdrawal}));
  });
  for(const index of days.length<=2?[0,days.length-1]:[0,Math.floor(days.length/2),days.length-1])svg.append(label(x(index)+step/2,H-10,days[index].day,'middle'));
  svg.append(node('rect',{x:W-right+8,y:flowTop,width:10,height:10,rx:2,fill:injection}),label(W-right+22,flowTop+9,'Injected','start','var(--text-2)'),
    node('rect',{x:W-right+8,y:flowTop+18,width:10,height:10,rx:2,fill:withdrawal}),label(W-right+22,flowTop+27,'Withdrawn','start','var(--text-2)'));
  const guide=node('line',{x1:0,x2:0,y1:top,y2:flowBottom,stroke:'var(--text-3)','stroke-dasharray':'2 3',visibility:'hidden'});svg.append(guide);
  days.forEach((d,i)=>{
    const target=node('rect',{x:x(i),y:top,width:step,height:flowBottom-top,fill:'transparent'});
    target.addEventListener('mousemove',event=>{
      guide.setAttribute('x1',String(x(i)+step/2));guide.setAttribute('x2',String(x(i)+step/2));guide.setAttribute('visibility','visible');
      show(event,[d.day,d.inject>0?`Inject ${short(d.inject)}`:d.withdraw>0?`Withdraw ${short(d.withdraw)}`:'No flow',`Inventory ${short(d.opening)} → ${short(d.closing)} ${drawing.unit}`,`Ratchet band ${d.band+1}`]);
    });
    target.addEventListener('mouseleave',()=>{guide.setAttribute('visibility','hidden');hide();});svg.append(target);
  });
}
