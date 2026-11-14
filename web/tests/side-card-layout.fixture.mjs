// Runs in an isolated static page: native editors, synthetic choices, no API writes.
export async function sideCardLayoutChecks() {
 const results=[];
 const check=(name,condition)=>results.push({name,passed:!!condition});
 const settle=()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
 const source=new DOMParser().parseFromString(await (await fetch('/index.html')).text(),'text/html');
 const host=document.createElement('div');host.id='side-panel';host.className='side-panel-form details-form';host.style.cssText='width:960px;max-width:100%;margin:20px auto';document.body.append(host);
 const panel=(id)=>{const node=document.createElement('section');node.id=id;host.append(node);return node;};
 const {setupNewTrade,newTradeProducts}=await import('/new-trade.js');
 const parts=Object.fromEntries(['head','body','view'].map(key=>[key,panel('entry-test-'+key)])),chosen=[];
 const entry=setupNewTrade({element:key=>parts[key]},{choose:product=>chosen.push(product),busy:()=>false});
 entry.show({kind:'new',product:null},'choose');
 for(const button of parts.head.querySelectorAll('#new-trade-product button'))button.click();
 check('Every instrument type is available before ownership is entered',JSON.stringify(chosen)===JSON.stringify(newTradeProducts.map(([key])=>key)));
 const o={api:async()=>{throw Error('Unexpected API request');},act:async work=>work(),source:()=>({snapshotId:'synthetic'}),historical:()=>false,actor:()=>'synthetic',zone:()=>'UTC',busy:()=>false,canBook:()=>true};
 const {setupPhysicalCrude}=await import('/physical-crude-editor.js');
 panel('crude-page');setupPhysicalCrude(o);
 const {setupPhysicalLng}=await import('/physical-lng-editor.js');
 panel('lng-page');setupPhysicalLng(o);
 const swap=source.querySelector('#swap-editing');swap.hidden=false;host.append(swap);
 for(const id of ['swap-fixed-template','swap-floating-template'])host.append(source.getElementById(id));
 const {mountSwapSideEditor,mountFirstFloatingEditor,arrangeSwapSections}=await import('/swap-side-editor.js');
 mountSwapSideEditor('fixed');mountSwapSideEditor('floating');mountFirstFloatingEditor();arrangeSwapSections();
 const {tradeAnnotations}=await import('/trade-annotations.js');tradeAnnotations('swap',o.zone);
 const before=(first,last)=>!!(first.compareDocumentPosition(last)&Node.DOCUMENT_POSITION_FOLLOWING);
 check('Swap instrument terms precede ownership',before(document.getElementById('swap-instrument'),document.getElementById('swap-trade-terms')));
 check('Futures instrument terms precede ownership',before(source.getElementById('futures-instrument'),source.getElementById('futures-trade-terms')));
 check('Crude instrument terms precede ownership',before(document.getElementById('crude-side-cards'),document.getElementById('crude-book')));
 check('LNG instrument terms precede ownership',before(document.getElementById('lng-cargo-side'),document.getElementById('lng-book')));
 check('Crude references precede dependent quantity entry',before(document.getElementById('crude-commodity'),document.getElementById('crude-quantity')));
 check('Swap commodity precedes dependent delivery choices',before(document.getElementById('swap-commodity'),document.getElementById('swap-floating-from')));
 check('Booking has no numbered step badges',swap.querySelector('.entry-step')===null&&['none','normal','""'].includes(getComputedStyle(document.querySelector('#swap-trade-terms>h3'),'::before').content));
 const ppa=panel('ppa-page'),deck=document.createElement('div');ppa.className='embedded-trade';deck.className='ppa-components entry-side-deck';ppa.append(deck);
 const {componentEditor}=await import('/ppa-components.js');
 const catalog={references:{locations:[],quantityGroups:{contract:[],energy:[],capacity:[],certificate:[]},units:[],calendars:[],priceIndices:[]},priceKinds:['flat','escalated','stepped'],definitionsMetadata:{priceCurves:[],volumeSources:[],generationAssets:[]},authoringPolicies:{escalationBases:[],certificateEligibility:[],certificateHolding:[],certificateDisposition:[],capacityRules:[],quantityStages:[],hourMemberships:[]},paymentOptions:{frequencies:[],rules:[],dayBases:[],shortMonths:[],adjustments:[]},contextTouVersions:[]};
 for(const [index,kind] of ['energy','fixed','indexed'].entries()){
  const card=document.createElement('section');deck.append(card);
  componentEditor(card,catalog,{role:kind,ordinal:1,sideKind:kind,sideKinds:[kind],linkedSlots:[],quantityKinds:['volume'],linkRoles:[]},index,()=>{},undefined,{delivery:()=>({clock:'synthetic',cadence:'60'}),links:()=>[]});
 }
 // The normal PPA editor prepares this deck; this fixture mounts its components directly.
 const {prepareSideDeck}=await import('/side-card.js');prepareSideDeck(deck);
 const gas=panel('gas-test');
 const {renderTicket}=await import('/deal-ticket.js'),{newTicket}=await import('/ticket-model.js');
 const common=band=>({id:band,label:band,type:'text',unit:null,state:'editable',required:false,band,choices:null,dependsOn:[],lookup:null,soon:null,rule:null});
 const description={product:'synthetic_flow',version:1,rowShape:'flow',tradeFields:[],sideLimits:[],tradeColumns:[],levels:['month'],tabs:[],modes:['new'],
  sides:['physical','cash'].map(kind=>({kind,pill:kind,colour:kind==='physical'?'gas':'cash',min:1,initial:1,max:1,fields:['direction','start','end','quantity','price','payment'].map(common),columns:[]}))};
 renderTicket(gas,{state:newTicket(description),mode:'new',actions:null,schedule:null,quantityUnit:null,instant:at=>at,previous:null,summaries:[],links:[],entry:null,status:{kind:'current',text:'Synthetic fixture'},fieldErrors:{},choices:{},selectedSide:null,tab:'instrument',level:'month',closed:new Set(),on:new Proxy({},{get:()=>()=>{}})});
 const shelf=panel('editor-shelf'),scheduledHost=panel('scheduled-test');shelf.hidden=true;
 const {setupScheduledSwapEditor}=await import('/scheduled-swap-editor.js');
 const {setCurveNames}=await import('/market-view.js');setCurveNames([{code:'synthetic',name:'Synthetic gas'}]);
 const quantity={catalogId:'synthetic',revisionId:'synthetic',code:'energy'};
 const swapCatalog={bookingActor:{name:'Synthetic trader'},books:[],legalEntities:[],counterparties:[],commodities:[{code:'gas',name:'Gas'}],context:{},canBook:true,
  indices:[{commodity:'gas',definitionRevisionId:'synthetic',curve:'synthetic',index:'synthetic',quantityChoices:[{specification:quantity,unit:'MMBtu'}],samplings:[],customProjections:[]}],
  paymentCalendars:[{definitionRevisionId:'synthetic',code:'synthetic_calendar'}],paymentOptions:{rules:[],dayBases:[],shortMonths:[],adjustments:[{value:'following',label:'Following'}]},scheduledOptions:{powerQuantities:[],clocks:[]},entryTemplate:{payment:{}},factorOptions:{}};
 let jobs=[];
 const scheduled=setupScheduledSwapEditor({...o,context:()=>({}),act:work=>{const job=work();jobs.push(job);return job;},api:async(path,body)=>{
  if(path==='swaps/catalog')return swapCatalog;
  if(path==='swaps/scheduled-periods')return{periods:body.from==='2031-12-01'?[{deliveryFrom:'2031-12-01',deliveryTo:'2032-01-01'}]:[{deliveryFrom:'2031-11-01',deliveryTo:'2031-12-01'},{deliveryFrom:'2031-12-01',deliveryTo:'2032-01-01'}]};
  throw Error('Unexpected API request: '+path);
 },host:()=>scheduledHost,pending:()=>undefined,queue:async()=>{throw Error('No writes');},onDenied(){},unsavedChanged(){},reopen(){},onSettlement(){},ask:async()=>true});
 await scheduled.openNew();
 const choose=(id,value)=>{const field=document.getElementById(id);field.value=value;field.dispatchEvent(new Event('change',{bubbles:true}));};
 choose('scheduled-kind','fixed_floating');choose('scheduled-commodity','gas');choose('scheduled-market','synthetic');
 const scheduledCards=[...scheduledHost.querySelectorAll('.entry-side-card')];
 for(const card of scheduledCards){card.querySelector('[data-role=delivery-from]').value='2031-11-01';card.querySelector('[data-role=delivery-to]').value='2032-01-01';[...card.querySelectorAll('button')].find(b=>b.textContent==='Generate monthly period rows').click();await Promise.all(jobs);jobs=[];}
 const box=selector=>document.querySelector(selector).getBoundingClientRect();
 const sameTop=(...selectors)=>{const boxes=selectors.map(box);return boxes.every(b=>b.width>0&&b.height>0)&&Math.max(...boxes.map(b=>b.top))-Math.min(...boxes.map(b=>b.top))<1;};
 await settle();
 check('LNG sides are next to each other',sameTop('#lng-cargo-side','#lng-cash-side')&&box('#lng-cash-side').left>box('#lng-cargo-side').left);
 check('Crude directions align',sameTop('#crude-direction','#crude-cash-direction'));
 check('LNG directions align',sameTop('#lng-direction','#lng-considerationDirection'));
 check('Swap delivery starts align',sameTop('#swap-fixed-from','#swap-floating-from'));
 check('Swap payment rules align',sameTop('#swap-fixed-payment-rule','#swap-floating-payment-rule'));
 check('PPA directions align',sameTop('#ppa-component-0-coefficient','#ppa-component-1-coefficient','#ppa-component-2-coefficient'));
 check('PPA payment rules align',sameTop('#ppa-component-0-payment-rule','#ppa-component-1-payment-rule','#ppa-component-2-payment-rule'));
 check('PPA quantity editors retain their controls',deck.querySelectorAll('.ppa-side-quantity [data-q=kind]').length===3);
 for(const band of ['direction','start','quantity','price','payment'])check('Gas '+band+' rows remain aligned',sameTop(`#gas-test [data-kind=physical] [data-band=${band}]`,`#gas-test [data-kind=cash] [data-band=${band}]`));
 const tops=nodes=>{const boxes=nodes.map(node=>node.getBoundingClientRect());return boxes.every(b=>b.width>0&&b.height>0)&&Math.max(...boxes.map(b=>b.top))-Math.min(...boxes.map(b=>b.top))<1;};
 check('Scheduled contract starts align',tops(scheduledCards.map(card=>card.querySelector('[data-role=delivery-from]'))));
 check('Scheduled quantity units align',tops(scheduledCards.map(card=>card.querySelector('[data-role=quantity-unit]'))));
 for(let i=0;i<2;i++){
  check('Scheduled period '+(i+1)+' quantities align',tops(scheduledCards.map(card=>card.querySelectorAll('[data-role=period-amount]')[i])));
  const payments=scheduledCards.map(card=>[...card.querySelectorAll('.scheduled-period')][i].querySelector(':scope > fieldset select'));
  check('Scheduled period '+(i+1)+' payment rules align',tops(payments));
 }
 const payments=scheduledCards.map(card=>card.querySelector('.scheduled-period > fieldset'));
 const labelled=(parent,text)=>[...parent.querySelectorAll('select')].find(field=>field.labels[0].textContent===text);
 for(const payment of payments){const adjustment=labelled(payment,'Date adjustment');adjustment.value='following';adjustment.dispatchEvent(new Event('change',{bubbles:true}));}
 const calendars=payments.map(payment=>labelled(payment,'Payment calendar'));await settle();
 check('Each holiday-adjusted side requires its own calendar',calendars.every(field=>field.required&&!field.disabled));
 check('Payment calendar fields align',tops(calendars));
 const firstAmounts=scheduledCards[0].querySelectorAll('[data-role=period-amount]');firstAmounts[1].value='125';
 scheduledCards[1].querySelector('[data-role=delivery-from]').value='2031-12-01';
 [...scheduledCards[1].querySelectorAll('button')].find(b=>b.textContent==='Generate monthly period rows').click();await settle();await Promise.all(jobs);jobs=[];
 check('Independent periods align by matching dates',tops([firstAmounts[1],scheduledCards[1].querySelector('[data-role=period-amount]')]));
 check('Relayout preserves independent quantities',firstAmounts[1].value==='125'&&scheduledCards[1].querySelector('[data-role=period-amount]').value==='');
 const fee=document.getElementById('crude-handling-side');fee.hidden=false;await settle();
 check('Optional fee direction aligns when added',sameTop('#crude-direction','#crude-cash-direction','#crude-fee-direction'));
 // Resizing must retain existing controls, values and handlers.
 const price=document.getElementById('crude-price');price.value='3.50';let edits=0;price.addEventListener('input',()=>edits++);
 host.style.width='380px';await settle();
 for(const [name,first,last] of [['LNG','#lng-cargo-side','#lng-cash-side'],['crude','#crude-delivery-side','#crude-consideration-side'],['swap','#swap-fixed-editor .instrument-side','#swap-floating-editor .instrument-side'],['PPA','.ppa-component:first-child','.ppa-component:last-child']]){
  check(name+' stacks in a narrow panel',box(last).top>=box(first).bottom&&Math.abs(box(last).left-box(first).left)<1);
 }
 check('Scheduled sides stack in a narrow panel',scheduledCards[1].getBoundingClientRect().top>=scheduledCards[0].getBoundingClientRect().bottom);
 check('Gas sides stack in a narrow panel',box('#gas-test [data-kind=cash]').top>=box('#gas-test [data-kind=physical]').bottom);
 check('Narrow panels do not overflow',host.scrollWidth<=host.clientWidth+1);
 host.style.width='960px';await settle();price.dispatchEvent(new Event('input',{bubbles:true}));
 check('Responsive layout preserves controls and edits',document.getElementById('crude-price')===price&&price.value==='3.50'&&edits===1);
 ppa.id='ppa-components-fixture';panel('ppa-page');
 const {setupPpa}=await import('/ppa-editor.js');setupPpa({...o,can:()=>true});
 check('PPA composition and sides precede ownership',before(document.getElementById('ppa-composition'),document.getElementById('ppa-book'))&&before(document.getElementById('ppa-components'),document.getElementById('ppa-book')));
 panel('option-page');const {setupOptionTicket}=await import('/futures-option-view.js');setupOptionTicket({...o,instant:at=>at,onBooked:async()=>{},surface(){}});
 check('Option contract and execution precede ownership',before(document.getElementById('option-strike'),document.getElementById('option-book'))&&before(document.getElementById('option-premium'),document.getElementById('option-book')));
 const report=document.createElement('section');report.id='layout-check-results';
 const title=document.createElement('h2');title.textContent=`${results.filter(r=>r.passed).length} layout checks passed; ${results.filter(r=>!r.passed).length} failed`;report.append(title);
 for(const result of results){const line=document.createElement('p');line.textContent=(result.passed?'PASS: ':'FAIL: ')+result.name;report.append(line);}
 document.body.prepend(report);
 return results;
}
