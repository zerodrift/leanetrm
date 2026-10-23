// Pixel geometry for the small inline charts (row sparklines and signed bars).
// Exact server strings stay the values: they are read as numbers only to place
// pixels, and a string that is not an exact decimal raises instead of drawing a
// guess. Callers render the result as SVG.
export type ShapePoint={x:number;y:string|null;label:string};
type Pixel={x:number;y:number};
type Placed=Pixel&{at:number;label:string};
// Room for a 2px stroke and a mark of up to 3px radius inside the SVG viewport.
const inset=3;
// Geometry is kept to 0.01px so path strings stay short and stable.
const hundredths=(n:number)=>Math.round(n*100)/100;
function plotNumber(value:string){
  const n=/^-?\d+(?:\.\d+)?$/.test(value)?Number(value):NaN;
  if(!Number.isFinite(n))throw Error('A mini chart needs exact finite decimal strings.');
  return n;
}
/**
 * Places a forward curve in a width x height box; `points` ascend by x. A missing
 * month (null y) or a jump of more than one x breaks the line, so a gap is never
 * bridged. An isolated month is a zero-length segment that a round line cap draws
 * as a dot. `tips` has every valued month, with its label as given (callers put
 * the exact server string in it for tooltips). `mark` is the month at `markX`, or
 * null when that month has no value.
 */
export function sparklineGeometry(points:ShapePoint[],width:number,height:number,markX?:number):{path:string;mark:Pixel|null;tips:(Pixel&{label:string})[]}{
  const numbers=points.map(p=>p.y===null?null:plotNumber(p.y)),known=numbers.filter((n):n is number=>n!==null);
  if(!known.length)return {path:'',mark:null,tips:[]};
  const xs=points.map(p=>p.x),x0=Math.min(...xs),x1=Math.max(...xs),lo=Math.min(...known),hi=Math.max(...known);
  const padX=Math.min(inset,width/2),padY=Math.min(inset,height/2);
  const placeX=(x:number)=>x1===x0?width/2:padX+(x-x0)/(x1-x0)*(width-2*padX);
  const placeY=(n:number)=>hi===lo?height/2:height-padY-(n-lo)/(hi-lo)*(height-2*padY);
  const placed=points.map(({x,label},i)=>{const n=numbers[i];return n===null?null:{at:x,label,x:hundredths(placeX(x)),y:hundredths(placeY(n))};});
  const segments:Placed[][]=[];
  placed.forEach((q,i)=>{
    if(!q)return;
    const before=placed[i-1];
    if(!before||q.at-before.at>1)segments.push([]);
    segments.at(-1)!.push(q);
  });
  const path=segments.map(s=>s.length===1?`M${s[0].x} ${s[0].y}h0`:s.map((q,i)=>`${i?'L':'M'}${q.x} ${q.y}`).join('')).join('');
  const valued=placed.filter((q):q is Placed=>q!==null),mark=valued.find(q=>q.at===markX);
  return {path,mark:mark?{x:mark.x,y:mark.y}:null,tips:valued.map(({x,y,label})=>({x,y,label}))};
}
/**
 * A bar growing from the middle of a `width` track: right for a gain, left for a
 * loss. `max` is the largest magnitude on the scale shared by the rows being
 * compared; a bar never leaves its track, and a zero value or zero scale has none.
 */
export function divergingBar(value:string,max:string,width:number):{x:number;width:number;direction:'gain'|'loss'|'none'}{
  const v=plotNumber(value),m=plotNumber(max),middle=hundredths(width/2);
  if(v===0||m===0)return {x:middle,width:0,direction:'none'};
  const length=hundredths(Math.min(Math.abs(v/m),1)*width/2);
  return v>0?{x:middle,width:length,direction:'gain'}:{x:hundredths(middle-length),width:length,direction:'loss'};
}
