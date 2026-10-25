// Edits a person has made in a form and not saved (docs/ai-assistant.md §10.1, D191), which hold the assistant's moves
// away from the page and make the panel ask before it is left. Only the person's own input marks the form, outside the fields
// `except` names, as elements or by a selector, which only read with what it holds. A picker that sets a value for the person, as a
// calendar or the date lookup does, dispatches events a form cannot tell from a script's, so it marks the form itself. The form's
// owner clears the mark whenever it fills, resets or saves the form, and `changed` hears each time the form starts or stops holding
// edits.
export function trackEdits(form:HTMLElement,except:(HTMLElement|string)[]=[],changed:()=>void=()=>{}){
  let edited=false;
  const hold=(next:boolean)=>{if(edited===next)return;edited=next;changed();};
  const onEdit=(event:Event)=>{if(event.isTrusted&&!excepted(except,event.target))hold(true);};
  form.addEventListener('input',onEdit);form.addEventListener('change',onEdit);
  return {edited:()=>edited,mark(){hold(true);},clear(){hold(false);}};
}

/** Whether an event's target is in a field `except` names, as an element or by a selector, and so is no edit of the form. */
export function excepted(except:(HTMLElement|string)[],target:EventTarget|null):boolean{
  return except.some(field=>typeof field==='string'?!!(target as Element|null)?.closest?.(field):field.contains(target as Node));
}
