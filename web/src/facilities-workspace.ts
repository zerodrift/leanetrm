import {setupFacilityQuantities} from './facility-quantities-view.js';
import {setupFacilityOperations} from './facility-operations-view.js';
import {setupTransportCapacity} from './transport-capacity-view.js';
import {setupTransportRoutes} from './transport-routes-view.js';
import {setupTabs} from './side-panel.js';

type Section='quantities'|'definitions'|'activity'|'transport'|'route';
type Options=Parameters<typeof setupFacilityQuantities>[0]&{permission:(name:string)=>boolean};

/** One Facilities entry keeps physical asset work and contract allocations
 * distinct. Only the chosen view reads; each native operation keeps its rights. */
export function setupFacilities(o:Options){
 const root=document.getElementById('facilities-page')!,quantitiesHost=document.createElement('div'),operationsHost=document.createElement('div'),transportHost=document.createElement('div'),routesHost=document.createElement('div'),navigation=document.createElement('div'),content=document.createElement('div');
 quantitiesHost.id='facility-quantities-workspace';operationsHost.id='facility-operations-workspace';navigation.id='facilities-workspace-tabs';navigation.className='side-panel-tabs page-tabs';content.id='facilities-workspace-content';
 transportHost.id='transport-capacity-workspace';
 routesHost.id='transport-routes-workspace';
 quantitiesHost.append(...root.childNodes);content.append(quantitiesHost,operationsHost,transportHost,routesHost);root.append(navigation,content);
 const physicalAllowed=()=>['ReadMarketData','ReadTrades','ReadFacilityQuantities'].every(o.permission),transportAllowed=()=>['ReadMarketData','ReadTrades','ReadGasOperations'].every(o.permission);
 const available=(next:Section)=>next==='transport'||next==='route'?transportAllowed():physicalAllowed();
 const initial=():Section=>!physicalAllowed()&&transportAllowed()?'transport':'quantities';
 let section:Section=initial(),operations:ReturnType<typeof setupFacilityOperations>|undefined,transport:ReturnType<typeof setupTransportCapacity>|undefined,routes:ReturnType<typeof setupTransportRoutes>|undefined,quantities:ReturnType<typeof setupFacilityQuantities>|undefined;
 quantities=setupFacilityQuantities({...o,busy:()=>o.busy()||!quantities?.unconfirmed()&&(!!operations?.unconfirmed()||!!transport?.unconfirmed()||!!routes?.unconfirmed())});
 operations=setupFacilityOperations(operationsHost,{...o,pendingElsewhere:()=>!!quantities?.unconfirmed()||!!transport?.unconfirmed()||!!routes?.unconfirmed(),openAllocations:async id=>{if(!await operations!.leave())return;section='quantities';show();await quantities!.load(id,false);o.changed();}});
 transport=setupTransportCapacity(transportHost,{...o,pendingElsewhere:()=>!!quantities?.unconfirmed()||!!operations?.unconfirmed()||!!routes?.unconfirmed()});
 routes=setupTransportRoutes(routesHost,{...o,pendingElsewhere:()=>!!quantities?.unconfirmed()||!!operations?.unconfirmed()||!!transport?.unconfirmed()});
 const active=()=>section==='quantities'?quantities!:section==='transport'?transport!:section==='route'?routes!:operations!;
 const tabs=setupTabs(navigation,[['definitions','Facility definitions'],['activity','Physical activity'],['quantities','Quantities & allocations'],['transport','Transport capacity'],['route','Transport route plans']],{label:'Facilities workspace',panel:content,selected:section,select:next=>{
  tabs.mark(section);if(next===section||o.busy()||!available(next as Section))return;
  void o.act(async()=>{if(!await active().leave())return;section=next as Section;show();await active().load();o.changed();});
 }});
 for(const id of ['definitions','activity','quantities','transport','route'])navigation.querySelector<HTMLButtonElement>('#'+navigation.id+'-'+id)!.dataset.facilitiesView=id;
 function show(){tabs.mark(section);quantitiesHost.hidden=section!=='quantities';operationsHost.hidden=!['definitions','activity'].includes(section);transportHost.hidden=section!=='transport';routesHost.hidden=section!=='route';if(section==='definitions'||section==='activity')operations!.show(section);}
 show();
 return{
  async open(next:'definitions'|'activity'|'transport'|'route'){
   if(!available(next))throw Error('Current permission for this operations workspace is required.');
   const actor=o.identity();if(!await active().leave()||actor!==o.identity())return;
   section=next;show();await active().load();o.changed();
  },
  async load(ledgerId?:string,rememberSelection=true){operations!.sync();transport!.sync();routes!.sync();if(ledgerId!==undefined){if(!physicalAllowed())throw Error('Facility quantity read permission is required.');section='quantities';show();await quantities!.load(ledgerId,rememberSelection);}else{if(routes!.unconfirmed())section='route';else if(transport!.unconfirmed())section='transport';else if(operations!.unconfirmed())section=operations!.recoverySection();else if(!available(section))section=initial();show();await active().load();}},
  sync(){quantities!.sync();operations!.sync();transport!.sync();routes!.sync();for(const button of navigation.querySelectorAll<HTMLButtonElement>('button'))button.disabled=o.busy()||!available(button.dataset.facilitiesView as Section);},
  section:()=>section,selected:()=>section==='quantities'?quantities!.selected():null,
  edited:()=>quantities!.edited()||operations!.edited()||transport!.edited()||routes!.edited(),leave:()=>active().leave(),refresh:()=>active().refresh(),
  reset(){quantities!.reset();operations!.reset();transport!.reset();routes!.reset();if(!available(section))section=initial();show();},
 };
}
