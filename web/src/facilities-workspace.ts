import {setupPipelineOutlook} from './pipeline-outlook.js';
import {setupFacilityTrades} from './facility-trades.js';
import {setupFacilityQuantities} from './facility-quantities-view.js';
import {setupFacilityOperations} from './facility-operations-view.js';
import {setupTransportCapacity} from './transport-capacity-view.js';
import {setupTransportRoutes} from './transport-routes-view.js';
import {setupDeliveryWorkbench,type DeliveryWorkbenchOptions} from './delivery-workbench-view.js';
import {setupTabs} from './side-panel.js';
import {facilityViews,facilityViewSections,facilityViewOf,facilitySectionNames,type FacilitySection,type FacilityView} from './facilities-views.js';

type Section=FacilitySection;
type Options=Parameters<typeof setupFacilityQuantities>[0]&Pick<DeliveryWorkbenchOptions,'nominationContext'|'nativeDeliveryReason'|'openNativeDelivery'>&{permission:(name:string)=>boolean};
type Consumer={load:(id?:string)=>Promise<void>;sync:()=>void;edited:()=>boolean;unconfirmed:()=>boolean;leave:()=>boolean|Promise<boolean>;refresh:()=>void;reset:()=>void};

/** One Facilities entry coordinates independently authorized consumers. Only
 * the chosen view reads; unknown command outcomes remain owned by their source. */
