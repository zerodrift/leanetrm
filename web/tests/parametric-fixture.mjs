// A complete synthetic parametric VaR report for the parametric tests.
export const id='a'.repeat(64),column='b'.repeat(64),currency={revision:{catalogId:'reference',revisionId:'version'},code:'usd'},unit={currency,quantity:{revision:currency.revision,code:'energy'},multiplier:'1'};
export function report(){
 const policy={convention:'dated_factor_delta_normal_v1',numericalPolicy:'exact_moments_binary64_normal_v1',estimator:'equal_weight_sample_n_minus_one_v1',centering:'sample_mean_centered_v1',
  drift:'zero_drift_v1',horizon:'frozen_portfolio_one_adjacent_session_v1',tail:'signed_upper_loss_tail_v1',confidence:'0.99'};
 const finiteDifference={convention:'native_tick_central_half_nominal_double_v1',tradingTicks:1,deltaTolerance:{absolute:'0',relative:'0'}};
 const contribution={id:'book',mean:'0',varianceCross:'4000000000000',valueAtRisk:'4652695.7480816816',expectedShortfall:'5330428.44069161'};
 const history={id,forecast:{contextId:'context'},policy:{minimumSamples:2},columns:[{id:column,mapping:{factor:{unit}}}],samples:[{id:'a',changes:['1']},{id:'b',changes:['0']},{id:'c',changes:['-1']}],rejected:[],available:true};
 const request={history:{scope:{context:{contextId:'context',snapshotId:'snapshot'},bookIds:['book'],portfolioIds:[],tradeIds:['trade']},expectedManifestId:id},policy,finiteDifference};
 return {context:{contextId:'context'},snapshotId:'snapshot',tradeIds:['trade'],excludedPositions:[],parametric:{request,history,
  result:{policy,available:true,unavailableReasons:[],calibration:{historyId:id,samples:3,columns:[column],means:['0'],covariance:[['4']],correlation:[['1']],rank:1,constantColumns:[],conditioning:'full_rank',correlationCondition:'1'},
   partitions:[{measure:'present_value',currency,mean:'0',variance:'4000000000000',standardDeviation:'2000000',valueAtRisk:contribution.valueAtRisk,expectedShortfall:contribution.expectedShortfall,
    books:[contribution],factors:[{...contribution,id:column}],density:[{score:'-4',loss:'-8000000',density:'0.00000000006691511288244269'},{score:'0',loss:'0',density:'0.00000019947114020071635'}],
    linearizedLosses:[{sample:'a',loss:'-2000000'},{sample:'b',loss:'0'},{sample:'c',loss:'2000000'}],varReconciliation:'0',esReconciliation:'0'}]},
  exposures:[{bookId:'book',measure:'present_value',currency}],factors:[{id:column,factor:{id:'axis',label:'Gas futures · January 2026',unit:'USD / MMBtu',coordinate:'market_quote',kind:'point',curves:['gas']}}],books:[{id:'book',name:'Gas trading'}],
  partitions:[{measure:'present_value',measureLabel:'Present value',currencyReference:currency,currency:'USD'}],
  finiteDifference:{...finiteDifference,evaluated:true,estimates:[{columnId:column,bookId:'book',measure:'present_value',currency,half:'1000000',nominal:'1000000',double:'1000000',stable:true,gamma:'0',bump:'0.01',unit}]},passes:[{}, {}, {}]}};
}
// The same report with end-of-day price provenance for each history session, as a retained report carries it.
export function pricedReport(){
 const r=report(),h=r.parametric.history;h.policy={...h.policy,alignment:'daily_latest_visible_session_end_v1',regimes:'ordered_price_types_fixed_series_source_unit_contract_v1',priceTypes:['official_close_v1']};
 r.parametric.request.history.policy=structuredClone(h.policy);
 const marks=['9','10','10','9'].map((value,i)=>({observation:{id:'mark'+i},value}));
 h.sessions=marks.map((_,i)=>({date:'day'+i,close:'close'+i}));
 h.evidence=marks.map((m,i)=>({observation:{key:m.observation,source:{code:'close'},value:m.value,role:2,state:2,observedAt:'2026-01-0'+(i+1)+'T16:00:00Z'}}));
 for(const [i,s] of h.samples.entries()){s.from={at:h.sessions[i].close,marks:[marks[i]]};s.to={at:h.sessions[i+1].close,marks:[marks[i+1]]};}
 h.dayPrices=h.sessions.map(s=>({at:s.close,choices:[{selectedType:'official_close_v1',officialCloseStatus:'available',availableTypes:['official_close_v1']}]}));
 return r;
}
