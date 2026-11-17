import {plotNumber} from './plot-number.js';
import type {SurfaceLink} from './vol-surface-link.js';
import {richCheapColour,themeToken,volTheme} from './vol-surface-theme.js';
import type {AxisNode,VolSurfaceResult} from './vol-surface-view.js';

// The backend supplies every coordinate, tick label and unit. Only projection,
// rotation, shading and hit testing belong here. Point ids define mesh adjacency.
export type MeshRow={expiry:number;points:AxisNode[]};
export type MeshJoin={from:number;to:number;points:[string,string]};
export function surfaceMesh(surface:VolSurfaceResult):{rows:MeshRow[];joins:MeshJoin[]}{
 const rows:MeshRow[]=[];
 surface.expiries.forEach((expiry,index)=>{
  if(index&&expiry.expiryAt<=surface.expiries[index-1].expiryAt)throw Error('The surface lists its expiries out of order.');
  if((expiry.status==='fitted')!==(expiry.nodes.length>0))throw Error('The surface gives an expiry nodes that disagree with its status.');
  if(expiry.nodes.length)rows.push({expiry:index,points:expiry.nodes});
 });
 const following=(row:MeshRow)=>new Map(row.points.slice(0,-1).flatMap((point,i)=>point.covered&&row.points[i+1].covered?[[point.pointId,row.points[i+1].pointId]]:[]));
 const joins:MeshJoin[]=[];
 for(let i=1;i<rows.length;i++){
  const [near,far]=[rows[i-1],rows[i]];if(far.expiry!==near.expiry+1)continue;
  const farNext=following(far);
  for(const [point,next] of following(near))if(farNext.get(point)===next)joins.push({from:near.expiry,to:far.expiry,points:[point,next]});
 }
 return {rows,joins};
}
export function levelRange(surface:VolSurfaceResult):{low:string;high:string}|null{
 const fitted=surface.expiries.flatMap(e=>e.nodes.filter(n=>n.covered).map(n=>n.fitted!));
 if(!fitted.length)return null;
 const by=(pick:(a:number,b:number)=>boolean)=>fitted.reduce((best,v)=>pick(plotNumber(v),plotNumber(best))?v:best);
 return {low:by((a,b)=>a<b),high:by((a,b)=>a>b)};
}

type Rgb=[number,number,number];
type Screen={x:number;y:number;depth:number};
type Stalk={code:string;richCheap:string;base:[number,number,number];tip:[number,number,number]};
type Placed={code:string;base:Screen;tip:Screen};
// The default view: turned a little from the strike axis and looking down onto the surface.
const initial={yaw:-0.6,pitch:0.45};
const pitchLimits=[0.08,1.35] as const;
// The box's height against its strike and expiry sides, each spanning -1 to 1, and the viewer's distance in the same units.
const height=1.1,focal=6;

/**
 * Draws `surface` on `canvas` and keeps it drawn: it rotates on a drag or the arrow keys, redraws at the canvas's size and pixel
 * ratio and in the theme's colours, and highlights the linked series' stalk. `dispose` releases every listener and observer.
 */