export function setupFacilities(o:Options){
 const root=document.getElementById('facilities-page')!,navigation=document.createElement('div'),content=document.createElement('div');
 const host=()=>document.createElement('div'),hosts={outlook:host(),trades:host(),quantities:host(),activity:host(),transport:host(),route:host(),'gas-delivery':host(),'crude-delivery':host()};
 hosts.quantities.id='facility-quantities-workspace';hosts.activity.id='facility-operations-workspace';hosts.transport.id='transport-capacity-workspace';hosts.route.id='transport-routes-workspace';hosts['gas-delivery'].id='gas-delivery-workspace';hosts['crude-delivery'].id='crude-delivery-workspace';
 navigation.id='facilities-workspace-tabs';navigation.className='side-panel-tabs page-tabs';content.id='facilities-workspace-content';
 hosts.quantities.append(...root.childNodes);content.append(...Object.values(hosts));root.append(navigation,content);
 const has=(...rights:string[])=>rights.every(o.permission);
 const physicalAllowed=()=>has('ReadMarketData','ReadTrades','ReadFacilityQuantities'),transportAllowed=()=>has('ReadMarketData','ReadTrades','ReadGasOperations');
 const available=(next:Section)=>next==='trades'?has('ReadTrades','ReadMarketData'):next==='gas-delivery'||next==='outlook'||next==='transport'||next==='route'?transportAllowed():next==='crude-delivery'?has('ReadMarketData','ReadTrades'):physicalAllowed();
 const initial=():Section=>physicalAllowed()?'quantities':transportAllowed()?'transport':available('crude-delivery')?'crude-delivery':'quantities';
 let section:Section=initial();const consumers={} as Record<Section,Consumer>;
 const unique=()=>[...new Set(Object.values(consumers))];
 const othersPending=(own:Consumer|undefined)=>unique().some(consumer=>consumer!==own&&consumer.unconfirmed());
 const quantities=setupFacilityQuantities({...o,busy:()=>o.busy()||!consumers.quantities?.unconfirmed()&&othersPending(consumers.quantities)});
 consumers.quantities=quantities;
 const operations=setupFacilityOperations(hosts.activity,{...o,pendingElsewhere:()=>othersPending(operations),openAllocations:async id=>{if(!await operations.leave())return;section='quantities';show();await quantities.load(id,false);o.changed();}});
 consumers.definitions=consumers.activity=operations;
 const transport=setupTransportCapacity(hosts.transport,{...o,pendingElsewhere:()=>othersPending(transport)});consumers.transport=transport;
 const routes=setupTransportRoutes(hosts.route,{...o,pendingElsewhere:()=>othersPending(routes)});consumers.route=routes;
 const gas=setupDeliveryWorkbench(hosts['gas-delivery'],{...o,pendingElsewhere:()=>othersPending(gas)},'gas-transport');consumers['gas-delivery']=gas;
 const crude=setupDeliveryWorkbench(hosts['crude-delivery'],{...o,pendingElsewhere:()=>othersPending(crude)},'crude-receipt');consumers['crude-delivery']=crude;
 consumers.outlook=setupPipelineOutlook(hosts.outlook,o,next=>void o.act(async()=>{section=next;show();await active().load();o.changed();}));
 consumers.trades=setupFacilityTrades(hosts.trades,o);
 const active=()=>consumers[section];
 // Tabs are built in the order of the sidebar's views (facilities-views.ts), each view's sections together.
 const sections:[Section,string][]=facilityViews.flatMap(view=>facilityViewSections[view].map(id=>[id,facilitySectionNames[id]] as [Section,string]));
 const picker=document.createElement('select');picker.id='facilities-section-picker';picker.setAttribute('aria-label','Facilities workspace');picker.setAttribute('aria-controls',content.id);
 navigation.before(picker);
 function chooseSection(next:Section){
  tabs.mark(section);picker.value=section;
  if(next===section||o.busy()||!available(next))return;
  void o.act(async()=>{if(!await active().leave())return;section=next;show();await active().load();o.changed();});
 }
 const tabs=setupTabs(navigation,sections,{label:'Facilities workspace',panel:content,selected:section,select:next=>chooseSection(next as Section)});
 picker.onchange=()=>chooseSection(picker.value as Section);
 for(const [id]of sections)navigation.querySelector<HTMLButtonElement>('#'+navigation.id+'-'+id)!.dataset.facilitiesView=id;
 const tab=(id:Section)=>navigation.querySelector<HTMLButtonElement>('#'+navigation.id+'-'+id)!;
 // The tab row and the picker list the sections of the view the shown section belongs to. The other tabs stay in the document,
 // hidden, so an exact section can still be opened from elsewhere; the view then follows it.
 function show(){
  const listed=facilityViewSections[facilityViewOf(section)];
  tabs.mark(section);
  for(const [id]of sections)tab(id).hidden=!listed.includes(id);
  navigation.hidden=picker.hidden=listed.length<2;
  picker.replaceChildren(...listed.map(id=>new Option(facilitySectionNames[id],id)));picker.value=section;
  for(const [id,host]of Object.entries(hosts))host.hidden=id!==(section==='definitions'?'activity':section);if(section==='definitions'||section==='activity')operations.show(section);
 }
 const firstOf=(view:FacilityView)=>facilityViewSections[view].find(available)??null;
 show();
 return{
  canReadTrades:()=>available('trades'),
  async open(next:Exclude<Section,'quantities'>,scopeId?:string){
   if(!available(next))throw Error('Current permission for this operations workspace is required.');
   const actor=o.identity();if(!await active().leave()||actor!==o.identity())return;
   section=next;show();await active().load(scopeId);o.changed();
  },
  async load(ledgerId?:string,rememberSelection=true){for(const consumer of unique())consumer.sync();
   if(ledgerId!==undefined){if(!physicalAllowed())throw Error('Facility quantity read permission is required.');section='quantities';show();await quantities.load(ledgerId,rememberSelection);}
   else{const recovering=sections.find(([id])=>consumers[id].unconfirmed());if(recovering)section=recovering[0]==='definitions'?operations.recoverySection():recovering[0];else if(!available(section))section=initial();show();await active().load();}
  },
  sync(){for(const consumer of unique())consumer.sync();picker.disabled=o.busy();for(const item of picker.options)item.disabled=!available(item.value as Section);for(const button of navigation.querySelectorAll<HTMLButtonElement>('button'))button.disabled=o.busy()||!available(button.dataset.facilitiesView as Section);},
  section:()=>section,selected:()=>section==='quantities'?quantities.selected():null,
  view:()=>facilityViewOf(section),
  viewAvailable:(view:FacilityView)=>facilityViewSections[view].some(available),
  // Arriving from another page: shows the view's first permitted section without reading. The page's own load reads it.
  prepareView(view:FacilityView){const next=firstOf(view);if(next&&facilityViewOf(section)!==view){section=next;show();}},
  // On the page: moves to the view through the shown section's leave guard, as a tab does.
  chooseView(view:FacilityView){const next=firstOf(view);if(next&&facilityViewOf(section)!==view)chooseSection(next);},
  edited:()=>unique().some(consumer=>consumer.edited()),leave:()=>active().leave(),refresh:()=>active().refresh(),
  reset(){for(const consumer of unique())consumer.reset();if(!available(section))section=initial();show();},
 };
}
