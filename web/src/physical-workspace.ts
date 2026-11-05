const pages=['gas-setup','crude-setup','facilities'] as const;
type PhysicalPage=typeof pages[number];
export const isPhysicalPage=(page:string):page is PhysicalPage=>(pages as readonly string[]).includes(page);

// Shared navigation only. The owner opens the existing authorized page after its
// ordinary leave guard; a cancelled edit never marks another section selected.
export function setupPhysicalWorkspace(o:{open:(page:PhysicalPage)=>void;available:(page:PhysicalPage)=>boolean;busy:()=>boolean}){
 const get=(id:string)=>document.getElementById(id)!;
 const root=get('physical-workspace'),entry=get('nav-physical');
 const setupCards=[['physical-pipelines','gas-setup'],['physical-crude-sites','crude-setup']] as const;
 let shown='',last:PhysicalPage='gas-setup',setup:'gas-setup'|'crude-setup'='gas-setup';
 const current=()=>isPhysicalPage(shown)&&o.available(shown);
 const destination=()=>o.available(last)?last:'gas-setup';
 function open(page:PhysicalPage){if(!o.busy()&&o.available(page))o.open(page);}
 function mark(button:HTMLElement,on:boolean){button.classList.toggle('active',on);if(on)button.setAttribute('aria-current','page');else button.removeAttribute('aria-current');}
 function sync(){
  root.hidden=!current();entry.hidden=!pages.some(o.available);(entry as HTMLButtonElement).disabled=o.busy();mark(entry,current());
  get('physical-commodity').hidden=shown==='facilities';
  for(const [id,page] of setupCards)(get(id) as HTMLButtonElement).disabled=o.busy()||!o.available(page);
  const delivery=get('physical-delivery-setup') as HTMLButtonElement;delivery.disabled=o.busy();mark(delivery,shown==='gas-setup'||shown==='crude-setup');
  for(const page of pages){const button=get('nav-'+page) as HTMLButtonElement;button.hidden=!o.available(page);button.disabled=o.busy();mark(button,shown===page);}
 }
 entry.onclick=()=>{if(!current())open(destination());};
 get('physical-delivery-setup').onclick=()=>open(setup);
 for(const page of pages)get('nav-'+page).onclick=()=>open(page);
 for(const [id,page] of setupCards)get(id).onclick=()=>open(page);
 return{sync,entry:destination,
  show(page:string){shown=page;if(isPhysicalPage(page)){last=page;if(page!=='facilities')setup=page;}sync();},
  reset(){shown='';last=setup='gas-setup';sync();}};
}
