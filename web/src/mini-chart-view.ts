// The small inline charts of the desks as SVG: the elements they are drawn with, and a curve's sparkline drawn from the geometry
// of mini-charts.ts. Every label is a string the caller received; nothing here reads a value except to place a pixel.
import type {Sparkline} from './desk-model.js';
import {publicText} from './market-view.js';
import {sparklineGeometry} from './mini-charts.js';

const svgNamespace='http://www.w3.org/2000/svg';
export function svgNode<K extends keyof SVGElementTagNameMap>(tag:K,attributes:Record<string,string|number>){
  const element=document.createElementNS(svgNamespace,tag);for(const [name,value] of Object.entries(attributes))element.setAttribute(name,String(value));return element;
}
export function svgTooltip(element:SVGElement,text:string){const title=svgNode('title',{});title.textContent=publicText(text);element.prepend(title);return element;}

/**
 * A curve's sparkline in a width x height box, in `color`, with its marked contracts as dots; or a quiet flat line that says why it
 * has none. It is an image named by the first line of its title, which is its tooltip. A decorative sparkline beside text that already
 * names the curve is hidden from assistive technology and has no tooltip, so that it adds nothing to the text around it.
 */
export function drawSparkline(sparkline:Sparkline,width:number,height:number,color:string,{decorative=false}={}):SVGElement{
  const box=svgNode('svg',{class:'sparkline',width,height,viewBox:`0 0 ${width} ${height}`,...(decorative?{'aria-hidden':'true',focusable:'false'}:{role:'img'})});
  const described=(text:string,label:string)=>{if(decorative)return box;box.setAttribute('aria-label',publicText(label));return svgTooltip(box,text);};
  const unavailable=(reason:string)=>{box.setAttribute('class','sparkline sparkline-unavailable');
    box.replaceChildren(svgNode('line',{x1:3,x2:width-3,y1:height/2,y2:height/2}));return described(reason,reason);};
  if(sparkline.state==='unavailable')return unavailable(sparkline.reason);
  const geometry=sparklineGeometry(sparkline.points,width,height);
  if(!geometry.path)return unavailable('Curve shape unavailable · no prices at this close');
  box.append(svgNode('path',{d:geometry.path,stroke:color,class:'sparkline-line'}));
  for(const x of sparkline.marks){const mark=sparklineGeometry(sparkline.points,width,height,x).mark;if(mark)box.append(svgNode('circle',{cx:mark.x,cy:mark.y,r:3,fill:color,class:'sparkline-mark'}));}
  return described(sparkline.title,sparkline.title.split('\n')[0]);
}
