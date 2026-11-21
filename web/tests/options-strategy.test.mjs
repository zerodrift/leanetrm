import {prepareAmountDisplay,shownAmounts,selectDisplayPrecision,initialDisplayPrecision} from '../dist/amount-display.js';
import {test,before} from 'node:test';
import assert from 'node:assert/strict';
import {analysisLines,analysisRequest,breakevenText,choiceProblem,legFigures,offeredChoice,sensitivityTiles,strategyChart,strategyReason,templateLegs} from '../dist/options-strategy.js';
import {curveDashes,strategyChartOptions} from '../dist/options-strategy-chart.js';
import {chartTokens} from '../dist/chart-view.js';

const expiry='2027-01-26T19:30:00+00:00',at='2026-10-16T17:00:00+00:00',later='2026-10-17T17:00:00+00:00';
const limits={legs:4,points:41,curves:3,lots:100000,volatilityShiftPoints:100};
const offered={prices:{lower:'2.600',upper:'3.4',points:21},pointCounts:[11,21,41],instants:[at,later],instantsComplete:true,selectedInstants:[at],volatilityShiftPoints:'0',
 valuation:{observationRole:'daily_settlement',numericalPolicy:'american_futures_option_crr_v1'},sensitivities:{policy:'option_sensitivities_layers_v1',volatilityBump:'0.01',clock:null},expiryRule:'option_expiry_exercise_value_v1'};
const straddle={id:'straddle',name:'Straddle',refusal:null,legs:[{series:'c3',right:'call',strike:'3.000',side:'buy',lots:1,entryPrice:'0.0800'},{series:'p3',right:'put',strike:'3.000',side:'buy',lots:1,entryPrice:null}]};
const setup={product:'synthetic_on',model:'m',expiryAt:expiry,state:'fitted',limits,templates:[straddle,{id:'iron_condor',name:'Iron condor',legs:null,refusal:{code:'strategy_template_strike',message:'Iron condor needs a put 4 listed strikes below 3, and the expiry lists none there.'}}],offered};
const figure=(amount,missing=null)=>({amount,missing}),measure=(value,missing=null)=>({value,missing});
const analysis={product:'synthetic_on',expiryAt:expiry,valuationAt:at,underlyingSeries:'ng_f27',currency:'USD',priceUnit:'USD/MMBtu',quantityUnit:'MMBtu',futures:'3.01',
 request:{prices:{lower:'2.9',upper:'3.2',points:4},instants:[at],volatilityShiftPoints:'0'},
 legs:[{series:'c3',right:'call',strike:'3',side:'buy',lots:'1',entryPrice:'0.08',multiplier:'10000',premium:'800',state:'solved',reason:null,settlement:'0.08',impliedVolatility:'0.4512',value:'0.08',delta:measure('0.523')},
  {series:'p3',right:'put',strike:'3',side:'buy',lots:'1',entryPrice:'0.08',multiplier:'10000',premium:'800',state:'at_intrinsic',reason:'option_at_intrinsic',settlement:'0.08',impliedVolatility:null,value:'0.08',delta:measure(null,'option_at_intrinsic')}],
 expiry:{rule:'option_expiry_exercise_value_v1',netPremium:{amount:'1600',direction:'debit'},maximumProfit:{amount:null,bounded:false},maximumLoss:{amount:'-1600',bounded:true},
  breakevens:[{lower:'2.84',upper:'2.84'},{lower:'3.16',upper:'3.16'}],
  curve:[{price:'2.9',profitAndLoss:'-600'},{price:'3',profitAndLoss:'-1600'},{price:'3.1',profitAndLoss:'-600'},{price:'3.2',profitAndLoss:'400'}]},
 dated:{state:'valued',reason:null,message:null,curves:[{at,points:[{price:'2.9',value:'1750.5',profitAndLoss:'150.5'},{price:'3',value:'1600',profitAndLoss:'0'},{price:'3.1',value:'1760',profitAndLoss:'160'},{price:'3.2',value:'2400',profitAndLoss:'800'}]}]},
 strategy:{value:figure('1600'),delta:figure('460.000000000000000000'),gamma:figure('39759.1066'),vega:figure(null,'incomplete'),theta:figure('-82.4'),dayTo:later},
 policies:{sensitivities:{policy:'option_sensitivities_layers_v1'},expiryRule:'option_expiry_exercise_value_v1'},limits,work:{calibrationTreePrices:60,valuationTreePrices:120,treePrices:180,curveSets:4,elapsedMs:900}};

