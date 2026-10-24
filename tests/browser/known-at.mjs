// Observe the actual catalog cutoff without changing the response or the clock.
export async function observeCatalogClock(evaluate,path){
 await evaluate(`window.catalogKnownAt=null;{const original=window.fetch;window.fetch=async(...args)=>{
  const response=await original(...args);
  if(String(args[0])===${JSON.stringify(path)}&&response.ok)window.catalogKnownAt=(await response.clone().json()).knownAt;
  return response;
 };}`);
}

function matchesKnownAt(input,instant,zone){
 if(!instant||input.type!=='datetime-local')return false;
 const parts=new Intl.DateTimeFormat('en-US',{timeZone:zone,year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit',hourCycle:'h23'}).formatToParts(new Date(instant));
 const part=name=>Number(parts.find(p=>p.type===name).value);
 return input.valueAsNumber===Date.UTC(part('year'),part('month')-1,part('day'),part('hour'),part('minute'),part('second'));
}

// datetime-local's numeric value is the local wall time represented on a UTC axis.
export const knownAtAssertion=(input,instant,zone)=>`(${matchesKnownAt})(${input},${instant},${JSON.stringify(zone)})`;
