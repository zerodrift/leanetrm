import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {prepareAmountDisplay,amountText,selectDisplayPrecision,initialDisplayPrecision} from '../dist/amount-display.js';
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
