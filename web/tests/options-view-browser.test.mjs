import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

// The Options page drawn in a real browser over synthetic reads: no application runtime, account or database is attached.
const page='<link rel="stylesheet" href="/app.css"><section id="options-page"></section>';
const setup=`
 const {setupOptions}=await import('/options-view.js');
 const {prepareAmountDisplay,shownAmounts}=await import('/amount-display.js');
 const {decimalText}=await import('/vol-surface-view.js');
 const fixtureFormat=r=>{let [w,f]=decimalText(r.value,{places:r.decimalPlaces}).split('.');if(r.trimTrailingZeros)f=f?.replace(/0+$/,'');return w.replace(/\\B(?=(\\d{3})+$)/g,',')+(f?'.'+f:'');};
 const expiry='2027-01-26T19:30:00+00:00',model='smile_quadratic_log_moneyness_v1';
 const measure=(value,missing=null)=>({value,missing});
 const analytics=delta=>({value:'0.167',priceBump:'0.012',delta:measure(delta),gamma:measure('1.987955334753591876'),vega:measure('0.001634163476493991'),theta:measure('-0.00412')});
 const series=(code,right,strike,inTheMoney,extra={})=>({series:{code,right,strike,expiryAt:expiry,underlyingCode:'ng_f27',settlement:'0.1670',outcome:'solved',marketVolatility:'0.4512'},
  logMoneyness:'0',fitted:'0.45',richCheap:'0.0012',fitPoint:true,inTheMoney,analytics:analytics(right==='call'?'0.523':'-0.477'),...extra});
 const surface={modelId:model,strikeAxis:['3','3.25'],
  sensitivities:{policy:'option_sensitivities_layers_v1',ticks:1,priceSteps:2,volatilityBump:'0.01',clockSource:'request',clockZone:'UTC',dayTo:'2026-10-17T17:00:00+00:00'},
  expiries:[{expiryAt:expiry,underlyingCode:'ng_f27',futures:'3.01',status:'fitted',parameters:{a:'0.45',b:'-0.1',c:'0.8'},points:5,rmsResidual:'0.001',maxAbsResidual:'0.002',grid:[],atTheMoneyStrike:'3',daysToExpiry:'102.104'}],
  series:[series('c3','call','3',false),series('p3','put','3',false),series('c325','call','3.25',false,{analytics:{...analytics('0.3'),vega:measure(null,'sensitivity_unstable')}}),series('p325','put','3.25',true)]};
 const figure=(amount,missing=null)=>({amount,missing});
 const reference={catalogId:'synthetic',revisionId:'r'};
 const positions={model,held:[{series:'c3',seriesReference:{revision:reference,code:'c3'},openLots:'2'},{series:'c3',seriesReference:{revision:{...reference,revisionId:'other'},code:'c3'},openLots:'99'}],totals:[{currency:'USD',value:figure('4000.000000000000000000'),theta:figure(null,'theta_days_differ')}],
  groups:[{underlyingSeries:'ng_f27',expiryAt:expiry,product:'synthetic_on',productName:'Synthetic gas options',definitionRevisionId:'definition-held',currency:'USD',priceUnit:'USD/MMBtu',quantityUnit:'MMBtu',
    sensitivities:surface.sensitivities,
    rows:[{tradeId:'t1',bindingId:'b1',tradeReference:'TR-000001',bookId:'bk',book:'Synthetic options',series:'c3',right:'call',strike:'3',openLots:'2',state:'analysed',reason:null,
      value:figure('3340.000000000000000000'),delta:figure('10460.000000000000000000'),gamma:figure('39759.106695071837520000'),vega:figure('32.6833269529879820000'),theta:figure('-82.4000000000000000000')}],
    subtotal:{value:figure('3340.000000000000000000'),delta:figure('10460.000000000000000000'),gamma:figure('39759.106695071837520000'),vega:figure('32.6833269529879820000'),theta:figure('-82.4000000000000000000')}},
   {underlyingSeries:'cl_g27',expiryAt:'2027-02-23T19:30:00+00:00',product:'synthetic_lo',productName:'Synthetic crude options',definitionRevisionId:'definition-other',currency:'USD',priceUnit:'USD/MMBtu',quantityUnit:'bbl',sensitivities:null,
    rows:[{tradeId:'t2',bindingId:'b2',tradeReference:'TR-000002',bookId:'bk',book:'Synthetic options',series:'lo70',right:'call',strike:'70',openLots:'4',state:'notCalibrated',reason:'not_calibrated',
      value:figure(null,'not_calibrated'),delta:figure(null,'not_calibrated'),gamma:figure(null,'not_calibrated'),vega:figure(null,'not_calibrated'),theta:figure(null,'not_calibrated')}],
    subtotal:{value:figure(null,'incomplete'),delta:figure(null,'incomplete'),gamma:figure(null,'incomplete'),vega:figure(null,'incomplete'),theta:figure(null,'incomplete')}}]};
 // What a strategy on the expiry may be built from, and an analysis that answers the legs and scenario it is sent.
 const at='2026-10-16T17:00:00+00:00',nextDay='2026-10-17T17:00:00+00:00',limits={legs:4,points:41,curves:3,lots:100000,volatilityShiftPoints:100};
 const offered={prices:{lower:'2.6',upper:'3.4',points:21},pointCounts:[11,21,41],instants:[at,nextDay],instantsComplete:true,selectedInstants:[at],volatilityShiftPoints:'0',
  valuation:{observationRole:'daily_settlement',numericalPolicy:'american_futures_option_crr_v1',scenarioPolicy:'option_implied_volatility_held_v1',calibrationFallback:'intrinsic'},
  sensitivities:{policy:'option_sensitivities_layers_v1',bump:{ticks:1,priceSteps:2},volatilityBump:'0.01',clock:{code:'UTC'}},expiryRule:'option_expiry_exercise_value_v1'};
 const strategySetup={product:'synthetic_on',model,expiryAt:expiry,state:'fitted',limits,underlyingSeries:'ng_f27',futures:'3.01',atTheMoneyStrike:'3',templateRule:'listed_strike_offsets_from_at_the_money_v1',
  clock:{source:'surface',zone:'UTC'},offered,templates:[
   {id:'straddle',name:'Straddle',refusal:null,legs:[{series:'c3',right:'call',strike:'3',side:'buy',lots:1,entryPrice:'0.1670'},{series:'p3',right:'put',strike:'3',side:'buy',lots:1,entryPrice:'0.1670'}]},
   {id:'iron_condor',name:'Iron condor',legs:null,refusal:{code:'strategy_template_strike',message:'Iron condor needs a put 4 listed strikes below 3, and the expiry lists none there.'}}]};
 const analysed=body=>({product:body.product,expiryAt:body.expiryAt,valuationAt:at,underlyingSeries:'ng_f27',currency:'USD',priceUnit:'USD/MMBtu',quantityUnit:'MMBtu',futures:'3.01',
  request:{prices:body.prices,instants:body.instants,volatilityShiftPoints:body.volatilityShiftPoints},
  legs:body.legs.map(leg=>({series:leg.series,right:leg.series[0]==='c'?'call':'put',strike:'3',side:leg.side,lots:String(leg.lots),entryPrice:leg.entryPrice,multiplier:'10000',premium:'1670',
   state:'solved',reason:null,settlement:'0.167',impliedVolatility:'0.4512',value:'0.167',delta:measure(leg.series[0]==='c'?'0.523':'-0.477')})),
  expiry:{rule:'option_expiry_exercise_value_v1',netPremium:{amount:'3340',direction:'debit'},maximumProfit:{amount:null,bounded:false},maximumLoss:{amount:'-3340',bounded:true},
   breakevens:[{lower:'2.666',upper:'2.666'},{lower:'3.334',upper:'3.334'}],curve:[{price:'2.6',profitAndLoss:'660'},{price:'3',profitAndLoss:'-3340'},{price:'3.4',profitAndLoss:'660'}]},
  dated:refuseDated?{state:'refused',reason:'option_at_intrinsic',message:'A leg is valued at intrinsic and has no volatility, so the strategy has no dated curve.',curves:[]}:
   {state:'valued',reason:null,message:null,curves:body.instants.map(instant=>({at:instant,points:[{price:'2.6',value:'4400',profitAndLoss:'1060'},{price:'3',value:'3340',profitAndLoss:'0'},{price:'3.4',value:'4500',profitAndLoss:'1160'}]}))},
  strategy:{value:figure('3340'),delta:figure('460'),gamma:figure('39759.1'),vega:figure(null,'incomplete'),theta:figure('-82.4'),dayTo:nextDay},
  policies:{sensitivities:{policy:'option_sensitivities_layers_v1'},expiryRule:'option_expiry_exercise_value_v1'},limits,work:{calibrationTreePrices:60,valuationTreePrices:120,treePrices:180,curveSets:4,elapsedMs:900}});
 const analyses=[],signals=[],unhandled=[];let refuseDated=false,refuseAnalysis=false,holdAnalysis=null;
 addEventListener('unhandledrejection',event=>unhandled.push(String(event.reason)));
 const calls=[],opened=[],booked=[],contracts=[];let fitted=true,failPositions=false,canBook=true,snapshotId='synthetic-close',holdPositions=null,closeAt=at,missingSource=null;
 const optionProducts=[{code:'synthetic_on',name:'Synthetic gas options',underlying_product_code:'ng',exchange_code:'alpha'},
  {code:'synthetic_lo',name:'Synthetic crude options',underlying_product_code:'cl',exchange_code:'beta'}];
 const references={futures_products:[{code:'ng',name:'Gas future',commodity_code:'gas'},{code:'cl',name:'Crude future',commodity_code:'oil'}],
  exchanges:[{code:'alpha',name:'Alpha Exchange'},{code:'beta',name:'Beta Exchange'}],commodities:[{code:'gas',name:'Natural gas'},{code:'oil',name:'Crude oil'}]};
 const listing={expiryAt:expiry,underlyingCode:'ng_f27',name:'Gas future',deliveryFrom:'2027-01-01',deliveryTo:'2027-02-01',
   contractId:'future-jan',curves:['gas_curve'],settlementMethod:'physical',openForBooking:true};
 const listedSeries=surface.series.map(s=>({code:s.series.code,right:s.series.right,strike:s.series.strike,openForBooking:s.series.code!=='c325'}));
 const api=async(path,body,signal)=>{
  calls.push(path.split('?')[0]);
  if(path.startsWith('reference/catalog')){
   const revision=new URL(path,location.href).searchParams.get('definitionRevisionId')??'definition-a';
   if(revision===missingSource)throw Object.assign(Error('The selected inputs omit this exact source.'),{code:'market_inputs_missing'});
   return {definitionRevisionId:revision,rows:optionProducts,references,hasMore:false};
  }
  if(path==='options/positions'){if(holdPositions)return holdPositions;if(failPositions)throw Object.assign(Error('The positions could not be read.'),{code:'synthetic_failure'});await prepareAmountDisplay(shownAmounts(path,positions),async requests=>requests.map(fixtureFormat));return positions;}
  if(path==='options/listings'){
   if(body.expiryAt&&Date.parse(body.expiryAt)!==Date.parse(listing.expiryAt)||body.underlyingCode&&body.underlyingCode!==listing.underlyingCode||
    body.series&&!listedSeries.some(s=>s.code===body.series))
    throw Object.assign(Error('The selected option is unavailable at this close in this definition.'),{code:'option_definition'});
   return {snapshotId,definitionRevisionId:body.definitionRevisionId,product:body.product,
    expiries:[{expiryAt:listing.expiryAt,underlyingCode:listing.underlyingCode}],underlying:listing,series:listedSeries,canBook};
  }
  if(path==='vol-surfaces/axes')return {definitionRevisionId:'definition-a',snapshotId,model,initialAxis:'server_axis',axes:[{id:'server_axis',preset:{points:[{value:'0'}]}}]};
  if(path==='vol-surfaces/read')return {referenceRevision:reference,priceUnit:'USD/MMBtu',definitionRevisionId:'definition-a',snapshotId,model,state:fitted?'fitted':'notFitted',job:null,surface:fitted?surface:null};
  if(path==='options/strategy/setup')return {...strategySetup,product:body.product,expiryAt:body.expiryAt};
  if(path==='options/strategy/analyze'){
   analyses.push(body);signals.push(signal);if(holdAnalysis)await holdAnalysis;
   if(refuseAnalysis)throw Object.assign(Error('Choose a positive lower bound, a higher upper bound and at least two points whose even spacing is an exact decimal.'),{code:'strategy_price_range'});
   const answer=analysed(body);await prepareAmountDisplay(shownAmounts(path,answer),async requests=>requests.map(fixtureFormat));return answer;
  }
  throw Error('unexpected read '+path);
 };
 const options=setupOptions({api,context:()=>({snapshotId,convention:'futures_option_premium_v1',knowledgeMode:'current'}),zone:()=>'UTC',
  frames:{here:()=>({closeId:null,at:closeAt,close:null}),sessionName:code=>code},
  openSurface:(product,definition,series)=>opened.push([product,definition,series??null]),book:(series,definition)=>booked.push([series,definition]),
  openContract:(curve,id,definition)=>contracts.push([curve,id,definition]),bookFuture:(curve,id,definition)=>booked.push([curve,id,definition])});
 const $=selector=>document.querySelector(selector),all=selector=>[...document.querySelectorAll(selector)];
 const tick=()=>new Promise(resolve=>setTimeout(resolve,0)),state=()=>$('#options-page').dataset.state;
`;
const browser=(name,body,expected)=>test(name,{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 assert.deepEqual(await evaluate(`(async()=>{${setup}${body}})()`),expected);
}));

