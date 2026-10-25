// One presentation for each screen's native temporal contract. Inputs retain their
// existing form ownership, exact-time resolver and authorized operations.
export function refreshContextPanels(){
 for(const panel of document.querySelectorAll<HTMLDetailsElement>('.floating-context')){
  const summary=panel.querySelector('summary');if(!summary)continue;
  const values=[...panel.querySelectorAll<HTMLInputElement|HTMLSelectElement>('[data-context-coordinate]')]
   .map(input=>input.value?`${input.dataset.contextCoordinate} ${input instanceof HTMLSelectElement?input.selectedOptions[0]?.textContent??input.value:input.value.replace('T',' ')}`:'')
   .filter(Boolean);
  if(panel.dataset.contextMode==='current'){summary.textContent='Context · working data · current knowledge';continue;}
  summary.textContent=values.length?`Context${panel.dataset.contextMode==='draft'?' · unapplied':''} · ${values.join(' · ')}`:'Context · choose inputs';
 }
}
