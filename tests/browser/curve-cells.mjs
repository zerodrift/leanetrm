// Optional curve cells outside the viewport or clipped grid wait for demand.
function visible(node){
  const box=node.getBoundingClientRect(),holder=node.closest('.tabulator-tableholder').getBoundingClientRect();
  return box.width>0&&box.height>0&&box.left<Math.min(innerWidth,holder.right)&&box.right>Math.max(0,holder.left)&&box.top<Math.min(innerHeight,holder.bottom)&&box.bottom>Math.max(0,holder.top);
}
export const visibleCurveCells=`[...document.querySelectorAll('#desk-positions svg.sparkline,#desk-positions .sparkline-loading')].filter(${visible})`;
export const visibleCurveCellsReady=`${visibleCurveCells}.every(node=>!node.classList.contains('sparkline-loading'))`;
