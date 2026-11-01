import {exactNumber} from './exact-number.js';
// An exact decimal or rational string as a number, to place pixels only: the string stays the value. A rational's terms are
// scaled together, because either may exceed a number's range even when their ratio fits ordinary plot coordinates.
const unplottable='A plot needs exact finite decimal or rational strings.';
export function plotNumber(value:string):number{
  const exact=exactNumber(value);
  if(!exact)throw Error(unplottable);
  const absolute=exact.integer.replace(/^0+/,'')||'0',d=exact.denominator;
  const result=d===null?Number(value):Number(`${(exact.sign?-1:1)*(Number('0.'+absolute.slice(0,16))/Number('0.'+d.slice(0,16)))}e${absolute.length-d.length}`);
  if(!Number.isFinite(result))throw Error(unplottable);
  return result;
}
