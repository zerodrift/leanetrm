// Edits a person has made in a form and not saved (docs/ai-assistant.md §10.1, D191), which hold the assistant's moves
// away from the page and make the panel ask before it is left. Only the person's own input marks the form, outside the fields
// `except` names, which only read with what it holds. A picker that sets a value for the person, as a calendar or the date lookup
// does, dispatches events a form cannot tell from a script's, so it marks the form itself. The form's owner clears the mark
// whenever it fills, resets or saves the form.
export function trackEdits(form:HTMLElement,except:HTMLElement[]=[]){
  let edited=false;
  const onEdit=(event:Event)=>{if(event.isTrusted&&!except.some(field=>field.contains(event.target as Node)))edited=true;};
  form.addEventListener('input',onEdit);form.addEventListener('change',onEdit);
  return {edited:()=>edited,mark(){edited=true;},clear(){edited=false;}};
}