before(async()=>{
 selectDisplayPrecision(initialDisplayPrecision());
 const values={'1600':'1,600.000','-1600':'-1,600.000','600':'600.000','-1400':'-1,400.000','460.000000000000000000':'460','39759.1066':'39,759.107','400':'400.000','-12.505':'-12.505','1234567890123456789012.125':'1,234,567,890,123,456,789,012.125','0':'0.000','-82.4':'-82.400'};
 await prepareAmountDisplay([{presentValue:'600'},{presentValue:'-1400'},{presentValue:'-12.505'},{presentValue:'1234567890123456789012.125'},...shownAmounts('options/strategy/analyze',analysis)],async body=>body.map(r=>values[r.value]??r.value));
});

test('a template gives its legs with the settlements the backend offers as entry prices, and a refused template gives its reason',()=>{
 assert.deepEqual(templateLegs(straddle),[{series:'c3',right:'call',strike:'3.000',side:'buy',lots:'1',entryPrice:'0.08'},{series:'p3',right:'put',strike:'3.000',side:'buy',lots:'1',entryPrice:''}]);
 assert.throws(()=>templateLegs(setup.templates[1]),/4 listed strikes below 3/);
});

test('the choice starts from what the setup offers and is sent back with the offered policies untouched',()=>{
 const choice=offeredChoice(offered);
 assert.deepEqual(choice,{legs:[],lower:'2.6',upper:'3.4',points:21,instants:[at],volatilityShiftPoints:'0'});
 choice.legs=templateLegs(straddle);choice.legs[1].entryPrice='0.075';choice.legs[1].side='sell';choice.legs[1].lots='2';choice.instants=[later,at];choice.volatilityShiftPoints='-2.5';
 const context={snapshotId:'s',convention:'futures_option_premium_v1',knowledgeMode:'current'};
 assert.deepEqual(analysisRequest(context,setup,choice,'definition-a'),{context,product:'synthetic_on',expiryAt:expiry,definitionRevisionId:'definition-a',
  legs:[{series:'c3',side:'buy',lots:1,entryPrice:'0.08'},{series:'p3',side:'sell',lots:2,entryPrice:'0.075'}],prices:{lower:'2.6',upper:'3.4',points:21},instants:[at,later],
  volatilityShiftPoints:'-2.5',valuation:offered.valuation,sensitivities:offered.sensitivities,expiryRule:'option_expiry_exercise_value_v1'});
 assert.throws(()=>analysisRequest(context,{...setup,offered:undefined},choice,undefined),/offers no strategy analysis/);
 assert.deepEqual(offeredChoice({...offered,prices:null}),{legs:[],lower:'',upper:'',points:11,instants:[at],volatilityShiftPoints:'0'});
});

test('a choice that cannot be sent says what to fix and nothing is corrected for the person',()=>{
 const good={legs:templateLegs(straddle).map(l=>({...l,entryPrice:'0.08'})),lower:'2.6',upper:'3.4',points:21,instants:[at],volatilityShiftPoints:'0'};
 assert.equal(choiceProblem(good,limits),null);
 const problem=change=>choiceProblem({...good,...change},limits);
 assert.match(problem({legs:[]}),/Add a leg/);
 assert.match(problem({legs:Array.from({length:5},(_,n)=>({...good.legs[0],series:'c'+n}))}),/at most 4 legs/);
 for(const lots of ['0','1.5','-1','','100001'])assert.match(problem({legs:[{...good.legs[0],lots}]}),/whole number of lots from 1 to 100000/,lots);
 for(const entryPrice of ['','-0.1','abc','1e3'])assert.match(problem({legs:[{...good.legs[0],entryPrice}]}),/entry price/,entryPrice);
 assert.match(problem({lower:''}),/lower and upper futures price/);assert.match(problem({upper:'3,4'}),/lower and upper futures price/);
 assert.match(problem({instants:[at,later,expiry,'x']}),/at most 3 dated curves/);
 for(const volatilityShiftPoints of ['','five','1.234'])assert.match(problem({volatilityShiftPoints}),/volatility shift in points/);
 assert.equal(problem({volatilityShiftPoints:'-2.5'}),null);
});

