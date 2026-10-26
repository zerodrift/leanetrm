// An exact decimal or rational string as a number, to place pixels only: the string stays the value. A rational's terms are
// scaled together, because either may exceed a number's range even when their ratio fits ordinary plot coordinates.
const unplottable='A plot needs exact finite decimal or rational strings.';
export function plotNumber(value:string):number{
  if(!/^-?\d+(?:\.\d+|\/[1-9]\d*)?$/.test(value))throw Error(unplottable);
  const [n,d]=value.split('/');
  const absolute=n.replace(/^-/,'').replace(/^0+/,'')||'0';
  const result=d===undefined?Number(n):Number(`${(n.startsWith('-')?-1:1)*(Number('0.'+absolute.slice(0,16))/Number('0.'+d.slice(0,16)))}e${absolute.length-d.length}`);
  if(!Number.isFinite(result))throw Error(unplottable);
  return result;
}
