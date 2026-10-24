export async function run({command,evaluate,wait,click,set,persona,check,shot,idle,url}){
 await command('Runtime.enable');await command('Page.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url:url+'/?example=exchange'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await evaluate(`(()=>{const send=window.fetch;window.__exchangeCatalog=null;window.fetch=async(input,init)=>{const response=await send(input,init);if(String(input)==='/examples/exchange/api/futures/catalog'&&response.ok)window.__exchangeCatalog=await response.clone().json();return response;};})()`);
 await persona('trader');await click('#example');await click('#nav-futures');
 await check('exchange dataset is visibly selected and prices are identified as synthetic',"document.querySelector('#example-dataset').value==='exchange'&&document.querySelector('#example-dataset').selectedOptions[0].textContent.includes('synthetic prices')");
 await click('#profile-open');await check('Profile does not expose deal amount precision',"!document.querySelector('#precision-money')&&!document.querySelector('#precision-price')");await click('#profile-close');
 await click('#futures-new');await click('#trade-kind-future');
 const catalog=await evaluate('window.__exchangeCatalog');
 const select=async(curve,day)=>{const c=catalog.contracts.find(c=>c.curve===curve&&new Intl.DateTimeFormat('en',{month:'short',timeZone:'UTC'}).format(new Date(c.deliveryFrom+'T00:00:00Z'))===day);if(!c)throw Error('Missing '+curve+' '+day);for(const key of ['commodity','exchange','product'])await set('#futures-contract-'+key,c[key]);await click('#futures-contract-date');await evaluate(`(()=>{const p=document.querySelector('#futures-contract-date').parentElement;for(let i=0;i<240&&!p.querySelector('[data-date="${c.deliveryFrom}"]');i++)p.querySelector('[aria-label="Next month with data"]').click();p.querySelector('[data-date="${c.deliveryFrom}"]').click();})()`);await set('#futures-contract',JSON.stringify([c.curve,c.id]));};
 await select('nymex_ng','Nov');
 await check('physical Henry Hub futures show typed lot, execution tick and lifecycle scope',"(()=>{const t=document.querySelector('#futures-terms').textContent;return t.includes('10000 MMBtu')&&t.includes('physical delivery')&&t.includes('Execution tick 0.001')&&t.includes('daily/final settlement is not processed');})()");
 await select('ice_spm','Nov');await check('monthly power distinguishes nominal MW from dated MWh per lot',"(()=>{const t=document.querySelector('#futures-terms').textContent;return t.includes('384 MWh per lot')&&t.includes('nominal 1 MW');})()");
 await select('ice_spm','Dec');await check('different delivery periods retain their own published power lot',"document.querySelector('#futures-terms').textContent.includes('416 MWh per lot')");
 await select('ice_wah','Nov');await check('basis future shows its exact benchmark and independent quote meaning',"(()=>{const t=document.querySelector('#futures-terms').textContent;return t.includes('waha_bidweek minus nymex_ng_20261101')&&t.includes('own market factor');})()");
 await select('ice_css','Oct');await check('daily swing has a fixed gas lot',"(()=>{const t=document.querySelector('#futures-terms').textContent;return t.includes('2500 MMBtu')&&t.includes('fixed total');})()");
 await select('test_calendar_day_gas','Nov');await check('daily-rate control is explicitly synthetic and retains its total period lot',"(()=>{const t=document.querySelector('#futures-terms').textContent;return t.includes('75000 MMBtu')&&t.includes('not exchange-listed')&&t.includes('per delivery day');})()");
 await shot('exchange-contract-entry');
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('trader');await click('#example');await click('#nav-futures');
 await check('returning to the original example restores its independent dated catalog',"document.querySelector('#example-dataset').value==='baseline'&&document.querySelector('#futures-contract').options.length===1&&document.querySelector('#futures-contract-commodity').options.length===2");
}