export function drawSurface3d(canvas:HTMLCanvasElement,surface:VolSurfaceResult,link:SurfaceLink):{reset():void;dispose():void}{
 const mesh=surfaceMesh(surface),levels=levelRange(surface);
 if(!levels)throw Error('The surface has no fitted expiry to draw in 3D.');
 const supplied=surface.projection;
 const fittedOf=new Map(mesh.rows.map(row=>[row.expiry,new Map(row.points.map(p=>[p.pointId,p]))]));
 const stalks=surface.series.filter(s=>s.axisValue!==null&&s.richCheap!==null&&s.fitted!==null&&s.series.marketVolatility!==null);
 const xs=[...supplied.x.ticks.map(p=>plotNumber(p.value)),...stalks.map(s=>plotNumber(s.axisValue!))];
 const times=surface.expiries.map(e=>plotNumber(e.yearFraction!));
 const span=(values:number[])=>({min:Math.min(...values),max:Math.max(...values)});
 const strikeSpan=span(xs),timeSpan=span(times),volTicks=supplied.height.ticks;
 const volSpan=span(volTicks.map(t=>plotNumber(t.value)));
 const unit=(value:number,{min,max}:{min:number;max:number})=>max>min?(value-min)/(max-min):.5;
 const across=(value:number)=>(supplied.x.reversed?1-unit(value,strikeSpan):unit(value,strikeSpan))*2-1;
 const world=(x:number,time:number,vol:number):[number,number,number]=>[across(x),unit(time,timeSpan)*2-1,unit(vol,volSpan)*height];
 const timeOf=new Map(surface.expiries.map(e=>[e.expiryAt,plotNumber(e.yearFraction!)]));
 const placedStalks:Stalk[]=stalks.map(s=>{const x=plotNumber(s.axisValue!),t=timeOf.get(s.series.expiryAt)!;
  return {code:s.series.code,richCheap:s.richCheap!,base:world(x,t,plotNumber(s.fitted!)),tip:world(x,t,plotNumber(s.series.marketVolatility!))};});
 const point=(expiry:number,id:string)=>{const node=fittedOf.get(expiry)!.get(id)!;return world(plotNumber(node.axisValue),times[expiry],plotNumber(node.fitted!));};
 const shade=(expiry:number,id:string)=>unit(plotNumber(fittedOf.get(expiry)!.get(id)!.fitted!),{min:plotNumber(levels.low),max:plotNumber(levels.high)});
 const gaps=mesh.rows.flatMap(r=>r.points.filter(n=>!n.covered).map(n=>({text:`${n.label}: ${n.reason!.message} (${n.reason!.code})`,at:world(plotNumber(n.axisValue),times[r.expiry],volSpan.min)})));
 let gapScreens:{text:string;at:Screen}[]=[];
 canvas.dataset.axis=supplied.x.id;canvas.dataset.gaps=String(gaps.length);

 canvas.dataset.rows=mesh.rows.map(r=>surface.expiries[r.expiry].expiryAt).join(' ');
 canvas.dataset.joins=[...new Set(mesh.joins.map(j=>`${surface.expiries[j.from].expiryAt}/${surface.expiries[j.to].expiryAt}`))].join(' ');
 canvas.tabIndex=0;canvas.setAttribute('role','img');
 canvas.setAttribute('aria-label','Fitted volatility surface in 3D. Drag or use the arrow keys to rotate it; Reset view returns to the default angle.');

 let angle={...initial},size={width:0,height:0,ratio:0},highlighted:string|null=null,placed:Placed[]=[],drag:{x:number;y:number}|null=null;
 const context=canvas.getContext('2d');
 if(!context)throw Error('This browser cannot draw the 3D surface.');
 const ctx=context;

 function project([x,y,z]:[number,number,number],scale:number,centre:{x:number;y:number}):Screen{
  const cy=Math.cos(angle.yaw),sy=Math.sin(angle.yaw),cp=Math.cos(angle.pitch),sp=Math.sin(angle.pitch);
  const across=x*cy-y*sy,ahead=x*sy+y*cy,up=z-height/2;
  const depth=ahead*cp-up*sp,s=focal/(focal+depth);
  return {x:centre.x+across*s*scale,y:centre.y-(ahead*sp+up*cp)*s*scale,depth};
 }

 function draw(){
  const {width,height:tall,ratio}=size;if(!width)return;
  const colours=palette(canvas,ctx);
  ctx.setTransform(ratio,0,0,ratio,0,0);ctx.clearRect(0,0,width,tall);
  // The box and its labels' anchors fill the canvas at every angle, leaving room for the labels' text around them.
  const unitView=(p:[number,number,number])=>project(p,1,{x:0,y:0});
  const nearY=unitView([0,-1,0]).depth<unitView([0,1,0]).depth?-1:1,nearX=unitView([-1,0,0]).depth<unitView([1,0,0]).depth?-1:1;
  const out=1.16,top=height;
  const anchors=[-1,1].flatMap(a=>[-1,1].flatMap(b=>[[a,b,0],[a,b,top],[a,nearY*(out+.16),0],[nearX*(out+.62),b*.5,0]] as [number,number,number][])).map(unitView);
  const bounds={left:Math.min(...anchors.map(p=>p.x)),right:Math.max(...anchors.map(p=>p.x)),top:Math.min(...anchors.map(p=>p.y)),bottom:Math.max(...anchors.map(p=>p.y))};
  const room={x:44,top:30,bottom:14};
  const scale=Math.min((width-2*room.x)/(bounds.right-bounds.left),(tall-room.top-room.bottom)/(bounds.bottom-bounds.top));
  const centre={x:width/2-(bounds.left+bounds.right)/2*scale,y:room.top+(tall-room.top-room.bottom)/2-(bounds.top+bounds.bottom)/2*scale};
  const at=(p:[number,number,number])=>project(p,scale,centre);
  ctx.font=`11px ${colours.font}`;ctx.lineJoin='round';ctx.lineCap='round';

  // The box: its floor and the two walls behind the surface, with the volatility ticks on the walls.
  const quad=(points:[number,number,number][],fill:string,stroke?:string)=>{const s=points.map(at);ctx.beginPath();s.forEach((p,i)=>i?ctx.lineTo(p.x,p.y):ctx.moveTo(p.x,p.y));ctx.closePath();
   ctx.fillStyle=fill;ctx.fill();if(stroke){ctx.strokeStyle=stroke;ctx.lineWidth=1;ctx.stroke();}};
  quad([[-1,-1,0],[1,-1,0],[1,1,0],[-1,1,0]],colours.surface2,colours.border);
  quad([[-nearX,-1,0],[-nearX,1,0],[-nearX,1,top],[-nearX,-1,top]],colours.surface2,colours.border);
  quad([[-1,-nearY,0],[1,-nearY,0],[1,-nearY,top],[-1,-nearY,top]],colours.surface2,colours.border);
  const line=(a:[number,number,number],b:[number,number,number],stroke:string,widthPx=1)=>{const p=at(a),q=at(b);ctx.beginPath();ctx.moveTo(p.x,p.y);ctx.lineTo(q.x,q.y);ctx.strokeStyle=stroke;ctx.lineWidth=widthPx;ctx.stroke();};
  ctx.setLineDash([3,3]);
  for(const tick of volTicks){const z=unit(plotNumber(tick.value),volSpan)*height;line([-nearX,-1,z],[-nearX,1,z],colours.border);line([-1,-nearY,z],[1,-nearY,z],colours.border);}
  ctx.setLineDash([]);

  // The axes' labels, each kept only where it does not overlap the one before it.
  const text=(value:string,p:Screen,fill:string,align:CanvasTextAlign='center',bold=false)=>{ctx.font=`${bold?'600 ':''}11px ${colours.font}`;ctx.fillStyle=fill;ctx.textAlign=align;ctx.textBaseline='middle';ctx.fillText(value,p.x,p.y);};
  const spaced=(labels:{text:string;at:[number,number,number];floor:[number,number,number][];fill:string}[])=>{
   const kept:{x:number;y:number;w:number}[]=[];
   for(const l of labels){const p=at(l.at),w=ctx.measureText(l.text).width+8;
    if(kept.some(k=>Math.abs(k.x-p.x)<(k.w+w)/2&&Math.abs(k.y-p.y)<14))continue;
    kept.push({x:p.x,y:p.y,w});ctx.setLineDash([2,4]);line(l.floor[0],l.floor[1],colours.border);ctx.setLineDash([]);text(l.text,p,l.fill);}
  };
  spaced(supplied.x.ticks.map(t=>{const x=across(plotNumber(t.value));
   return {text:t.label,at:[x,nearY*out,0],floor:[[x,-1,0],[x,1,0]],fill:colours.text3};}));
  spaced(surface.expiries.map((e,i)=>{const y=unit(times[i],timeSpan)*2-1;
   return {text:e.timeLabel!+(e.status==='fitted'?'':' · no fit'),at:[nearX*(out+.08),y,0],floor:[[-1,y,0],[1,y,0]],fill:e.status==='fitted'?colours.text2:colours.text3};}));
  const corner=[[-nearX,nearY],[-nearX,-nearY],[nearX,-nearY]].map(([x,y])=>({x,y,p:at([x,y,0])})).sort((a,b)=>a.p.x-b.p.x)[0];
  line([corner.x,corner.y,0],[corner.x,corner.y,top],colours.borderStrong);
  let lastTick=Infinity;
  for(const tick of volTicks){const p=at([corner.x,corner.y,unit(plotNumber(tick.value),volSpan)*height]);if(Math.abs(lastTick-p.y)<13)continue;lastTick=p.y;text(tick.label,{...p,x:p.x-8},colours.text3,'right');}
  const xTitle=at([0,nearY*(out+.16),0]);
  text(`${supplied.x.label} (${supplied.x.unit})`,{...xTitle,y:xTitle.y+18},colours.text2,'center',true);
  text(`${supplied.depth.label} (${supplied.depth.unit})`,at([nearX*(out+.62),0,0]),colours.text2,'center',true);
  text(`${supplied.height.label} (${supplied.height.unit})`,{...at([corner.x,corner.y,top]),y:at([corner.x,corner.y,top]).y-14},colours.text2,'center',true);

  // The surface, the fitted rows and the stalks, painted far to near.
  const light=normalise([-.35,-.55,.75]);
  type Shape={depth:number;paint():void};
  const shapes:Shape[]=[];
  for(const j of mesh.joins){
   const corners:[number,number][]=[[j.from,0],[j.from,1],[j.to,1],[j.to,0]];
   const points=corners.map(([e,k])=>point(e,j.points[k])),screen=points.map(at);
   const level=corners.reduce((sum,[e,k])=>sum+shade(e,j.points[k]),0)/4;
   const normal=normalise(cross(minus(points[2],points[0]),minus(points[3],points[1])));
   const lit=.7+.3*Math.abs(dot(normal,light));
   const fill=rgb(mix(colours.low,colours.high,level).map(c=>c*lit) as Rgb);
   shapes.push({depth:screen.reduce((sum,p)=>sum+p.depth,0)/4,paint(){ctx.beginPath();screen.forEach((p,i)=>i?ctx.lineTo(p.x,p.y):ctx.moveTo(p.x,p.y));ctx.closePath();
    ctx.globalAlpha=.9;ctx.fillStyle=fill;ctx.fill();ctx.strokeStyle=fill;ctx.lineWidth=.6;ctx.stroke();ctx.globalAlpha=1;}});
  }
  for(const row of mesh.rows)for(let i=1;i<row.points.length;i++){
   if(!row.points[i-1].covered||!row.points[i].covered)continue;
   const a=at(point(row.expiry,row.points[i-1].pointId)),b=at(point(row.expiry,row.points[i].pointId));
   shapes.push({depth:(a.depth+b.depth)/2-.02,paint(){ctx.beginPath();ctx.moveTo(a.x,a.y);ctx.lineTo(b.x,b.y);ctx.strokeStyle=colours.accent;ctx.lineWidth=1.6;ctx.stroke();}});
  }
  for(const row of mesh.rows)for(let i=0;i<row.points.length;i++){
   const node=row.points[i];if(!node.covered||row.points[i-1]?.covered||row.points[i+1]?.covered)continue;
   const p=at(point(row.expiry,node.pointId));
   shapes.push({depth:p.depth-.02,paint(){ctx.beginPath();ctx.arc(p.x,p.y,2.5,0,Math.PI*2);ctx.fillStyle=colours.accent;ctx.fill();}});
  }
  gapScreens=gaps.map(g=>({text:g.text,at:at(g.at)}));
  for(const gap of gapScreens){ctx.beginPath();ctx.arc(gap.at.x,gap.at.y,4,0,Math.PI*2);ctx.strokeStyle=colours.text3;ctx.setLineDash([2,2]);ctx.stroke();ctx.setLineDash([]);}
  placed=[];
  for(const s of placedStalks){
   const base=at(s.base),tip=at(s.tip),linked=s.code===highlighted,colour=richCheapColour(colours,s.richCheap);
   placed.push({code:s.code,base,tip});
   shapes.push({depth:Math.min(base.depth,tip.depth)-.03-(linked?1:0),paint(){
    ctx.beginPath();ctx.moveTo(base.x,base.y);ctx.lineTo(tip.x,tip.y);ctx.strokeStyle=colour;ctx.lineWidth=linked?3:1.6;ctx.stroke();
    ctx.beginPath();ctx.arc(tip.x,tip.y,linked?6:3.5,0,Math.PI*2);ctx.fillStyle=colour;ctx.fill();
    if(linked){ctx.strokeStyle=colours.text;ctx.lineWidth=2;ctx.stroke();}else{ctx.strokeStyle=colours.surface;ctx.lineWidth=1;ctx.stroke();}
   }});
  }
  shapes.sort((a,b)=>b.depth-a.depth);
  for(const shape of shapes)shape.paint();
  const linked=placed.find(p=>p.code===highlighted);
  if(linked){canvas.dataset.linked=linked.code;canvas.dataset.linkedAt=`${linked.tip.x.toFixed(1)},${linked.tip.y.toFixed(1)}`;}
  else{delete canvas.dataset.linked;delete canvas.dataset.linkedAt;}
 }

 // The canvas's size in CSS pixels and device pixels, kept to its width and the display's pixel ratio.
 function measure(){
  const width=canvas.clientWidth,ratio=window.devicePixelRatio||1;
  if(width===size.width&&ratio===size.ratio)return;
  const tall=Math.round(Math.min(460,Math.max(300,width*.56)));
  size={width,height:tall,ratio};canvas.style.height=`${tall}px`;canvas.width=Math.round(width*ratio);canvas.height=Math.round(tall*ratio);
  draw();
 }
 const resize=new ResizeObserver(measure);resize.observe(canvas);

 const turn=(yaw:number,pitch:number)=>{angle={yaw:angle.yaw+yaw,pitch:Math.min(pitchLimits[1],Math.max(pitchLimits[0],angle.pitch+pitch))};draw();};
 // The stalk under the pointer: the nearest market point within reach, or else the nearest stalk; the nearer to the viewer on a tie.
 function stalkAt(x:number,y:number):string|null{
  let best:{code:string;distance:number;depth:number}|null=null;
  for(const p of placed){
   const tip=Math.hypot(p.tip.x-x,p.tip.y-y),along=segmentDistance(x,y,p.base,p.tip),distance=tip<=8?tip:along<=5?along+8:Infinity;
   if(distance<Infinity&&(!best||distance<best.distance||distance===best.distance&&p.tip.depth<best.depth))best={code:p.code,distance,depth:p.tip.depth};
  }
  return best?.code??null;
 }
 const down=(event:PointerEvent)=>{if(event.button!==0)return;drag={x:event.clientX,y:event.clientY};canvas.setPointerCapture(event.pointerId);canvas.style.cursor='grabbing';};
 const move=(event:PointerEvent)=>{
  if(drag){turn((event.clientX-drag.x)*.008,(event.clientY-drag.y)*.006);drag={x:event.clientX,y:event.clientY};return;}
  const r=canvas.getBoundingClientRect(),code=stalkAt(event.clientX-r.left,event.clientY-r.top);
  const gap=gapScreens.find(g=>Math.hypot(g.at.x-(event.clientX-r.left),g.at.y-(event.clientY-r.top))<9);
  canvas.title=gap?.text??'';canvas.style.cursor=code||gap?'pointer':'grab';if(code)link.set(code);
 };
 const up=(event:PointerEvent)=>{if(!drag)return;drag=null;if(canvas.hasPointerCapture(event.pointerId))canvas.releasePointerCapture(event.pointerId);canvas.style.cursor='grab';};
 const keys:Record<string,[number,number]>={ArrowLeft:[-.12,0],ArrowRight:[.12,0],ArrowUp:[0,.06],ArrowDown:[0,-.06]};
 const key=(event:KeyboardEvent)=>{const step=keys[event.key];if(!step)return;event.preventDefault();turn(...step);};
 canvas.addEventListener('pointerdown',down);canvas.addEventListener('pointermove',move);canvas.addEventListener('pointerup',up);canvas.addEventListener('pointercancel',up);
 canvas.addEventListener('keydown',key);canvas.style.cursor='grab';
 const unsubscribe=link.subscribe(code=>{highlighted=code;draw();});
 highlighted=link.current();measure();

 return {
  reset(){angle={...initial};draw();},
  dispose(){
   resize.disconnect();unsubscribe();
   canvas.removeEventListener('pointerdown',down);canvas.removeEventListener('pointermove',move);canvas.removeEventListener('pointerup',up);canvas.removeEventListener('pointercancel',up);
   canvas.removeEventListener('keydown',key);
  },
 };
}