browser('the chain draws the stored surface: calls mirrored about the strike, the money marked, a missing figure a dash that says why',`
 await options.refresh();
 const idle={state:state(),products:all('.options-product').map(c=>c.dataset.product),chosen:all('.options-product[aria-pressed=true]').length,placeholder:!$('#options-placeholder').hidden,reads:calls.filter(c=>c==='vol-surfaces/read').length};
 all('.options-product')[0].click();await tick();await tick();
 const row=strike=>all('#options-chain tbody tr').find(r=>r.dataset.strike===strike),cells=(strike,right)=>[...row(strike).querySelectorAll('td[data-right='+right+']')].map(td=>td.textContent);
 const missing=row('3.25').querySelector('td.options-missing-figure');
 return {idle,state:state(),expiry:$('#options-expiries [aria-current=true]').dataset.expiry===expiry,strikes:all('#options-chain tbody tr').map(r=>r.dataset.strike),
  atTheMoney:all('#options-chain tr.options-atm').map(r=>r.dataset.strike),call3:cells('3','call'),put3:cells('3','put'),
  inTheMoney:all('#options-chain td.options-itm').map(td=>td.dataset.series).filter((code,i,list)=>list.indexOf(code)===i),
  missing:[missing.dataset.series,missing.textContent,missing.title],facts:$('#options-facts').textContent,
  // The hero and expiry chips are the page's own: another page's suites find theirs by class.
  own:[all('.vol-hero,.vol-eyebrow,.vol-here,.vol-close-chip').length,$('.options-hero .options-eyebrow').textContent,all('.options-expiry').length],basis:/option_sensitivities_layers_v1/.test($('#options-basis').textContent)&&/Risk reports keep theirs/.test($('#options-basis').textContent)};
`,{idle:{state:'idle',products:['synthetic_on','synthetic_lo'],chosen:0,placeholder:true,reads:0},state:'fitted',expiry:true,strikes:['3','3.25'],atTheMoney:['3'],
 call3:['2','-0.004120','0.001634','1.988','0.52','45.1%','0.167'],put3:['0.167','45.1%','-0.48','1.988','0.001634','-0.004120',''],inTheMoney:['p325'],
 missing:['c325','—','Estimates at the half, whole and double bump did not agree'],own:[0,'Working snapshot',1],facts:'Underlying futureng_f27ContractGas futureDeliveryJan 2027Settlement methodPhysical deliverySettlement3.01ExpiryJan 26, 2027, 7:30 PMDays to expiry102.1TradeBook underlying future',basis:true});