test('leg figures show the analysed volatility and delta, or a dash that says why',()=>{
 const figures=legFigures(analysis);
 assert.deepEqual(figures.get('c3'),{impliedVolatility:{text:'45.1%',missing:null},delta:{text:'0.52',missing:null}});
 assert.deepEqual(figures.get('p3'),{impliedVolatility:{text:'—',missing:'option_at_intrinsic'},delta:{text:'—',missing:'option_at_intrinsic'}});
 assert.equal(strategyReason('option_at_intrinsic'),'Valued at intrinsic, so it has no sensitivities');
 assert.equal(strategyReason('scenario_volatility'),'The shifted volatility is outside what the option model prices');
 assert.equal(strategyReason('synthetic_unknown_code'),'synthetic_unknown_code');
});

test('the analysis card states the premium, the extremes and the breakevens as the backend gives them, signed and labelled',()=>{
 assert.deepEqual(analysisLines(analysis).map(l=>[l.term,l.value,l.tone]),[['Net premium','Debit 1,600.000 USD','none'],['Maximum profit','Unbounded','none'],
  ['Maximum loss','-1,600.000 USD','loss'],['Breakevens','2.84 · 3.16','none']]);
 const condor={...analysis,expiry:{...analysis.expiry,netPremium:{amount:'600',direction:'credit'},maximumProfit:{amount:'600',bounded:true},maximumLoss:{amount:'-1400',bounded:true},breakevens:[{lower:'2.8',upper:'3.2'}]}};
 assert.deepEqual(analysisLines(condor).map(l=>[l.term,l.value,l.tone]),[['Net premium','Credit 600.000 USD','none'],['Maximum profit','+600.000 USD','gain'],['Maximum loss','-1,400.000 USD','loss'],['Breakeven','2.8 – 3.2','none']]);
 const flat={...analysis,expiry:{...analysis.expiry,netPremium:{amount:'0',direction:'none'},maximumProfit:{amount:'0',bounded:true},breakevens:[]}};
 assert.deepEqual(analysisLines(flat).map(l=>l.value),['0.000 USD','0.000 USD','-1,600.000 USD','None']);
 assert.equal(breakevenText({lower:'3.2',upper:null}),'3.2 and above');
 assert.equal(breakevenText({lower:'3.083333333333333333',upper:'3.083333333333333333'}),'3.08333');
});

test('sensitivity tiles carry each figure with its unit, and a sum with a missing member is a dash that says why',()=>{
 assert.deepEqual(sensitivityTiles(analysis),[{term:'Value',value:'1,600.000',unit:'USD',missing:null},{term:'Delta',value:'460',unit:'MMBtu',missing:null},
  {term:'Gamma',value:'39,759.107',unit:'MMBtu per (USD/MMBtu)',missing:null},{term:'Vega',value:'—',unit:'USD per volatility point',missing:'A position in this sum has no figure'},
  {term:'Theta',value:'-82.400',unit:'USD for one day',missing:null}]);
});

