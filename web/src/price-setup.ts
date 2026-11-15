import {setupContractPriceTerms,type ContractTermsSelection} from './contract-price-terms-view.js';
import {setupPricePublications} from './price-publications-view.js';
import {type PriceAuthoringOptions} from './price-authoring-ui.js';

export type PriceSetupView='contracts'|'publications';
// Contract pricing and Publications are entries of their own in the Reference Data menu and share this page: one shows at a time.
export function setupPriceSetup(host:HTMLElement,o:PriceAuthoringOptions){
 const contracts=setupContractPriceTerms(host.querySelector<HTMLElement>('#contract-price-terms-editor')!,o);
 const publications=setupPricePublications(host.querySelector<HTMLElement>('#price-publications-editor')!,o);
 const panels={contracts:host.querySelector<HTMLElement>('#contract-price-terms-editor')!,publications:host.querySelector<HTMLElement>('#price-publications-editor')!};
 let selected:PriceSetupView='contracts';
 const shown=()=>selected==='contracts'?contracts:publications;
 function show(){for(const [key,panel] of Object.entries(panels))panel.hidden=key!==selected;}
 async function load(){show();if(selected==='contracts')await contracts.load();else await publications.load();}
 show();
 return {load,leave:()=>shown().leave(),sync(){contracts.sync();publications.sync();},unsaved:()=>shown().unsaved(),editing:()=>shown().unsaved(),view:()=>selected,
  reset(){contracts.reset();publications.reset();},
  // Chooses the view to show; load reads it. The page asks about the edits of the view it leaves first.
  select(view:PriceSetupView){selected=view;show();},
  async openContract(selection:ContractTermsSelection){selected='contracts';show();await contracts.load(selection);},
  async openPublication(id:string){selected='publications';show();await publications.load(id);}
 };
}