browser('a chosen series can be booked or opened on its surface, and a product with no surface says so and links to where it is calibrated',`
 await options.open('synthetic_on');await tick();
 $('#options-chain td[data-series=p325]').click();
 const readout=$('#options-readout').textContent,selected=all('#options-chain td.options-selected').length;
 $('#options-book').click();$('#options-open-surface').click();
 fitted=false;await options.refresh();await tick();
 const panel={state:state(),shown:!$('#options-missing').hidden&&/No calibrated analytics at this close/.test($('#options-missing').textContent),chain:!!$('#options-listed-chain')&&!$('.options-chain-view').hidden};
 $('#options-calibrate').click();
 return {readout:/p325/.test(readout)&&/Delta-0.477/.test(readout),selected,booked,opened,panel};
`,{readout:true,selected:7,booked:[['p325','definition-a']],opened:[['synthetic_on','definition-a','p325'],['synthetic_on','definition-a',null]],panel:{state:'notFitted',shown:true,chain:true}});

browser('positions list the rows, subtotals and totals the backend states with their units, and a product with no surface links to its own definition',`
 await options.refresh();$('#options-view-positions').click();
 const body=underlying=>all('#options-position-table tbody').find(b=>b.dataset.underlying===underlying),text=node=>[...node.querySelectorAll('td,th')].map(c=>c.textContent);
 const gas=body('ng_f27'),crude=body('cl_g27');
 crude.querySelector('.options-position-calibrate').click();
 return {state:state(),products:$('#options-products').hidden,note:gas.querySelector('.options-position-group').textContent,
  row:text(gas.querySelector('tr[data-series=c3]')),subtotal:text(gas.querySelector('.options-position-subtotal')),
  uncalibrated:[crude.querySelector('tr[data-series=lo70]').dataset.state,...text(crude.querySelector('tr[data-series=lo70]')).slice(3)],
  total:text($('#options-position-table .options-position-total')),totalWhy:$('#options-position-table .options-position-total td.options-missing-figure').title,opened};
`,{state:'positions',products:true,note:'ng_f27Synthetic gas options · expires Jan 26, 2027, 7:30 PM · money in USD, delta and gamma in MMBtu · theta for the day to Oct 17, 2026, 5:00 PM',
 row:['TR-000001','Synthetic options','c3','2','3,340.000','10,460','39,759.107','32.683','-82.400'],subtotal:['Subtotal · ng_f27','3,340.000','10,460','39,759.107','32.683','-82.400'],
 uncalibrated:['notCalibrated','4','—','—','—','—','—'],total:['Total · USD','4,000.000','','','','—'],totalWhy:'These thetas run to different days, so they are not one sum',
 opened:[['synthetic_lo','definition-other',null]]});

