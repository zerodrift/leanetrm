import {setupContractPriceTerms,type ContractTermsSelection} from './contract-price-terms-view.js';
import {setupPricePublications} from './price-publications-view.js';
import {type PriceAuthoringOptions} from './price-authoring-ui.js';
import {setupTabs} from './side-panel.js';

type Dates={open:()=>Promise<void>};
export function setupPriceSetup(host:HTMLElement,o:PriceAuthoringOptions,dates:Dates){
 const contracts=setupContractPriceTerms(host.querySelector<HTMLElement>('#contract-price-terms-editor')!,o);
 const publications=setupPricePublications(host.querySelector<HTMLElement>('#price-publications-editor')!,o);
 const panels={contracts:host.querySelector<HTMLElement>('#contract-price-terms-editor')!,publications:host.querySelector<HTMLElement>('#price-publications-editor')!};
 let selected:keyof typeof panels='contracts',switching=false,epoch=0;
 const tabs=setupTabs(host.querySelector<HTMLElement>('#price-setup-tabs')!,[['contracts','Contract pricing'],['publications','Publications'],['dates','Date rules']],{label:'Contract setup editors',panel:host.querySelector<HTMLElement>('#price-setup-panel')!,selected,select:id=>{
  const next=id as keyof typeof panels|'dates';tabs.mark(selected);if(o.busy()||switching||next===selected)return;
  switching=true;const requestedAt=epoch;
  void (async()=>{try{if(!await leave()||epoch!==requestedAt)return;if(next==='dates'){await o.act(dates.open);return;}selected=next;show();await o.act(load);}finally{if(epoch===requestedAt)switching=false;}})();
 }});
 function show(){for(const [key,panel] of Object.entries(panels))panel.hidden=key!==selected;tabs.mark(selected);}
 async function load(){show();if(selected==='contracts')await contracts.load();else await publications.load();}
 async function leave(){return selected==='contracts'?contracts.leave():publications.leave();}
 show();
 return {load,leave,sync(){contracts.sync();publications.sync();},unsaved:()=>selected==='contracts'?contracts.unsaved():publications.unsaved(),editing:()=>selected==='contracts'?contracts.unsaved():publications.unsaved(),
  reset(){epoch++;switching=false;contracts.reset();publications.reset();},
  async openContract(selection:ContractTermsSelection){epoch++;switching=false;selected='contracts';show();await contracts.load(selection);}
 };
}