test('the chart draws the stated payoff through its breakevens, one curve per dated instant, and marks only what lies in range',()=>{
 const chart=strategyChart(analysis,instant=>instant===at?'Valuation':'Later');
 assert.deepEqual(chart.curves.map(c=>[c.id,c.name,c.kind]),[['expiry','At expiry','expiry'],[at,'Valuation','dated']]);
 // 3.16 lies between the stated points at 3.1 and 3.2 and joins the line at zero; 2.84 lies below the range and does not.
 assert.deepEqual(chart.curves[0].points.map(p=>[p.x,p.y]),[[2.9,-600],[3,-1600],[3.1,-600],[3.16,0],[3.2,400]]);
 assert.deepEqual(chart.curves[1].points.map(p=>[p.x,p.y]),[[2.9,150.5],[3,0],[3.1,160],[3.2,800]]);
 // Each point keeps the stated text beside the number it is plotted at.
 assert.deepEqual(chart.curves[1].points[0],{x:2.9,y:150.5,price:'2.9',profitAndLoss:'150.5'});
 assert.deepEqual(chart.curves[0].points[3],{x:3.16,y:0,price:'3.16',profitAndLoss:'0'});
 assert.deepEqual(chart.marks,[{kind:'futures',price:3.01,label:'Futures 3.01'},{kind:'breakeven',price:3.16,label:'Breakeven 3.16'}]);
 assert.equal(chart.currency,'USD');
 assert.deepEqual(strategyChart({...analysis,futures:null,dated:{...analysis.dated,curves:[]}},String).marks.map(m=>m.kind),['breakeven']);
});

test('the chart options use the theme: a solid expiry line filled gain above zero and loss below, dated curves each with a dash of their own',()=>{
 const twoCurves={...analysis,dated:{...analysis.dated,curves:[analysis.dated.curves[0],{...analysis.dated.curves[0],at:later}]}};
 const options=strategyChartOptions(strategyChart(twoCurves,instant=>instant===at?'Valuation':'Later')),series=id=>options.series.find(s=>s.id===id);
 assert.equal(series('expiry').lineStyle.type,'solid');assert.equal(series('expiry').lineStyle.color,chartTokens.text);
 assert.equal(series('gain').areaStyle.color,chartTokens.gain);assert.equal(series('loss').areaStyle.color,chartTokens.loss);
 // The fills clip the stated payoff at zero and state nothing of their own.
 assert.deepEqual(series('gain').data,[[2.9,0],[3,0],[3.1,0],[3.16,0],[3.2,400]]);assert.deepEqual(series('loss').data,[[2.9,-600],[3,-1600],[3.1,-600],[3.16,0],[3.2,0]]);
 assert.deepEqual([series(at).lineStyle.type,series(later).lineStyle.type],[curveDashes[0],curveDashes[1]]);
 assert.notDeepEqual(curveDashes[0],curveDashes[1]);assert.notEqual(series(at).lineStyle.color,series(later).lineStyle.color);
 const marks=series('expiry').markLine.data;
 assert.deepEqual(marks.map(m=>m.xAxis??('y'+m.yAxis)),['y0',3.01,3.16]);assert.equal(marks[1].name,'Futures 3.01');
 assert.equal(options.tooltip.trigger,'axis');assert.equal(options.tooltip.axisPointer.type,'cross');
 assert.deepEqual(series('expiry').data.at(-1),{value:[3.2,400],price:'3.2',profitAndLoss:'400'});assert.deepEqual(series(at).data[0],{value:[2.9,150.5],price:'2.9',profitAndLoss:'150.5'});
 // The readout states the backend's text, rounded half to even as the analysis card rounds it, whatever its size.
 assert.equal(options.tooltip.formatter([{seriesName:'At expiry',data:{price:'3.2',profitAndLoss:'400'},marker:''},{seriesName:'Valuation',data:{price:'3.2',profitAndLoss:'-12.505'},marker:''},
  {seriesName:'<Later>',data:{price:'3.083333',profitAndLoss:'1234567890123456789012.125'},marker:''},{seriesName:'Flat',data:{price:'3.16',profitAndLoss:'0'},marker:''}]),
  'At expiry at 3.2 USD/MMBtu: <b>+400.000</b> USD<br>Valuation at 3.2 USD/MMBtu: <b>-12.505</b> USD<br>&lt;Later&gt; at 3.083333 USD/MMBtu: <b>+1,234,567,890,123,456,789,012.125</b> USD<br>Flat at 3.16 USD/MMBtu: <b>0.000</b> USD');
 assert.equal(options.yAxis.axisLabel.formatter(1500),'+1,500');assert.equal(options.yAxis.axisLabel.formatter(-1e22),'-10,000,000,000,000,000,000,000');assert.equal(options.yAxis.axisLabel.formatter(0),'0');
 assert.deepEqual([options.xAxis.min,options.xAxis.max],[2.9,3.2]);
});