browser('a read that fails is said, and stays said while the person changes view, until a read succeeds',`
 failPositions=true;await options.refresh();
 const failed={state:state(),error:!$('#options-error').hidden&&/could not be read/.test($('#options-error').textContent)};
 $('#options-view-positions').click();const afterPositions={state:state(),error:!$('#options-error').hidden};
 $('#options-view-chain').click();const afterChain={state:state(),error:!$('#options-error').hidden};
 failPositions=false;await options.refresh();
 return {failed,afterPositions,afterChain,recovered:{state:state(),error:!$('#options-error').hidden}};
`,{failed:{state:'error',error:true},afterPositions:{state:'error',error:true},afterChain:{state:'error',error:true},recovered:{state:'idle',error:false}});

const strategyState="$('#options-strategy').dataset.state";
browser('Strategy opens on the expiry in view with the templates, range and curves its setup offers; a template it cannot place is disabled and says why',`
 await options.open('synthetic_on');await tick();
 $('#options-view-strategy').click();await tick();await tick();
 const refused=$('.options-template[data-template=iron_condor]');
 return {state:state(),pressed:$('#options-view-strategy').getAttribute('aria-pressed'),soon:/Coming soon/.test($('#options-view-strategy').textContent),strategy:${strategyState},
  shown:!$('#options-strategy').hidden&&$('.options-chain-view').hidden&&!$('#options-expiries').closest('[hidden]'),setup:calls.filter(c=>c==='options/strategy/setup').length,
  templates:all('.options-template').map(c=>[c.dataset.template,c.getAttribute('aria-disabled')==='true']),why:refused.title,
  said:(refused.click(),[$('#options-strategy').dataset.state,$('#options-strategy-message').textContent,$('#options-strategy-legs tbody tr[data-series]')]),
  range:[$('#options-strategy-lower').value,$('#options-strategy-upper').value,$('#options-strategy-points').value,all('#options-strategy-points option').map(o=>o.value).join()],
  curves:all('.options-strategy-curve').map(c=>c.dataset.instant===at),dates:all('#options-strategy-add-curve option').length,shift:$('#options-strategy-shift').value,
  legs:$('#options-strategy-legs tbody').textContent,analyse:$('#options-strategy-analyse').disabled,analysed:analyses.length};
`,{state:'strategy',pressed:'true',soon:false,strategy:'empty',shown:true,setup:1,templates:[['straddle',false],['iron_condor',true]],
 why:'Iron condor needs a put 4 listed strikes below 3, and the expiry lists none there.',
 said:['refused','Iron condor is not offered at this expiry. Iron condor needs a put 4 listed strikes below 3, and the expiry lists none there.',null],range:['2.6','3.4','21','11,21,41'],curves:[true],dates:2,shift:'0',
 legs:'No legs yet. Choose a template, or add a call or a put from the chain.',analyse:true,analysed:0});

