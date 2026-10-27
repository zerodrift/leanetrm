// The summary tile shared by the desks: a label, figures with their captions and an optional note. Every figure is a display
// string the caller received from the backend; a sign only colours a figure the backend stated it for.
import {publicText} from './market-view.js';

export function node<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
  const element=document.createElement(tag);if(className)element.className=className;if(text!==undefined)element.textContent=publicText(text);return element;
}
// A space between neighbouring pieces of text, so the text a screen reader gets keeps them apart as the layout does.
export const space=()=>document.createTextNode(' ');
export function label(text:string,aside?:string){const row=node('div','desk-tile-label');row.append(node('span',undefined,text),...(aside?[space(),node('span',undefined,aside)]:[]));return row;}
/**
 * A figure and its unit. The figure is one token that never splits or is cut off (app.css): its length is stated for a desk too
 * narrow to hold it at full size, and the unit may wrap beneath it.
 */
export function kpi(figure:string,unit?:string){
  const element=node('p','kpi-value'),token=node('span','kpi-figure',figure);
  token.style.setProperty('--figure-length',String(token.textContent!.length));
  element.append(token,...(unit===undefined?[]:[space(),node('small',undefined,unit)]));return element;
}

export type TileRow={figure:string;caption?:string;sign?:'positive'|'negative'|'zero'};
/**
 * `aside` sits opposite the label, as the instant a figure belongs to; a tile of `words` lets its figures wrap instead of shrinking;
 * `kind` names the tile for its page's layout and checks (`data-tile`), apart from the label it shows.
 */
export type TileSpec={label:string;kind?:string;aside?:string;rows:readonly TileRow[];note?:string;words?:boolean};
export function renderTile(spec:TileSpec){
  const tile=node('div','desk-tile');tile.append(label(spec.label,spec.aside));if(spec.words)tile.classList.add('desk-tile-words');if(spec.kind)tile.dataset.tile=spec.kind;
  for(const row of spec.rows){
    const block=node('div','desk-tile-row'),figure=kpi(row.figure,row.caption);if(row.sign)figure.dataset.sign=row.sign;
    block.append(figure);tile.append(block);
  }
  if(spec.note)tile.append(node('p','desk-tile-note',spec.note));return tile;
}
