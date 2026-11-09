const pages=['gas-setup','crude-setup'] as const;
type DeliverySetupPage=typeof pages[number];
const isDeliverySetup=(page:string):page is DeliverySetupPage=>(pages as readonly string[]).includes(page);

// Navigation only: the commodity switch above Delivery setup. The existing page controller applies the leave guard before calling
// show, so a cancelled edit never marks the other commodity.
export function setupDeliverySetupSwitch(o:{open:(page:DeliverySetupPage)=>void;available:(page:DeliverySetupPage)=>boolean;busy:()=>boolean}){
 const get=<T extends HTMLElement>(id:string)=>document.getElementById(id) as T;
 const root=get('physical-commodity');
 let shown='';
 function sync(){
  root.hidden=!isDeliverySetup(shown);
  for(const page of pages){
   const button=get<HTMLButtonElement>('nav-'+page);
   button.hidden=!o.available(page);button.disabled=o.busy();button.classList.toggle('active',shown===page);
   if(shown===page)button.setAttribute('aria-current','page');else button.removeAttribute('aria-current');
  }
 }
 for(const page of pages)get('nav-'+page).onclick=()=>{if(!o.busy()&&o.available(page)&&shown!==page)o.open(page);};
 return {sync,show(page:string){shown=page;sync();},reset(){shown='';sync();}};
}