browser('a template fills its legs and is analysed with the offered policies sent back: the payoff is drawn and the backend\'s figures are shown',`
 await options.open('synthetic_on');await tick();$('#options-view-strategy').click();await tick();await tick();
 $('.options-template[data-template=straddle]').click();await tick();await tick();await tick();
 const sent=analyses[0],row=series=>[...$('#options-strategy-legs tr[data-series='+series+']').children];
 const plot=$('#options-strategy-chart .options-strategy-plot');
 return {strategy:${strategyState},sent:{product:sent.product,expiry:sent.expiryAt===expiry,legs:sent.legs,prices:sent.prices,instants:sent.instants.length===1&&sent.instants[0]===at,shift:sent.volatilityShiftPoints,
   policies:JSON.stringify([sent.valuation,sent.sensitivities,sent.expiryRule])===JSON.stringify([offered.valuation,offered.sensitivities,offered.expiryRule]),context:sent.context.snapshotId,definition:sent.definitionRevisionId},
  call:[row('c3')[0].textContent,row('c3')[1].querySelector('input').value,row('c3')[2].textContent,row('c3')[3].querySelector('input').value,row('c3')[4].textContent,row('c3')[5].textContent],
  put:[row('p3')[0].textContent,row('p3')[5].textContent],
  chart:!!plot.querySelector('canvas')&&plot.querySelector('canvas').width>0,label:plot.getAttribute('aria-label'),
  card:[...$('#options-strategy-analysis dl').children].map(n=>n.textContent),tones:all('#options-strategy-analysis dd').map(d=>d.className),
  tiles:all('.options-tile').map(t=>[t.dataset.measure,t.querySelector('.options-tile-value').textContent,t.querySelector('.options-tile-unit').textContent,t.title]),
  basis:/option_expiry_exercise_value_v1/.test($('#options-strategy-basis').textContent)&&/Risk reports keep theirs/.test($('#options-strategy-basis').textContent)&&/books and stores nothing/.test($('#options-strategy-basis').textContent),
  message:$('#options-strategy-message').textContent};
`,{strategy:'analysed',sent:{product:'synthetic_on',expiry:true,legs:[{series:'c3',side:'buy',lots:1,entryPrice:'0.167'},{series:'p3',side:'buy',lots:1,entryPrice:'0.167'}],
  prices:{lower:'2.6',upper:'3.4',points:21},instants:true,shift:'0',policies:true,context:'synthetic-close',definition:'definition-a'},
 call:['Buy','1','3 call c3','0.167','45.1%','0.52'],put:['Buy','-0.48'],chart:true,
 label:'Profit and loss against the futures price: At expiry, Valuation · Oct 16, 2026, 5:00 PM. Futures 3.01, Breakeven 2.666, Breakeven 3.334',
 card:['Net premium','Debit 3,340.000 USD','Maximum profit','Unbounded','Maximum loss','-3,340.000 USD','Breakevens','2.666 · 3.334'],tones:['','','options-tone-loss',''],
 tiles:[['value','3,340.000','USD',''],['delta','460','MMBtu',''],['gamma','39,759.1','MMBtu per (USD/MMBtu)',''],['vega','—','USD per volatility point','A position in this sum has no figure'],['theta','-82.400','USD for one day','']],
 basis:true,message:''});

