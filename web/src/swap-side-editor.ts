// Each supported side has its own presentation template. Payment/observation
// choices, resolved dates, quantities and values are supplied by the backend.
export function mountSwapSideEditor(side:'fixed'|'floating'){
 const host=document.getElementById(`swap-${side}-editor`)!;
 const template=document.getElementById(`swap-${side}-template`) as HTMLTemplateElement;
 host.replaceChildren(template.content.cloneNode(true));
}
