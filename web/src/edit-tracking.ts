// Edits a person has made in a form and not saved (docs/ai-assistant.md §10.1, D191), which hold the assistant's moves
// away from the page. Only the person's own input marks the form, outside the fields `except` names, which only read
// with what it holds; its owner clears the mark whenever it fills, resets or saves the form.
export function trackEdits(form:HTMLElement,except:HTMLElement[]=[]){
  let edited=false;
  const mark=(event:Event)=>{if(event.isTrusted&&!except.some(field=>field.contains(event.target as Node)))edited=true;};
  form.addEventListener('input',mark);form.addEventListener('change',mark);
  return {edited:()=>edited,clear(){edited=false;}};
}