browser('a leg comes from the chain at its settlement; changing it marks the analysis as stale, and a refusal is said with its code while the legs stay',`
 await options.open('synthetic_on');await tick();
 $('#options-chain td[data-series=c325]').click();$('#options-strategy-sell').click();
 const again=$('#options-strategy-sell').disabled;
 $('#options-view-strategy').click();await tick();await tick();
 const added=[...$('#options-strategy-legs tr[data-series=c325]').children].map(td=>td.querySelector('input')?.value??td.textContent).slice(0,4);
 $('#options-strategy-analyse').click();await tick();await tick();
 const first=${strategyState};
 const lots=$('#options-strategy-legs .options-leg-lots');lots.value='3';lots.dispatchEvent(new Event('change'));
 const stale=[${strategyState},$('#options-strategy-message').textContent,!!$('#options-strategy-chart canvas')];
 lots.value='0';lots.dispatchEvent(new Event('change'));$('#options-strategy-analyse').click();await tick();
 const problem=[$('#options-strategy-message').textContent,analyses.length];
 lots.value='3';lots.dispatchEvent(new Event('change'));refuseAnalysis=true;$('#options-strategy-analyse').click();await tick();await tick();
 const refused=[${strategyState},$('#options-strategy-message').textContent,all('#options-strategy-legs tbody tr').length,analyses.at(-1).legs];
 $('.options-leg-side').click();const flipped=$('#options-strategy-legs tr[data-series=c325]').dataset.side;
 $('.options-leg-remove').click();
 return {again,added,first,stale,problem,refused,flipped,cleared:$('#options-strategy-legs tbody').textContent,analyse:$('#options-strategy-analyse').disabled};
`,{again:true,added:['Sell','1','3.25 call c325','0.167'],first:'analysed',stale:['stale','The legs or the scenario changed. Analyse again to update the payoff.',true],
 problem:['Each leg needs a whole number of lots from 1 to 100000.',1],
 refused:['refused','Choose a positive lower bound, a higher upper bound and at least two points whose even spacing is an exact decimal. (strategy_price_range)',1,[{series:'c325',side:'sell',lots:3,entryPrice:'0.167'}]],
 flipped:'buy',cleared:'No legs yet. Choose a template, or add a call or a put from the chain.',analyse:true});

browser('dated curves are chosen from the offered dates up to the limit; refused curves say why and the payoff at expiry is still drawn; an answer to a replaced choice is dropped',`
 await options.open('synthetic_on');await tick();$('#options-view-strategy').click();await tick();await tick();
 const add=$('#options-strategy-add-curve');add.value=nextDay;add.dispatchEvent(new Event('change'));
 const chosen=all('.options-strategy-curve').map(c=>c.dataset.instant),left=all('#options-strategy-add-curve option').length;
 refuseDated=true;$('.options-template[data-template=straddle]').click();await tick();await tick();await tick();
 const refused={strategy:${strategyState},instants:analyses[0].instants,why:$('#options-strategy-refused').textContent,title:$('#options-strategy-refused').title,chart:!!$('#options-strategy-chart canvas'),
  label:$('#options-strategy-chart .options-strategy-plot').getAttribute('aria-label')};
 // An analysis is awaited when a curve is removed: its answer belongs to the choice it was sent for and is not shown.
 let release;holdAnalysis=new Promise(resolve=>release=resolve);refuseDated=false;
 $('#options-strategy-analyse').click();await tick();const busy=${strategyState};
 $('.options-strategy-curve').click();const during=${strategyState};
 release();holdAnalysis=null;await tick();await tick();
 return {chosen,left,refused,busy,during,after:${strategyState},curves:all('.options-strategy-curve').length,refusedStill:!!$('#options-strategy-refused'),cancelled:signals.at(-1).aborted,earlier:signals[0].aborted};
`,{chosen:['2026-10-16T17:00:00+00:00','2026-10-17T17:00:00+00:00'],left:1,
 refused:{strategy:'analysed',instants:['2026-10-16T17:00:00+00:00','2026-10-17T17:00:00+00:00'],why:'No dated curve: Valued at intrinsic, so it has no sensitivities.',
  title:'A leg is valued at intrinsic and has no volatility, so the strategy has no dated curve.',chart:true,label:'Profit and loss against the futures price: At expiry. Futures 3.01, Breakeven 2.666, Breakeven 3.334'},
 busy:'busy',during:'stale',after:'stale',curves:1,refusedStill:true,cancelled:true,earlier:false});

