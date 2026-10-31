// Product editors share the desk's panel without duplicating their forms,
// native requests or recovery state. Each editor owns its product rules.
export type TicketProduct='ppa'|'gas';
export type ProductEditor={root:HTMLElement;begin:()=>Promise<void>;inspect:(trade:string)=>Promise<void>;reset:()=>void;
 unsaved:()=>boolean;pending:()=>boolean;sync:()=>void};
export function productTickets(editors:Record<TicketProduct,ProductEditor>){
 const homes=new Map<TicketProduct,Comment>();
 for(const product of ['ppa','gas'] as const){const home=document.createComment(`${product} editor home`);editors[product].root.before(home);homes.set(product,home);}
 let active:TicketProduct|undefined;
 function detach(){
  if(!active)return;const product=active;active=undefined;const editor=editors[product];
  editor.reset();editor.root.classList.remove('embedded-trade','inspect-trade');editor.root.hidden=true;homes.get(product)!.after(editor.root);
 }
 return{
  async open(product:TicketProduct,host:HTMLElement,trade?:string){
   detach();const editor=editors[product];active=product;host.replaceChildren(editor.root);editor.root.hidden=false;
   editor.root.classList.add('embedded-trade');editor.root.classList.toggle('inspect-trade',trade!==undefined);
   try{if(trade)await editor.inspect(trade);else await editor.begin();}
   catch(error){detach();throw error;}
  },
  detach,active:()=>active,
  unsaved:()=>active!==undefined&&editors[active].unsaved(),
  pending:()=>(['ppa','gas'] as const).filter(p=>editors[p].pending()),
  sync:()=>{if(active)editors[active].sync();},
 };
}
export type ProductTickets=ReturnType<typeof productTickets>;
