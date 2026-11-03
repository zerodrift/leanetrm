// One presentation for each screen's native temporal contract. Inputs retain their
// existing form ownership, exact-time resolver and authorized operations.
// A coordinate's date or wall time reads as a calendar date; seconds show only when they are set.
function coordinateText(value:string){
 const date=/^\d{4}-\d{2}-\d{2}$/.test(value),time=/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?$/.test(value);
 if(!date&&!time)return value;
 const seconds=time&&!/T\d{2}:\d{2}(:00)?$/.test(value);
 return new Intl.DateTimeFormat('en',{timeZone:'UTC',month:'short',day:'numeric',year:'numeric',...(time?{hour:'numeric',minute:'2-digit',...(seconds?{second:'2-digit'}:{})}:{})}).format(new Date(date?value+'T00:00:00Z':value+'Z'));
}
export function refreshContextPanels(){
 for(const panel of document.querySelectorAll<HTMLDetailsElement>('.floating-context')){
  const summary=panel.querySelector('summary');if(!summary)continue;
  const values=[...panel.querySelectorAll<HTMLInputElement|HTMLSelectElement>('[data-context-coordinate]')]
   .map(input=>input.value?`${input.dataset.contextCoordinate} ${input instanceof HTMLSelectElement?input.selectedOptions[0]?.textContent??input.value:coordinateText(input.value)}`:'')
   .filter(Boolean);
  // The shared context names the knowledge it shows itself, beside its values: working data has none to add.
  const named=summary.querySelector<HTMLElement>('[data-context-values]');
  if(named){named.textContent=panel.dataset.contextMode==='current'?'':values.join(' · ');continue;}
  if(panel.dataset.contextMode==='current'){summary.textContent='Context · working data · current knowledge';continue;}
  summary.textContent=values.length?`Context${panel.dataset.contextMode==='draft'?' · unapplied':''} · ${values.join(' · ')}`:'Context · choose inputs';
 }
}