browser('the keyboard keeps its place when the legs are redrawn, an answer leaves what is being typed alone, and moving to another product cancels a pending analysis',`
 await options.open('synthetic_on');await tick();$('#options-view-strategy').click();await tick();await tick();
 const focus=()=>document.activeElement?.dataset?.focus??document.activeElement?.tagName;
 const chip=$('.options-template[data-template=straddle]');chip.focus();chip.click();const afterTemplate=focus();await tick();await tick();await tick();
 const flip=$('.options-leg-side');flip.focus();flip.click();const afterFlip=[focus(),document.activeElement.textContent];
 const remove=all('.options-leg-remove')[1];remove.focus();remove.click();const afterRemove=focus();
 // An answer arrives while a number is being typed and has not been entered yet.
 let release;holdAnalysis=new Promise(resolve=>release=resolve);
 $('#options-strategy-analyse').click();await tick();
 const lots=$('.options-leg-lots');lots.focus();lots.value='7';
 release();holdAnalysis=null;await tick();await tick();
 const typing=[${strategyState},$('.options-leg-lots')===lots,lots.value,document.activeElement===lots,$('#options-strategy-legs .options-leg-figure').textContent];
 // Another product is chosen while an analysis is awaited.
 holdAnalysis=new Promise(resolve=>release=resolve);$('#options-strategy-analyse').click();await tick();
 const pending=signals.at(-1);
 all('.options-product')[1].click();await tick();
 const cancelled=pending.aborted;release();holdAnalysis=null;await tick();await tick();await tick();
 return {afterTemplate,afterFlip,afterRemove,typing,cancelled,state:state(),product:$('.options-product[aria-pressed=true]').dataset.product,legs:$('#options-strategy-legs tbody').textContent,unhandled};
`,{afterTemplate:'template:straddle',afterFlip:['side:c3','Sell'],afterRemove:'analyse',typing:['analysed',true,'7',true,'45.1%'],cancelled:true,state:'strategy',product:'synthetic_lo',
 legs:'No legs yet. Choose a template, or add a call or a put from the chain.',unhandled:[]});

browser('the directory groups by commodity and finds products by their underlying or exchange',`
 await options.refresh();
 const groups=all('#options-products .options-market h4').map(n=>n.textContent).sort();
 const search=$('#options-product-search');
 const find=value=>{search.value=value;search.dispatchEvent(new Event('input'));return all('.options-product').map(b=>b.dataset.product);};
 const byFuture=find('crude future'),byVenue=find('Alpha Exchange'),empty=find('no such contract');
 return {groups,byFuture,byVenue,empty,message:$('#options-products').textContent.includes('No matching products')};
`,{groups:['Crude oil','Natural gas'],byFuture:['synthetic_lo'],byVenue:['synthetic_on'],empty:[],message:true});

browser('listed strikes, the dated underlying and an exact booking ticket are available before calibration',`
 fitted=false;await options.open('synthetic_on');await tick();
 const chosen=$('#options-listed-chain button[data-series=p3]');chosen.focus();chosen.click();
 $('#options-book').click();$('#options-underlying-open').click();
 $('#options-listed-chain button[data-series=c325]').click();const closed=$('#options-book').disabled;
 canBook=false;await options.open('synthetic_on',expiry,'p3');
 return {strikes:all('#options-listed-chain th[scope=row]').map(n=>n.textContent),
  details:$('#options-facts').textContent.includes('ng_f27'),booked,contracts,closed,readOnly:$('#options-book').disabled,
  calibration:!$('#options-calibrate').hidden,analytics:!!$('#options-listed-chain .options-leg-figure')};
`,{strikes:['3','3.25'],details:true,booked:[['p3','definition-a']],contracts:[['gas_curve','future-jan','definition-a']],closed:true,readOnly:true,calibration:true,analytics:false});

browser('a reference-only underlying shows its terms and explains why the series cannot be booked',`
 fitted=false;listing.contractId=null;listing.curves=[];for(const row of listedSeries)row.openForBooking=false;
 await options.open('synthetic_on',expiry,'p3');
 return {terms:$('#options-facts').textContent.includes('ng_f27'),links:all('#options-facts button').length,
  disabled:$('#options-book').disabled,reason:$('#options-book').title};
`,{terms:true,links:0,disabled:true,reason:'This definition has no tradable contract for the underlying future.'});

browser('the underlying future can be inspected outside its booking window',`
 fitted=false;await options.open('synthetic_on');$('.options-future-book').click();
 listing.openForBooking=false;await options.refresh();$('#options-underlying-open').click();
 return {booked,contracts,offers:all('.options-future-book').length};
`,{booked:[['gas_curve','future-jan','definition-a']],contracts:[['gas_curve','future-jan','definition-a']],offers:0});

browser('a close without the retained definition offers the current directory after explaining the error',`
 await options.open('synthetic_on',expiry,'p3','definition-held');missingSource='definition-held';await options.refresh();
 const browse=$('#options-browse-current'),reason=state()==='error'&&$('#options-error').textContent.includes('exact source');
 const available=!browse.closest('[hidden]');if(available){browse.click();await tick();await tick();}
 return {reason,offered:available,choices:all('.options-product').length};
`,{reason:true,offered:true,choices:2});

browser('a strategy leg opens its own series in the chain and keeps the draft',`
 await options.open('synthetic_on');$('#options-view-strategy').click();await tick();await tick();
 $('.options-template[data-template=straddle]').click();await tick();await tick();
 $('#options-strategy-legs tr[data-series=p3] .options-leg-contract').click();
 const selected=$('#options-chain .options-selected').dataset.series;
 $('#options-view-strategy').click();await tick();
 return {selected,legs:all('#options-strategy-legs tr[data-series]').map(r=>r.dataset.series)};
`,{selected:'p3',legs:['c3','p3']});