type V3=[number,number,number];
const minus=(a:V3,b:V3):V3=>[a[0]-b[0],a[1]-b[1],a[2]-b[2]];
const cross=(a:V3,b:V3):V3=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]];
const dot=(a:V3,b:V3)=>a[0]*b[0]+a[1]*b[1]+a[2]*b[2];
const normalise=(a:V3):V3=>{const length=Math.hypot(...a)||1;return [a[0]/length,a[1]/length,a[2]/length];};
const mix=(a:Rgb,b:Rgb,t:number):Rgb=>[0,1,2].map(i=>a[i]+(b[i]-a[i])*Math.min(1,Math.max(0,t))) as Rgb;
const rgb=([r,g,b]:Rgb)=>`rgb(${Math.round(r)},${Math.round(g)},${Math.round(b)})`;
function segmentDistance(x:number,y:number,a:Screen,b:Screen){
 const dx=b.x-a.x,dy=b.y-a.y,length=dx*dx+dy*dy,t=length?Math.min(1,Math.max(0,((x-a.x)*dx+(y-a.y)*dy)/length)):0;
 return Math.hypot(a.x+t*dx-x,a.y+t*dy-y);
}

// The theme's colours where the canvas is, with the ends of the shading, which are mixed, read as RGB through the canvas's own colour
// parsing.
function palette(node:Element,ctx:CanvasRenderingContext2D){
 const channels=(name:string):Rgb=>{
  ctx.fillStyle='#000';ctx.fillStyle=themeToken(node,name);const parsed=String(ctx.fillStyle);
  const hex=/^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(parsed),functional=/^rgba?\((\d+), (\d+), (\d+)/.exec(parsed),found=hex?hex.slice(1).map(h=>parseInt(h,16)):functional?.slice(1).map(Number);
  if(!found)throw Error(`The theme's ${name} is not a colour the 3D view can shade.`);
  return found as Rgb;
 };
 return {...volTheme(node),low:channels('--vol-level-low'),high:channels('--vol-level-high')};
}
