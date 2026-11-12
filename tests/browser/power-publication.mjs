// Real controls and business responses against the runner-owned captured-price fixture.
import {selectPublication} from './publication-library.mjs';
export async function run({command,evaluate,wait,click,set,persona,check,shot,idle,url}){
 await command('Runtime.enable');await command('Page.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1500,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelector('#nav-configuration')&&"+idle,30000);await persona('viewer');
 await click('#nav-configuration');await click('#price-setup-tabs-publications');
 const fixture=await evaluate(`(async()=>{
  const prices=await(await fetch('/api/power-imports')).json();
  const source=prices.sources.find(s=>s.provider==='PJM'&&s.market==='DA'&&s.availableDates.includes('2021-03-14'));
  const catalog=await(await fetch('/api/price-publications?snapshotId='+document.querySelector('#snapshot').value)).json();
  const publication=catalog.publications.find(p=>p.name==='Captured hourly compatibility check'&&p.clock.code===source.clock_code&&p.indices[0]?.unit.multiplier==='1');
  return {source:source.id,publication:publication.key,index:publication.indices[0].key.code,knownAt:prices.knownAt};
 })()`);
 await selectPublication({evaluate,wait,click,idle},`${fixture.publication.id}:${fixture.publication.version}`);
 await check('captured inspection requires explicit source, market, dates and cutoff',"!document.querySelector('#price-publications-power').hidden&&['source','market','from','through','known'].every(id=>document.querySelector('#power-publication-'+id).value==='')");
 await click('#power-publication-load-sources');
 await set('#power-publication-index',fixture.index);await set('#power-publication-source',fixture.source);await set('#power-publication-market','DA');
 await set('#power-publication-from','2021-03-14');await set('#power-publication-through','2021-03-14');await set('#power-publication-known',fixture.knownAt);
 await click('#power-publication-check');
 await check('reader inspects all 23 real DST hours from the existing source with unqualified status and correction lineage',"document.querySelector('#power-publication-result').textContent.includes('23 expected intervals · 0 missing')&&document.querySelector('#power-publication-result').textContent.includes('not qualified final settlements')&&document.querySelectorAll('#power-publication-result tbody tr').length===23&&document.querySelector('#power-publication-result tbody tr td:nth-child(3)').textContent==='-9'&&document.querySelector('#error').hidden");
 await set('#power-publication-market','RT');
 await check('changing the market clears captured-price evidence',"document.querySelector('#power-publication-result').textContent===''");
 await click('#power-publication-check');
 await check('source and market mismatch is refused by the server',"!document.querySelector('#error').hidden&&document.querySelector('#power-publication-result').textContent===''");
 await set('#power-publication-market','DA');await set('#power-publication-from','2021-03-15');await set('#power-publication-through','2021-03-15');await click('#power-publication-check');
 await check('missing hourly capture stays missing instead of inventing settlement prices',"document.querySelector('#power-publication-result').textContent.includes('24 expected intervals · 24 missing')&&document.querySelectorAll('#power-publication-result tbody tr').length===0&&document.querySelector('#error').hidden");
 const productFixture=await evaluate(`(async()=>{
  const snapshot=document.querySelector('#snapshot').value;
  const catalog=await(await fetch('/api/price-publications?snapshotId='+snapshot)).json();
  const publication=catalog.publications.find(p=>p.name==='PJM 2020 captured product check');
  const choices=await(await fetch('/api/price-publications/power/products?snapshotId='+snapshot)).json();
  const product=choices.products.find(p=>p.code==='pjm');
  const prices=await(await fetch('/api/power-imports')).json();
  return {publication:publication.key,index:publication.indices[0].key.code,product:product.code+':'+product.version,knownAt:prices.knownAt};
 })()`);
 await selectPublication({evaluate,wait,click,idle},`${productFixture.publication.id}:${productFixture.publication.version}`);
 await click('#power-publication-load-sources');await click('#power-publication-load-products');
 await check('product inspection requires explicit hours and averaging and identifies current-definition diagnostics',"['product','block','averaging'].every(id=>document.querySelector('#power-publication-'+id).value==='')&&document.querySelector('#price-publications-power').textContent.includes('current published product definition')");
 await set('#power-publication-index',productFixture.index);await set('#power-publication-source',fixture.source);await set('#power-publication-market','DA');
 await set('#power-publication-from','2020-12-18');await set('#power-publication-through','2020-12-19');await set('#power-publication-known',productFixture.knownAt);
 await set('#power-publication-product',productFixture.product);await set('#power-publication-block','off');await set('#power-publication-averaging','duration_weighted_price_mean_v1');
 await click('#power-publication-check-product');
 await check('native product mean uses historical Friday and Saturday hours and keeps the nonfinal boundary',"document.querySelector('#power-publication-result').textContent.includes('Selected-product coverage · 32 expected intervals · 0 missing')&&document.querySelector('#power-publication-result').textContent.includes('Native-hour average · 36.')&&document.querySelector('#power-publication-result').textContent.includes('not an official monthly exchange fixing')&&document.querySelector('#error').hidden");
 await set('#power-publication-from','2020-12-25');await set('#power-publication-through','2020-12-25');await click('#power-publication-check-product');
 await check('Christmas off-peak uses all 24 real hours and retains negative prices',"document.querySelector('#power-publication-result').textContent.includes('24 expected intervals · 0 missing')&&document.querySelector('#power-publication-result').textContent.includes('Native-hour average · -5.')&&document.querySelector('#power-publication-result').textContent.includes('Holidays in range: 2020-12-25')");
 await set('#power-publication-block','peak');await click('#power-publication-check-product');
 await check('a holiday with no peak hours has no invented product average',"document.querySelector('#power-publication-result').textContent.includes('0 expected intervals · 0 missing')&&document.querySelector('#power-publication-result').textContent.includes('Native-hour average · Unavailable')&&document.querySelector('#power-publication-result').textContent.includes('outside_product')");
 await set('#power-publication-from','2020-12-21');await set('#power-publication-through','2020-12-21');await click('#power-publication-check-product');
 await check('complete peak coverage is distinct from incomplete full-day capture',"document.querySelector('#power-publication-result').textContent.includes('Selected-product coverage · 16 expected intervals · 0 missing')&&document.querySelector('#power-publication-result').textContent.includes('Native-hour average · 0.')&&document.querySelector('#power-publication-result details').textContent.includes('24 expected intervals · 8 missing')");
 await shot('power-publication-capture');
}
