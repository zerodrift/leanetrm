const id='a'.repeat(64),currency={revision:{catalogId:'reference',revisionId:'version'},code:'usd'};
export function historicalReport(){
 const policy={convention:'dated_factor_full_pv_historical_v1',numericalPolicy:'exact_empirical_probability_mass_v1',horizon:'frozen_portfolio_one_adjacent_session_v1',samplePolicy:'equal_weight_raw_joint_changes_v1',quantilePolicy:'empirical_inverse_cdf_v1',tailPolicy:'signed_upper_loss_fractional_mass_uniform_ties_v1',valuationPolicy:'current_instrument_pv_frozen_nonfactor_inputs_v1',confidence:'0.75'};
 const history={id,forecast:{contextId:'context'},policy:{minimumSamples:2},columns:[{id:'factor',mapping:{factor:{unit:'native'}}}],samples:[{id:'a',changes:['0']},{id:'b',changes:['1']}],available:true};
 history.rejected=[];history.evidence=[];history.sessions=['2026-01-01','2026-01-02','2026-01-03'].map(date=>({date,close:date+'T16:00:00Z'}));
 for(const [i,s] of history.samples.entries()){s.from={at:history.sessions[i].close,marks:[null]};s.to={at:history.sessions[i+1].close,marks:[null]};}
 const scope={context:{contextId:'context',snapshotId:'snapshot'},bookIds:['book'],portfolioIds:[],tradeIds:['trade']};
 const request={history:{scope,expectedManifestId:id},policy,factors:[{coordinate:'market_quote'}]};
 const contribution={id:'book',baseValue:'100',valueAtRisk:'10',expectedShortfall:'10'};
 const samples=[{id:'a',value:'100',loss:'0',varWeight:'0',esWeight:'0'},{id:'b',value:'90',loss:'10',varWeight:'1',esWeight:'1'}];
 return {context:{contextId:'context'},snapshotId:'snapshot',tradeIds:['trade'],excludedPositions:[],inputs:{positions:[{bookId:'book',tradeId:'trade',bindingId:'binding',measure:'present_value'}]},historical:{studyId:id,request,history,
  result:{policy,historyId:id,samples:2,available:true,unavailableReasons:[],partitions:[{measure:'present_value',currency,baseValue:'100',mean:'5',valueAtRisk:'10',expectedShortfall:'10',tailSampleMass:'1/2',quantileTies:1,books:[contribution],trades:[{...contribution,id:'trade'}],samples,histogram:[{lower:'0',upper:'10',count:2,probability:'1'}]}]},
  factors:[{id:'factor',factor:{id:'axis',label:'Gas January',unit:'USD / MMBtu',coordinate:'market_quote'}}],books:[{id:'book',name:'Gas trading'}],
  exposures:[{id:'exposure',bookId:'book',tradeId:'trade',bindingId:'binding',measure:'present_value',currency,baseValue:'100',tradeReference:'Trade 1',instrumentReference:'Gas January',instrumentType:'future'}],
  partitions:[{measure:'present_value',measureLabel:'Present value',currencyReference:currency,currency:'USD',}],
  matrix:{columns:['exposure'],rows:[{id:'a',scenarioValues:['100'],losses:['0']},{id:'b',scenarioValues:['90'],losses:['10']}]},comparison:null}};
}
