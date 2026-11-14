export function economicReport(){
 const currency={revision:{catalogId:'reference-catalog',revisionId:'reference-revision'},code:'usd'};
 const owner={owner:{kind:2,id:'native-owner'},bindingId:'native-binding',bookId:'book'};
 const policy={implementation:'economic_lifecycle_no_variable_factors_v1',confidence:'0.99',quantilePolicy:'empirical_inverse_cdf_v1',tailPolicy:'signed_upper_loss_fractional_mass_uniform_ties_v1'};
 const partition=(measure,baseValue,loss)=>({measure,currency,baseValue,mean:loss,valueAtRisk:loss,expectedShortfall:loss,books:[{id:'book',valueAtRisk:loss,expectedShortfall:loss}],
  trades:[{id:'native-owner',owner:owner.owner,valueAtRisk:loss,expectedShortfall:loss}],samples:[{id:'no_variable_factors',value:'115',loss,varWeight:'1',esWeight:'1'}],histogram:[{lower:loss,upper:loss,count:1,probability:'1'}]});
 return {measure:'economic_pnl_v1',convention:'portfolio_instrument_repricing_v1',engineVersion:'synthetic-engine',context:{contextId:'source-context',knownAt:'2026-11-02T16:00:00Z',effectiveOn:'2026-11-02'},source:{contextId:'source-context',snapshotId:'source-snapshot',knowledgeMode:'retained'},snapshotId:'source-snapshot',asOf:'2026-11-02T15:00:00Z',bookIds:['book'],tradeIds:null,pricingPolicy:{convention:'synthetic-pricing'},
  request:{projection:{policy:{implementation:'economic_lifecycle_projection_v1',inputExpiry:'declared_curve_expiry_policy_v1',physicalDelivery:'retain_generated_delivery_until_observed_performance_v1'},gasOwned:[]},policy,history:null,simulation:null,factors:[]},
  horizon:{openingAt:'2026-11-02T15:00:00Z',contextId:'source-context',knownAt:'2026-11-02T16:00:00Z',closes:[{date:'2026-11-03',instant:'2026-11-03T15:00:00Z'}],session:{code:'close',version:2}},
  holdings:[{owner,reference:'Synthetic owned holding',instrument:'Synthetic cash',book:'Synthetic book',currency:'USD',currencyReference:currency,openingValue:'100',residualValue:'110',periodCash:'5',economicPnl:'15'}],
  books:[{id:'book',name:'Synthetic book'}],products:[],optionCalibrations:[],history:null,factors:[],
  result:{policy,horizon:'2026-11-03T15:00:00Z',noShockTimeCarry:[{owner,currency}],economic:[partition('economic_pnl_v1','100','-15')],marketOnlyDiagnostic:[partition('market_only_horizon_diagnostic_v1','115','0')]}};
}
