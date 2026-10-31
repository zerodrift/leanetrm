/**
 * Where a person is in a grid, by row key: the first row fully in view and how far below the top of the viewport it sits, and the
 * selected row. Keys name positions, not places in the rows, so the place survives new data, sorting, grouping and filtering.
 */
export type Place={anchor:string|null;offset:number;selected:string|null};

/**
 * The place a grid keeps when its rows change from `previousOrder` to `nextOrder`. The anchor stays when its row does; otherwise it falls
 * to the nearest row after it that survives, then the nearest before it, at the same offset, so nothing jumps to the top. A selection
 * whose row is gone clears, and that is reported so the grid can say why.
 */
export function retainPlace(before:Place,previousOrder:readonly string[],nextOrder:readonly string[]):{place:Place;selectionLost:boolean}{
  const surviving=new Set(nextOrder),selectionLost=before.selected!==null&&!surviving.has(before.selected);
  const selected=selectionLost?null:before.selected;
  const anchor=before.anchor===null||surviving.has(before.anchor)?before.anchor:fallback(before.anchor,previousOrder,surviving);
  return {place:anchor===null?{anchor:null,offset:0,selected}:{anchor,offset:before.offset,selected},selectionLost};
}

function fallback(anchor:string,previousOrder:readonly string[],surviving:ReadonlySet<string>){
  const at=previousOrder.indexOf(anchor);
  if(at<0)return null;
  return previousOrder.slice(at+1).find(key=>surviving.has(key))??previousOrder.slice(0,at).reverse().find(key=>surviving.has(key))??null;
}