browser('a product link reveals its exact selection even after searching and long names fit their buttons',`
 optionProducts[0].name='Synthetic monthly natural gas option on a dated physically delivered future';
 await options.refresh();const first=$('.options-product'),name=first.querySelector('.options-product-name');
 const fits=name.getBoundingClientRect().right<=first.getBoundingClientRect().right;
 const search=$('#options-product-search');search.value='crude';search.dispatchEvent(new Event('input'));
 await options.open('synthetic_on',expiry,'p3','definition-a');
 return {fits,selected:$('.options-product[aria-pressed=true]')?.dataset.product,search:search.value};
`,{fits:true,selected:'synthetic_on',search:''});

browser('a position in a retained definition can return to the current product directory',`
 await options.refresh();$('#options-view-positions').click();
 $('#options-position-table tr[data-series=c3] .options-leg-contract').click();await tick();await tick();
 const retained=$('#options-readout').textContent.includes('c3');$('#options-browse-current').click();await tick();await tick();
 $('.options-product[data-product=synthetic_on]').click();await tick();await tick();
 $('#options-chain td[data-series=c3]').click();$('#options-book').click();
 return {retained,booked,backHidden:$('#options-browse-current').hidden};
`,{retained:true,booked:[['c3','definition-a']],backHidden:true});

browser('a narrow listed chain keeps long series codes in the inspector and places booking above the strikes',`
 fitted=false;optionProducts[0].name='Gas option';
 const code='synthetic_natural_gas_december_202701_call_3';
 listedSeries.find(s=>s.code==='c3').code=code;
 $('#options-page').style.width='360px';await options.open('synthetic_on');
 const quote=$('#options-listed-chain button[data-series="'+code+'"]');quote.click();
 const selected=$('#options-listed-chain button[data-series="'+code+'"]'),summary=$('#options-readout');
 return {quote:selected.innerText.toLowerCase().trim(),full:summary.textContent.includes(code),book:!!summary.querySelector('#options-book'),
  inspectorFirst:summary.getBoundingClientRect().bottom<=$('#options-listed-chain').getBoundingClientRect().top};
`,{quote:'3 call',full:true,book:true,inspectorFirst:true});

browser('changing the close keeps a live series, advances past an expired contract and preserves exact links',`
 fitted=false;await options.open('synthetic_on',expiry,'p3','definition-a');
 snapshotId='another-live-close';await options.refresh();
 const active=()=>$('#options-listed-chain button[aria-pressed=true]')?.dataset.series??null,stillLive=active();
 const nextExpiry='2027-02-23T19:30:00+00:00';listing.expiryAt=nextExpiry;listing.underlyingCode='ng_g27';
 listedSeries.splice(0,listedSeries.length,{code:'feb-c3',strike:'3',right:'call',openForBooking:true});
 snapshotId='after-january-expiry';await options.refresh();
 const advanced={state:state(),expiry:$('#options-expiries [aria-current=true]')?.dataset.expiry,
  selected:active(),underlying:$('#options-underlying-open')?.textContent};
 await options.open('synthetic_on',expiry,'p3','definition-a');
 const exact={state:state(),reason:$('#options-error').textContent.includes('unavailable'),hidden:$('.options-chain-view').hidden};
 $('.options-product[data-product=synthetic_on]').click();await tick();await tick();
 return {stillLive,advanced,exact,browsed:state()==='notFitted'&&$('#options-underlying-open')?.textContent};
`,{stillLive:'p3',advanced:{state:'notFitted',expiry:'2027-02-23T19:30:00+00:00',selected:null,underlying:'ng_g27'},
 exact:{state:'error',reason:true,hidden:true},browsed:'ng_g27'});

browser('a pending close refresh clears prior holdings and product actions until the new holdings arrive',`
 await options.refresh();$('#options-view-positions').click();
 const row=()=>!$('#options-positions').hidden&&!!$('#options-position-table tr[data-series=c3]');
 const before=row(),oldProduct=$('.options-product'),empty={...positions,held:[],groups:[],totals:[]};
 let finish;holdPositions=new Promise(resolve=>finish=resolve);snapshotId='new-close';
 const pending=options.refresh();await tick();const during=row();
 $('#options-view-chain').click();const visibleChoices=all('.options-product').filter(b=>!b.disabled&&!b.closest('[hidden],[inert]')).length;
 oldProduct.click();$('#options-view-positions').click();finish(empty);await pending;await tick();
 return {before,during,visibleChoices,after:row(),empty:$('#options-positions').textContent.includes('No option positions')};
`,{before:true,during:false,visibleChoices:0,after:false,empty:true});

browser('expired open lots remain in Positions and only a live expiry links to its chain',`
 closeAt=expiry;await options.refresh();$('#options-view-positions').click();
 const expired=$('#options-position-table tr[data-series=c3]'),live=$('#options-position-table tr[data-series=lo70]');
 return {series:expired.cells[2].textContent,lots:expired.cells[3].textContent,
  expiredLink:!!expired.querySelector('.options-leg-contract'),liveLink:live.querySelector('.options-leg-contract')?.textContent};
`,{series:'c3',lots:'2',expiredLink:false,liveLink:'lo70'});
