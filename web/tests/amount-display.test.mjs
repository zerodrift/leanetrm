import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {prepareAmountDisplay,amountText,selectDisplayPrecision,initialDisplayPrecision,shownAmounts,riskReportRead} from '../dist/amount-display.js';
import {newPreferences,parsePreferences} from '../dist/preferences.js';
test('display adapter sends exact text and tick choice, keeping raw financial values untouched',async()=>{
 selectDisplayPrecision(initialDisplayPrecision());
 const source={holdings:[{priceTick:'0.025',mark:'3.1',presentValue:'12345678901234567890.125',quantity:'1000'}]};
 const before=JSON.stringify(source),requests=[];
 await prepareAmountDisplay(source,async body=>{requests.push(...body);return body.map(r=>r.tick?'3.100':r.value==='1000'?'1,000.000':'12,345,678,901,234,567,890.12');});
 assert.equal(JSON.stringify(source),before);
 assert.equal(requests.find(r=>r.value==='3.1').tick,'0.025');
 assert.equal(amountText('3.1','price','0.025'),'3.100');
 assert.equal(amountText('1000','quantity'),'1,000.000');
});
test('incomplete formatter responses fail visibly instead of silently truncating values',async()=>{
 selectDisplayPrecision(initialDisplayPrecision());
 await assert.rejects(prepareAmountDisplay({quantity:'9.123'},async()=>[]),/incomplete/);
});
test('precision is scoped to the deal rather than the user profile',()=>{
 const p=newPreferences('UTC');
 assert.equal('displayPrecision' in p,false);
 assert.equal('displayPrecision' in parsePreferences(JSON.stringify({...p,displayPrecision:{money:9,price:9,quantity:9,factor:9}})),false);
});
test('two deals with identical amounts keep different precision in one response',async()=>{
 const first={money:2,price:4,quantity:1,factor:6},second={...first,quantity:3};
 const requests=[];
 await prepareAmountDisplay([{amountFormat:first,quantity:'123.45'},{amountFormat:second,quantity:'123.45'}],async body=>{requests.push(...body);return body.map(r=>r.decimalPlaces===1?'123.4':'123.450');});
 assert.deepEqual(requests.map(r=>r.decimalPlaces).sort(),[1,3]);
 assert.equal(amountText('123.45','quantity',undefined,first),'123.4');
 assert.equal(amountText('123.45','quantity',undefined,second),'123.450');
});
test('settlement marks retain their clearing increment separately from execution ticks',async()=>{
 const requests=[];
 await prepareAmountDisplay({priceTick:'0.001',markTick:'0.0001',mark:'3.1001',executionPrice:'3.101'},async body=>{requests.push(...body);return body.map(r=>r.value);});
 assert.equal(requests.find(r=>r.value==='3.1001').tick,'0.0001');
 assert.equal(requests.find(r=>r.value==='3.101').tick,'0.001');
});
test('a mark minus price shows to the mark increment, and a rational stays exact for the backend to format',async()=>{
 selectDisplayPrecision(initialDisplayPrecision());const requests=[];
 await prepareAmountDisplay({priceTick:'0.025',markTick:'0.001',markMinusPrice:'-1/3'},async body=>{requests.push(...body);return body.map(()=>'-0.333');});
 assert.deepEqual(requests.map(r=>[r.value,r.tick,r.trimTrailingZeros]),[['-1/3','0.001',true]]);
 assert.equal(amountText('-1/3','price','0.001'),'-0.333');
});
test('ordinary money display defaults to three decimal places',()=>{
 assert.equal(initialDisplayPrecision().money,3);
});
test('only money is padded while noncash values request compact backend formatting',async()=>{
 selectDisplayPrecision(initialDisplayPrecision());const requests=[];
 await prepareAmountDisplay({presentValue:'123.45678',quantity:'1000',fixedPrice:'3.1',discountFactor:'0.95',priceTick:'0.025',executionPrice:'3.1'},async body=>{requests.push(...body);return body.map(r=>r.value);});
 assert.equal(requests.find(r=>r.value==='123.45678').trimTrailingZeros,false);
 for(const value of ['1000','0.95'])assert.equal(requests.find(r=>r.value===value).trimTrailingZeros,true);
 assert.equal(requests.find(r=>r.tick==='0.025').trimTrailingZeros,true);
});
test('money and quantity with the same value and places keep separate display cache entries',async()=>{
 selectDisplayPrecision(initialDisplayPrecision());
 const requests=[];await prepareAmountDisplay({presentValue:'417',quantity:'417'},async body=>{requests.push(...body);return body.map(r=>r.trimTrailingZeros?'417':'417.000');});
 assert.equal(requests.length,2);
 assert.equal(amountText('417','money'),'417.000');
 assert.equal(amountText('417','quantity'),'417');
});
// The corpus the managed checks assert for tool results (tool_amount_display_v1): what this page requests for each payload.
const corpus=JSON.parse(readFileSync(new URL('../../tests/fixtures/amount-display-cases.json',import.meta.url),'utf8'));
const requested=r=>JSON.stringify([r.value,r.decimalPlaces,r.tick,r.trimTrailingZeros]);
test('the page requests exactly the displays the shared corpus states for each payload',async()=>{
 for(const example of corpus.cases){
  selectDisplayPrecision(initialDisplayPrecision());
  const displays=new Map(example.amounts.map(a=>[requested(a.request),a.display])),before=JSON.stringify(example.payload),sent=[];
  await prepareAmountDisplay(example.payload,async body=>{sent.push(...body);return body.map(r=>displays.get(requested(r)));},example.tick,example.format);
  assert.equal(JSON.stringify(example.payload),before,example.name);
  assert.deepEqual(sent.map(requested).sort(),[...displays.keys()].sort(),example.name);
 }
});

