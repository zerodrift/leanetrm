import test from 'node:test';
import assert from 'node:assert/strict';

test('scenario controls transport the exact offered coordinate and explicit decimal size',async()=>{
 const {scenarioShift}=await import('../dist/risk-scenarios.js');
 const offered={id:'computed-a',label:'Synthetic output',coordinate:'computed_output',kind:'points',unit:'USD/unit',factor:{coordinate:'computed_output',target:{kind:'points',curve:{revision:{catalogId:'catalog',revisionId:'revision'},code:'saved'},points:['point-a']}}};
 const before=structuredClone(offered),shift=scenarioShift(offered,'-0.700000000000000001','selected-convention');
 assert.deepEqual(shift,{factor:offered.factor,size:'-0.700000000000000001',convention:'selected-convention'});
 shift.factor.target.points.push('other');assert.deepEqual(offered,before);
});
test('delivery bucket transport preserves the server containment policy and requires explicit bounds',async()=>{
 const {scenarioShift}=await import('../dist/risk-scenarios.js');
 const offered={id:'bucket',label:'Synthetic delivery bucket',coordinate:'market_quote',kind:'delivery_bucket',unit:'USD/unit',factor:{coordinate:'market_quote',target:{kind:'delivery_bucket',curve:{code:'synthetic'},from:null,to:null,convention:'selected-containment-policy'}}};
 assert.throws(()=>scenarioShift(offered,'1','selected-convention',{from:'',to:''}),/delivery/);
 assert.deepEqual(scenarioShift(offered,'1','selected-convention',{from:'2027-01-01',to:'2027-04-01'}).factor.target,{...offered.factor.target,from:'2027-01-01',to:'2027-04-01'});
 assert.equal(offered.factor.target.from,null);
});
test('rejected scenario admission releases only a first attempt and never erases an uncertain original job',async()=>{
 const {knownRiskRejection}=await import('../dist/risk-scenarios.js');
 const {PendingCommand}=await import('../dist/pending-command.js');
 const pending=new PendingCommand(knownRiskRejection);
 pending.begin('portfolio/risk/jobs',{request:{bumps:[]}});
 pending.failed('scenario_bucket',pending.sending());
 assert.equal(pending.command,undefined);
 pending.begin('portfolio/risk/jobs',{request:{bumps:[]}});
 const original=pending.command;
 pending.failed(undefined,pending.sending());
 pending.failed('scenario_target',pending.sending());
 assert.equal(pending.command,original);
 assert.equal(pending.command.uncertain,true);
});
