import test from 'node:test';
import assert from 'node:assert/strict';
import {validateEconomicReport,restoreEconomicCommand,requireEconomicCalculationReceipt} from '../dist/economic-risk.js';
import {economicReport} from './economic-report-fixture.mjs';

test('economic reporting preserves signed backend tails and separate native ownership without browser financial arithmetic',()=>{
 const report=economicReport(),before=JSON.stringify(report);assert.equal(validateEconomicReport(report),report);
 assert.equal(report.result.economic[0].valueAtRisk,'-15');assert.equal(report.result.marketOnlyDiagnostic[0].valueAtRisk,'0');assert.equal(JSON.stringify(report),before);
});
test('economic receipt clocks recognize equivalent exact UTC text without losing a microsecond',()=>{
 const report=economicReport();report.context.knownAt='2026-11-02T16:00:00.1234560Z';report.horizon.knownAt='2026-11-02T16:00:00.123456+00:00';
 report.asOf='2026-11-02T15:00:00.0000000Z';report.result.horizon='2026-11-03T15:00:00.000000+00:00';
 assert.equal(validateEconomicReport(report),report);report.horizon.knownAt='2026-11-02T16:00:00.123457+00:00';assert.throws(()=>validateEconomicReport(report),/inconsistent/);
});
test('an economic report cannot hide a missing owner, substitute the source or broaden its currency/path coverage',()=>{
 for(const change of [r=>r.holdings.push(structuredClone(r.holdings[0])),r=>r.result.noShockTimeCarry.pop(),r=>r.result.noShockTimeCarry[0].owner={...r.holdings[0].owner,bindingId:'another-binding'},
  r=>r.source.contextId='other',r=>r.horizon.closes[0].instant='2026-11-04T15:00:00Z',r=>delete r.request.projection.policy.physicalDelivery,
  r=>r.result.economic[0].samples=[],r=>r.result.marketOnlyDiagnostic[0].samples[0].id='another-path',r=>r.result.economic[0].currency={code:'another-currency'},r=>r.request.history={}]){
  const report=economicReport();change(report);assert.throws(()=>validateEconomicReport(report),/inconsistent|incomplete/);
 }
 const duplicate=economicReport(),aliased=structuredClone(duplicate.holdings[0]);aliased.owner.tradeId='an-extra-transport-alias';duplicate.holdings.push(aliased);duplicate.result.noShockTimeCarry.push({owner:aliased.owner,currency:aliased.currencyReference});
 assert.throws(()=>validateEconomicReport(duplicate),/inconsistent/);
});
test('economic command restoration keeps the exact owned request and refuses unrelated writes and missing explicit choices',()=>{
 const report=economicReport(),request={context:report.source,bookIds:report.bookIds,bumps:[],stress:[],economic:report.request};
 const command={operationId:'original-operation',path:'portfolio/risk/jobs',body:{request},uncertain:true};assert.deepEqual(restoreEconomicCommand(command),command);
 const restored=restoreEconomicCommand(command);restored.body.request.context.snapshotId='changed';assert.equal(command.body.request.context.snapshotId,'source-snapshot');
 for(const path of ['lifecycle/execute','portfolio/risk','other/write'])assert.equal(restoreEconomicCommand({...command,path}),undefined);
 const absent=structuredClone(command);delete absent.body.request.economic.history;assert.equal(restoreEconomicCommand(absent),undefined);
});
test('a new economic calculation must answer its submitted complete source even when another report is internally consistent',()=>{
 const report=economicReport(),request={context:report.source,bookIds:report.bookIds,portfolioIds:[],tradeIds:report.tradeIds,pricingPolicy:report.pricingPolicy,convention:report.convention,economic:report.request};
 const source={engineVersion:report.engineVersion,horizon:report.horizon,books:report.books};
 requireEconomicCalculationReceipt(report,request,source,report.engineVersion);
 for(const change of [r=>{r.snapshotId='another';r.source.snapshotId='another';},r=>{r.request.policy.confidence='0.95';},r=>r.pricingPolicy={convention:'other'},
  r=>{r.horizon.closes[0].instant='2026-11-04T15:00:00Z';r.result.horizon='2026-11-04T15:00:00Z';},r=>r.tradeIds=['another-trade'],r=>r.engineVersion='another-engine',
  r=>{r.bookIds=['another-book'];r.books[0].id='another-book';r.holdings[0].owner.bookId='another-book';r.result.economic[0].books[0].id='another-book';r.result.marketOnlyDiagnostic[0].books[0].id='another-book';}]){
  const changed=structuredClone(report);change(changed);validateEconomicReport(changed);assert.throws(()=>requireEconomicCalculationReceipt(changed,request,source,report.engineVersion),/does not match/);
 }
});