test('price pivot measures use backend price formatting while keeping their exact values',async()=>{
 selectDisplayPrecision(initialDisplayPrecision());const requests=[];
 const source={groups:[{executionPriceMean:'0.123456789012345678',markMin:'2.34',fixedPriceMax:'9.87'}]};
 await prepareAmountDisplay(source,async body=>{requests.push(...body);return body.map(r=>r.value==='0.123456789012345678'?'0.1235':r.value);});
 assert.equal(requests.length,3);assert.ok(requests.every(r=>r.decimalPlaces===4&&r.tick===null));
 assert.equal(amountText(source.groups[0].executionPriceMean,'price'),'0.1235');
 assert.equal(source.groups[0].executionPriceMean,'0.123456789012345678');
});

test('a risk report and a retained risk job are formatted by the page that shows them, not by every read; the job directory has none',()=>{
 const report={factors:[{totals:[{values:{display:{delta:'31753799.370855039072'}}}]}]};
 assert.equal(shownAmounts('portfolio/risk',report),undefined);
 assert.equal(riskReportRead('portfolio/risk'),true);
 assert.equal(riskReportRead('portfolio/risk/jobs/job-1'),true);
 assert.equal(riskReportRead('portfolio/risk/jobs'),false);
});
test('a crude preview and a reopened crude trade show their amounts',()=>{
 const preview={presentValue:'292500',quantity:'50000',sides:[{presentValue:'3505000'}]};
 assert.equal(shownAmounts('crude/preview',preview),preview);assert.equal(shownAmounts('crude/editor',preview),preview);
});
test('a PPA read shows its exact fractions: each is sent as a rational by its field\'s kind, and the read itself is untouched',async()=>{
 selectDisplayPrecision(initialDisplayPrecision());const requests=[];
 const preview={bindings:[{measures:{valueExcludingOverdue:{numerator:'28407490050852804298252338129',denominator:'200000000000000000000000'},completePv:null},
  sides:[{measures:{periods:[{quantity:{numerator:'1895',denominator:'2'},amount:{numerator:'85275',denominator:'2'},presentValue:{numerator:'-7',denominator:'1'}}]}}]}]};
 for(const path of ['ppa/preview','ppa/inspect','ppa/break-even'])assert.equal(shownAmounts(path,preview),preview);
 const before=JSON.stringify(preview);
 await prepareAmountDisplay(preview,async body=>{requests.push(...body);return body.map(r=>r.value==='1895/2'?'947.5':'shown');});
 assert.equal(JSON.stringify(preview),before);
 assert.deepEqual(requests.map(r=>[r.value,r.trimTrailingZeros]),[['28407490050852804298252338129/200000000000000000000000',false],['1895/2',true],['85275/2',false],['-7',false]]);
 assert.equal(amountText('1895/2','quantity'),'947.5');
});
test('PCA / hybrid comparison amounts and diagnostic arrays use the backend display policy',async()=>{
 selectDisplayPrecision(initialDisplayPrecision());const requests=[];
 const values={fullValueAtRisk:'1/3',fullExpectedShortfall:'2/3',candidateValueAtRisk:'4/3',candidateExpectedShortfall:'5/3',hybridValueAtRisk:'7/3',hybridExpectedShortfall:'8/3',maximumCandidateError:'1/7',maximumCandidateTailError:'2/7',maximumAbsoluteError:'3/7',maximumTailError:'4/7',maximumResidualLinearPnl:'1/100000000000000000000000',retainedVarianceShare:'11/13',reconstructionError:'1/1000000000000000',orthogonalityError:'1/1000000000000001',eigenResidual:'1/1000000000000002',eigenvalues:['17/13'],varianceShares:['19/23']};
 const original=JSON.stringify(values);
 await prepareAmountDisplay(values,async rows=>{requests.push(...rows);return rows.map(r=>r.decimalPlaces===3?'money formatted':'factor formatted');});
 assert.equal(amountText(values.maximumResidualLinearPnl,'money'),'money formatted');
 assert.equal(amountText(values.candidateValueAtRisk,'money'),'money formatted');
 assert.equal(amountText(values.retainedVarianceShare,'factor'),'factor formatted');
 assert.equal(amountText(values.eigenvalues[0],'factor'),'factor formatted');
 assert.equal(requests.length,17);assert.equal(JSON.stringify(values),original);
});
