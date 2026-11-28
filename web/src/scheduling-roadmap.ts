import {schedulingDesks,type DeskId} from './navigation-groups.js';

type PlannedWorkflow={title:string;scope:string};
// Descriptions of missing workflows, never capabilities, permissions or executable contracts.
// Shipping appears on the desks that need it; these entries do not select a shared financial policy.
const shipping:readonly PlannedWorkflow[]=[
 {title:'Voyages & freight',scope:'Vessels, port calls, loading and discharge windows, voyage costs and freight agreements.'},
 {title:'Time charters',scope:'Vessel hire periods, delivery and redelivery, hire payments, off-hire events and bunker obligations.'},
 {title:'Laytime & demurrage',scope:'Statements of facts, agreed time allowances, exceptions and reviewed delay claims.'},
];
const workflows:Record<DeskId,readonly PlannedWorkflow[]>={
 gas:[
  {title:'Pipeline connections',scope:'Send nominations to an operator and reconcile acknowledgements with the existing gas workflow.'},
  {title:'Storage statements',scope:'Import operator statements and reconcile them with booked storage rights and recorded facility quantities.'},
 ],
 power:[
  {title:'Schedules & dispatch',scope:'Submit energy schedules and reconcile dispatch instructions with metered delivery under an explicit market contract.'},
  {title:'Transmission scheduling',scope:'Transmission reservations, interchange schedules and curtailments for a selected market.'},
 ],
 crude:[
  {title:'Cargo parcels & liftings',scope:'Identify cargo lots, grades and owners; track lifting windows, split or combined shipments, and allocations to trades.'},
  {title:'Pipeline movements',scope:'Nominations, capacity, batch movements, linefill and receipt-to-delivery reconciliation under an explicit pipeline contract.'},
  {title:'Tank inventory & blending',scope:'Tank positions across trades, quality evidence, transfers and blends with explicit ownership and measurement rules.'},
  ...shipping,
  {title:'Rail & truck movements',scope:'Movement tickets, loading and unloading, custody evidence and carrier charges.'},
 ],
 lng:[
  {title:'Cargo actuals & reconciliation',scope:'Loading and discharge evidence, quantity and quality measurements, losses and reconciliation to booked cargo terms.'},
  {title:'Terminal operations',scope:'Berth windows, terminal nominations, tank inventory and regasification under supported terminal contracts.'},
  ...shipping,
 ],
};

const element=<K extends keyof HTMLElementTagNameMap>(tag:K,text:string)=>{const node=document.createElement(tag);node.textContent=text;return node;};

/** Static contribution guidance for the desk actually shown. Opening it never navigates or reads business data. */
export function setupSchedulingRoadmap(root:HTMLElement){
 let shown:DeskId|null=null;
 root.classList.add('scheduling-roadmap');root.hidden=true;
 return {show(desk:DeskId|null){
  root.hidden=desk===null;
  if(shown===desk)return;
  shown=desk;
  const expanded=root.querySelector('details')?.open??false;
  root.replaceChildren();if(desk===null)return;
  const details=document.createElement('details');details.open=expanded;
  const summary=element('summary',schedulingDesks.find(d=>d.id===desk)!.label+' · Planned workflows');
  const count=element('span',workflows[desk].length+' TBD');count.className='roadmap-count';summary.append(count);
  const body=document.createElement('div');body.className='scheduling-roadmap-body';
  body.append(element('p','These workflows are not implemented. Each needs a scoped design and tests before it can be used.'));
  const list=document.createElement('ul');list.className='scheduling-roadmap-list';
  for(const workflow of workflows[desk]){
   const item=document.createElement('li'),heading=document.createElement('div'),status=element('span','TBD');
   heading.className='workflow-heading';status.className='workflow-status';
   heading.append(element('h3',workflow.title),status);item.append(heading,element('p',workflow.scope));list.append(item);
  }
  const contribute=document.createElement('div');contribute.className='roadmap-contribute';
  contribute.append(element('strong','Contribute a workflow'),element('p','Pick one area and bring a synthetic example: who uses it, what they record and the expected result. Propose the scope and acceptance tests before implementation.'));
  const guide=element('p','Installer and contributor guide: ');guide.append(element('code','docs/scheduling-roadmap.md'),document.createTextNode(' in the source distribution.'));
  contribute.append(guide);body.append(list,contribute);details.append(summary,body);root.append(details);
 }};
}
