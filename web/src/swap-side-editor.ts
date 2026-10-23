// Each supported side has its own presentation template. Payment/observation
// choices, resolved dates, quantities and values are supplied by the backend.
export function mountSwapSideEditor(side:'fixed'|'floating'){
 const host=document.getElementById(`swap-${side}-editor`)!;
 const template=document.getElementById(`swap-${side}-template`) as HTMLTemplateElement;
 host.replaceChildren(template.content.cloneNode(true));
 if(side==='floating'){
  const averaging=host.querySelector('#swap-averaging')!.parentElement!;
  const calendar=document.createElement('div');calendar.id='swap-observation-calendar-field';calendar.hidden=true;
  const label=document.createElement('label');label.htmlFor='swap-observation-calendar';label.textContent='Observation calendar';
  const choices=document.createElement('select');choices.id='swap-observation-calendar';
  calendar.append(label,choices);
  const status=document.createElement('p');status.id='swap-observation-status';status.className='field-help';status.setAttribute('role','status');
  const weights=document.createElement('div');weights.id='swap-observation-quantities';weights.hidden=true;
  averaging.append(calendar,status,weights);
 }
}
