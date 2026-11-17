// Options (Trading): the option chain of a product's expiry at the selected close, read from the product's stored volatility
// surface, with each series' settlement, implied volatility and sensitivities as its fit stored them; the booked option
// positions; and a strategy of legs on one expiry with the payoff the backend states for it. A product with no
// surface at the close says so and sends the person to Vol surfaces, where a close is calibrated. Runs after the HTTP
// checks of the exchange example, which leave nymex_on calibrated at the latest working snapshot, and before the Vol
// surfaces suite: that suite leaves a refit running, and this one fits nothing and changes nothing it reads.
const q=JSON.stringify;
const page='#options-page';
const state=`document.querySelector(${q(page)}).dataset.state`;

export async function run({command,evaluate,wait,click,persona,check,idle,fitsWidth,url}){
 await command('Runtime.enable');await command('Page.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 // Every surface and position read the page makes with its answer; and a switch that answers the next surface read as a
 // close with no surface, which this example's latest snapshot no longer is for either product.
 const {identifier}=await command('Page.addScriptToEvaluateOnNewDocument',{source:`
  window.optionCalls=[];window.optionsUnfitted=false;window.optionEntryValuations=[];const send=window.fetch;
  window.fetch=async(input,init)=>{
   const path=String(typeof input==='string'?input:input.url),call=/[/](vol-surfaces[/]read|options[/]positions|options[/]strategy[/]setup)$/.exec(path);
   if(window.refuseOptionEntryValuation&&/[/]trading[/](workspace|blotter)([/]|$)/.test(path)){
    window.optionEntryValuations.push(path);
    return new Response(JSON.stringify({code:'request_failed',message:'Synthetic portfolio valuation refusal.'}),{status:503,headers:{'Content-Type':'application/json'}});
   }
   if(!call)return send(input,init);
   const record={path:call[1],body:init?.body?JSON.parse(init.body):null};window.optionCalls.push(record);
   const response=await send(input,init);record.status=response.status;record.answer=await response.clone().json().catch(()=>null);
   if(call[1]==='vol-surfaces/read'&&response.ok&&window.optionsUnfitted)
    return new Response(JSON.stringify({...record.answer,state:'notFitted',surface:null,revision:null,job:null}),{status:200,headers:{'Content-Type':'application/json'}});
   return response;
  };`});
 const calls=path=>`window.optionCalls.filter(c=>c.path===${q(path)})`;
 const settled=(expression,ms)=>wait(expression,ms).catch(async error=>{throw Error(error.message+' · observed '+q(await evaluate(
  `({state:${state},page:document.querySelector('#page-title').textContent,error:document.querySelector('#options-error')?.textContent,
   globalError:document.querySelector('#error')?.textContent,entryValuations:window.optionEntryValuations,
   calls:window.optionCalls.slice(-3).map(c=>[c.path,c.status,c.answer?.state??c.answer?.code])})`)));});
 const chip=product=>`document.querySelector('.options-product[data-product=${product}]')`;
 // Choosing a product's chip reads its surface. A product already chosen is read by the page itself when it opens, so its
 // newest read is waited for instead.
 async function choose(product){
  await settled(`!!${chip(product)}&&${idle}`,30000);
  const answered=`${calls('vol-surfaces/read')}.at(-1)?.status!==undefined&&${calls('vol-surfaces/read')}.at(-1).body.product===${q(product)}&&${state}!=='loading'`;
  if(await evaluate(`${chip(product)}.getAttribute('aria-pressed')==='true'`))return settled(answered,30000);
  const reads=await evaluate(`${calls('vol-surfaces/read')}.length`);
  await click(`.options-product[data-product=${product}]`);
  await settled(`${calls('vol-surfaces/read')}.length>${reads}&&${answered}`,30000);
 }
 try{
  await command('Page.navigate',{url:url+'/?example=exchange'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle,60000);
  await persona('viewer');
  await check('Options is not listed for a market reader without Read trades',"document.querySelector('#nav-options')?.hidden===true");
  await persona('risk');
  await check('Options is listed in Trading for a reader of market data and trades',
   "(()=>{const n=document.querySelector('#nav-options');return !!n&&!n.hidden&&!!n.closest('#nav-group-trading');})()");
  await click('#example');await click('#nav-options');
  await settled(`document.querySelectorAll('.options-product').length>1&&${idle}`,30000);
  await check('the page is titled Options under Trading, names the working snapshot it reads and chooses no product',
   `document.querySelector('#page-title').textContent==='Options'&&document.querySelector('#page-section').textContent==='TRADING'&&
    document.querySelector('${page} .options-eyebrow').textContent==='Working snapshot'&&/2026/.test(document.querySelector('#options-here').textContent)&&
    [...document.querySelectorAll('.options-product')].map(c=>c.dataset.product).sort().join()==='nymex_lo,nymex_on'&&!document.querySelector('.options-product[aria-pressed=true]')&&
    !!document.querySelector('#options-placeholder').offsetParent&&${calls('vol-surfaces/read')}.length===0`,
   `[document.querySelector('#page-title').textContent,document.querySelector('${page} .options-hero').textContent,${state}]`);
  await check('the page opens on the chain and offers Positions and Strategy',
   `document.querySelector('#options-view-chain').getAttribute('aria-pressed')==='true'&&!document.querySelector('#options-view-positions').disabled&&
    !document.querySelector('#options-view-strategy').disabled`);

  await choose('nymex_on');
  await settled(`${state}==='fitted'&&!!document.querySelector('#options-chain tbody tr')`,30000);
  const surface=await evaluate(`${calls('vol-surfaces/read')}.at(-1).answer.surface`);
  const live=surface.expiries.filter(e=>e.status!=='option_expired'),first=live[0];
  const listed=new Set(surface.series.filter(s=>s.series.expiryAt===first.expiryAt).map(s=>s.series.strike));
  await check('the chain reads the stored surface and the held lots at the selected context, with the model stated',
   `(r=>r.status===200&&r.body.product==='nymex_on'&&r.body.model==='smile_quadratic_log_moneyness_v1'&&r.body.context.convention==='futures_option_premium_v1')(${calls('vol-surfaces/read')}.at(-1))&&
    (p=>p.status===200&&p.body.model==='smile_quadratic_log_moneyness_v1'&&Array.isArray(p.answer.held))(${calls('options/positions')}.at(-1))`,
   `${calls('options/positions')}.at(-1)`);
  await check('the chain opens on the first live expiry with one row per strike it lists and the at-the-money strike marked',
   `document.querySelectorAll('#options-expiries .options-expiry').length===${live.length}&&
    document.querySelector('#options-expiries [aria-current=true]').dataset.expiry===${q(first.expiryAt)}&&
    document.querySelectorAll('#options-chain tbody tr').length===${listed.size}&&
    [...document.querySelectorAll('#options-chain tr.options-atm')].map(r=>r.dataset.strike).join()===${q(first.atTheMoneyStrike??'')}&&
    document.querySelector('#options-facts').textContent.includes(${q(first.underlyingCode)})`,
   `[document.querySelectorAll('#options-chain tbody tr').length,[...document.querySelectorAll('#options-chain tr.options-atm')].map(r=>r.dataset.strike),document.querySelector('#options-facts').textContent]`);
  // Each side's delta is the stored figure to two decimals: a call's between 0 and 1, a put's between -1 and 0.
  const solved=surface.series.filter(s=>s.series.expiryAt===first.expiryAt&&s.analytics?.delta.value!=null);
  const shownDelta=code=>`[...document.querySelectorAll('#options-chain td[data-series="${code}"]')].map(td=>td.textContent)`;
  // The pair at the money, whose deltas are well inside the unit interval, so two decimals show their sign and size.
  const side=right=>solved.find(s=>s.series.right===right&&s.series.strike===first.atTheMoneyStrike)??solved.find(s=>s.series.right===right);
  const call=side('call'),put=side('put');
  await check('each side shows its stored delta to two decimals, calls mirrored about the strike',
   `${q(!!call&&!!put)}&&(c=>c.length===7&&Math.abs(Number(c[4])-(${Number(call?.analytics.delta.value)}))<=0.005+1e-9&&/^0[.]\\d\\d$/.test(c[4]))(${shownDelta(call?.series.code??'')})&&
    (p=>p.length===7&&Math.abs(Number(p[2])-(${Number(put?.analytics.delta.value)}))<=0.005+1e-9&&/^-0[.]\\d\\d$/.test(p[2]))(${shownDelta(put?.series.code??'')})`,
   `[${shownDelta(call?.series.code??'')},${shownDelta(put?.series.code??'')}]`);
  // What the surface stores as missing is drawn as a dash with its reason; every other cell shows its stored figure.
  const absent=surface.series.filter(s=>s.series.expiryAt===first.expiryAt).flatMap(s=>['delta','gamma','vega','theta'].filter(m=>!s.analytics||s.analytics[m].value===null)).length;
  await check('a figure the fit could not state is a dash that says why',
   `(cells=>cells.length>=${absent}&&cells.every(td=>td.textContent==='—'&&td.title.length>0))([...document.querySelectorAll('#options-chain td.options-missing-figure')])`,
   "[...document.querySelectorAll('#options-chain td.options-missing-figure')].map(td=>[td.dataset.series,td.title]).slice(0,5)");
  await check('the page states what its figures are and that Risk reports keep their own',
   "/Per unit of quote/.test(document.querySelector('#options-basis').textContent)&&/option_sensitivities_layers_v1/.test(document.querySelector('#options-basis').textContent)");
  await check('the chain fits the page width',fitsWidth);

  await click(`#options-chain td[data-series="${call.series.code}"]`);
  await check('choosing a series shows its stored figures and offers its ticket and its surface',
   `document.querySelector('#options-readout').textContent.includes(${q(call.series.code)})&&!!document.querySelector('#options-open-surface')&&!!document.querySelector('#options-book')&&
    document.querySelectorAll('#options-chain td.options-selected').length===7`,"document.querySelector('#options-readout').textContent");
  await click('#options-open-surface');
  await settled(`document.querySelector('#page-title').textContent==='Vol surfaces'&&document.querySelector('.vol-product-card[aria-pressed=true]')?.dataset.product==='nymex_on'&&
   document.querySelector('#vol-readout').textContent.includes(${q(call.series.code)})`,30000);
  await check('Open on Vol surfaces opens the product\'s surface with the series highlighted',`!!document.querySelector('#vol-open-chain')`);
  // The surface links back to the series' row in its expiry's chain.
  await click('#vol-open-chain');
  await settled(`document.querySelector('#page-title').textContent==='Options'&&${state}==='fitted'&&document.querySelectorAll('#options-chain td.options-selected').length===7`,30000);
  await check('Open chain returns to the same series in the chain of its expiry',
   `document.querySelector('#options-chain td.options-selected').dataset.series===${q(call.series.code)}&&
    document.querySelector('#options-expiries [aria-current=true]').dataset.expiry===${q(call.series.expiryAt)}&&document.querySelector('.options-product[aria-pressed=true]').dataset.product==='nymex_on'`,
   "[document.querySelector('#options-chain td.options-selected')?.dataset.series,document.querySelector('#options-expiries [aria-current=true]')?.dataset.expiry]");
  // Booking opens the option ticket on New trade's page with the series selected as a person would select it. The risk analyst books nothing.
  await persona('trader');await click('#example');await click('#nav-options');await choose('nymex_on');
  await settled(`${state}==='fitted'&&!!document.querySelector('#options-chain tbody tr')`,30000);
  await click(`#options-chain td[data-series="${call.series.code}"]`);
  // Opening the native ticket consumes entry metadata; an unrelated portfolio valuation may be unavailable.
  await evaluate('window.refuseOptionEntryValuation=true');await click('#options-book');
  await settled("document.querySelector('#page-title').textContent==='New futures option'&&document.querySelector('#futures-page').hasAttribute('data-entry')&&!!document.querySelector('#option-page')?.offsetParent&&document.querySelector('#option-strike').value!==''",30000);
  await check('opening a selected option ticket needs no portfolio valuation',"window.optionEntryValuations.length===0&&document.querySelector('#error').hidden","window.optionEntryValuations");
  await check('Book opens the option ticket on that series, with no execution terms filled in',
   `document.querySelector('#option-right').value===${q(call.series.right)}&&Number(document.querySelector('#option-strike').value)===${Number(call.series.strike)}&&
    document.querySelector('#option-underlying').value===${q(call.series.underlyingCode)}&&document.querySelector('#option-page').textContent.includes(${q(call.series.code)})&&
    ['#option-side','#option-lots','#option-premium'].every(id=>document.querySelector(id).value==='')`,
   "['exchange','commodity','underlying','right','strike','side','lots','premium'].map(id=>document.querySelector('#option-'+id)?.value)");
  await evaluate('window.refuseOptionEntryValuation=false');await persona('risk');await click('#example');

  // Positions: the booked option positions with the backend's figures, subtotals and totals. The page adds nothing up.
  await click('#nav-options');await choose('nymex_on');await settled(`${state}==='fitted'`,30000);
  await click('#options-view-positions');await settled(`${state}==='positions'`,30000);
  const held=await evaluate(`${calls('options/positions')}.at(-1).answer`);
  const rows=held.groups.flatMap(g=>g.rows);
  await check('Positions lists each booked option position under its underlying future, or says there are none',
   `document.querySelector('#options-view-positions').getAttribute('aria-pressed')==='true'&&document.querySelector('#options-products').hidden&&
    (${rows.length}===0?/No option positions/.test(document.querySelector('#options-positions').textContent)
     :document.querySelectorAll('#options-position-table tbody').length===${held.groups.length}&&document.querySelectorAll('#options-position-table tbody tr[data-series]').length===${rows.length}&&
      document.querySelectorAll('#options-position-table .options-position-subtotal').length===${held.groups.length}&&
      document.querySelectorAll('#options-position-table .options-position-total').length===${held.totals.length})`,
   "document.querySelector('#options-positions').textContent.slice(0,400)");
  await check('a position figure the backend could not state is a dash that says why',
   "[...document.querySelectorAll('#options-position-table td.options-missing-figure')].every(td=>td.textContent==='—'&&td.title.length>0)");
  await check('the positions fit the page width',fitsWidth);
  await click('#options-view-chain');await settled(`${state}==='fitted'&&!!document.querySelector('#options-chain tbody tr')`,30000);
  await check('returning to the chain shows the chosen product\'s chain again',"!document.querySelector('#options-products').hidden&&document.querySelector('.options-product[aria-pressed=true]')?.dataset.product==='nymex_on'");

  // Strategy: legs on the expiry in view. Its setup is read from the stored surface and its analysis is the backend's, sent
  // over the asynchronous read channel; the page draws what comes back.
  const strategy=`document.querySelector('#options-strategy').dataset.state`;
  const enter=(selector,value)=>evaluate(`(n=>{n.value=${q(value)};n.dispatchEvent(new Event('change'));})(document.querySelector(${q(selector)}))`);
  await click(`#options-chain td[data-series="${call.series.code}"]`);
  await check('a chosen series can be bought or sold in the strategy',`!document.querySelector('#options-strategy-buy').disabled&&!document.querySelector('#options-strategy-sell').disabled`);
  await click('#options-view-strategy');
  await settled(`${state}==='strategy'&&document.querySelectorAll('.options-template').length===7&&${strategy}==='empty'`,30000);
  const offer=await evaluate(`${calls('options/strategy/setup')}.at(-1)`);
  await check('Strategy reads its setup for the product, model and expiry in view and shows the offered range and the valuation curve, with no legs',
   `(r=>r.status===200&&r.body.product==='nymex_on'&&r.body.model==='smile_quadratic_log_moneyness_v1'&&r.body.expiryAt===${q(first.expiryAt)}&&r.answer.state==='fitted')(${calls('options/strategy/setup')}.at(-1))&&
    document.querySelector('#options-view-strategy').getAttribute('aria-pressed')==='true'&&!document.querySelector('#options-expiries').closest('[hidden]')&&
    Number(document.querySelector('#options-strategy-lower').value)===${Number(offer.answer.offered.prices.lower)}&&Number(document.querySelector('#options-strategy-upper').value)===${Number(offer.answer.offered.prices.upper)}&&
    document.querySelectorAll('.options-strategy-curve').length===1&&document.querySelector('#options-strategy-analyse').disabled&&/No legs yet/.test(document.querySelector('#options-strategy-legs').textContent)`,
   `[${strategy},document.querySelector('#options-strategy-controls').textContent,document.querySelector('#options-strategy-message').textContent]`);
  await check('each template is offered with its legs, or is marked unavailable and says which strike or series the expiry does not list',
   `${q(offer.answer.templates.filter(t=>t.refusal).map(t=>t.id).sort().join())}===[...document.querySelectorAll('.options-template[aria-disabled=true]')].map(c=>c.dataset.template).sort().join()&&
    [...document.querySelectorAll('.options-template[aria-disabled=true]')].every(c=>c.title.length>0)&&!document.querySelector('.options-template[data-template=straddle]').hasAttribute('aria-disabled')`,
   "[...document.querySelectorAll('.options-template')].map(c=>[c.dataset.template,c.getAttribute('aria-disabled'),c.title])");
  // Fewer price points keep this analysis short, inside the longest wait a page action may take; the range and the curve
  // stay as offered.
  await enter('#options-strategy-points','11');
  await click('.options-template[data-template=straddle]');
  await settled(`${strategy}==='analysed'||${strategy}==='refused'`,60000);
  const money=offer.answer.atTheMoneyStrike;
  await check('a straddle is analysed: two legs at the money at their settlements, the payoff drawn, and the premium, extremes and breakevens stated',
   `${strategy}==='analysed'&&[...document.querySelectorAll('#options-strategy-legs tbody tr')].map(r=>r.dataset.side+' '+r.querySelector('.options-leg-name').textContent).join()===${q(`buy ${Number(money)} call,buy ${Number(money)} put`)}&&
    [...document.querySelectorAll('#options-strategy-legs .options-leg-entry')].every(i=>Number(i.value)>0)&&!!document.querySelector('#options-strategy-chart canvas')&&
    (card=>/Net premium\\s*Debit [\\d,]+[.]\\d\\d USD/.test(card)&&/Maximum profit\\s*Unbounded/.test(card)&&/Maximum loss\\s*-[\\d,]+[.]\\d\\d USD/.test(card)&&/Breakevens\\s*[\\d.]+ · [\\d.]+/.test(card))(document.querySelector('#options-strategy-analysis').innerText)&&
    document.querySelectorAll('#options-strategy-tiles .options-tile').length===5&&/At expiry/.test(document.querySelector('#options-strategy-chart .options-strategy-plot').getAttribute('aria-label'))`,
   `[${strategy},document.querySelector('#options-strategy-message').textContent,document.querySelector('#options-strategy-analysis').innerText,document.querySelector('#options-strategy-legs').innerText]`);
  await check('each leg shows its implied volatility and delta, or a dash that says why, and a strategy figure that is missing says why',
   `[...document.querySelectorAll('#options-strategy-legs .options-leg-figure')].every(td=>td.textContent==='—'?td.title.length>0:/^-?[\\d.]+%?$/.test(td.textContent))&&
    [...document.querySelectorAll('#options-strategy-tiles .options-tile')].every(t=>t.querySelector('.options-tile-value').textContent==='—'?t.title.length>0:/^-?[\\d,.]+$/.test(t.querySelector('.options-tile-value').textContent))&&
    /option_expiry_exercise_value_v1/.test(document.querySelector('#options-strategy-basis').textContent)&&/Risk reports keep theirs/.test(document.querySelector('#options-strategy-basis').textContent)`,
   "[document.querySelector('#options-strategy-legs').innerText,document.querySelector('#options-strategy-tiles').innerText]");
  await check('the dated curve at the valuation instant is drawn beside the expiry payoff, or the page says why there is none',
   `(label=>/Valuation/.test(label)||!!document.querySelector('#options-strategy-refused'))(document.querySelector('#options-strategy-chart .options-strategy-plot').getAttribute('aria-label'))`,
   "document.querySelector('#options-strategy-chart .options-strategy-plot').getAttribute('aria-label')");
  await check('the strategy fits the page width',fitsWidth);
  await enter('#options-strategy-legs .options-leg-lots','2');
  await check('changing a leg leaves the last analysis on show, marked as no longer the choice\'s',
   `${strategy}==='stale'&&/Analyse again/.test(document.querySelector('#options-strategy-message').textContent)&&!!document.querySelector('#options-strategy-chart canvas')`,
   `[${strategy},document.querySelector('#options-strategy-message').textContent]`);
  // A lower bound of zero is refused by the backend and said; the legs stay.
  await enter('#options-strategy-lower','0');
  await click('#options-strategy-analyse');
  await settled(`${strategy}==='refused'`,60000);
  await check('a range the backend refuses is said with its code, and the legs are kept',
   `/strategy_price_range/.test(document.querySelector('#options-strategy-message').textContent)&&document.querySelectorAll('#options-strategy-legs tbody tr[data-series]').length===2`,
   "document.querySelector('#options-strategy-message').textContent");
  await click('#options-strategy-clear');
  await check('Clear legs empties the strategy',`/No legs yet/.test(document.querySelector('#options-strategy-legs').textContent)&&document.querySelector('#options-strategy-analyse').disabled`);
  // A leg comes from the chain: the series chosen there, at the settlement shown.
  await click('#options-view-chain');await settled(`${state}==='fitted'&&!!document.querySelector('#options-chain tbody tr')`,30000);
  await click(`#options-chain td[data-series="${call.series.code}"]`);await click('#options-strategy-sell');
  await check('a series already in the strategy is not offered again',"document.querySelector('#options-strategy-sell').disabled&&document.querySelector('#options-strategy-buy').disabled");
  await click('#options-view-strategy');await settled(`${state}==='strategy'&&document.querySelectorAll('#options-strategy-legs tbody tr[data-series]').length===1`,30000);
  await check('a leg added from the chain is a sale of that series at its settlement',
   `(r=>r.dataset.series===${q(call.series.code)}&&r.dataset.side==='sell'&&Number(r.querySelector('.options-leg-entry').value)===${Number(call.series.settlement)})(document.querySelector('#options-strategy-legs tbody tr'))`,
   "document.querySelector('#options-strategy-legs').innerText");
  await click('#options-strategy-clear');
  await click('#options-view-chain');await settled(`${state}==='fitted'&&!!document.querySelector('#options-chain tbody tr')`,30000);

  // A close with no surface: the page says so and offers Vol surfaces, and draws no chain.
  await evaluate('window.optionsUnfitted=true');
  await click('#nav-options');await settled(`${state}==='notFitted'`,30000);
  await check('a product not calibrated at this close shows that and no chain',
   `!!document.querySelector('#options-missing').offsetParent&&/not calibrated at this close/.test(document.querySelector('#options-missing').textContent)&&
    !document.querySelector('#options-chain')?.offsetParent&&document.querySelector('#options-calibrate').textContent==='Calibrate on Vol surfaces →'`,
   "document.querySelector('#options-missing').textContent");
  await evaluate('window.optionsUnfitted=false');
  await click('#options-calibrate');
  await settled("document.querySelector('#page-title').textContent==='Vol surfaces'&&document.querySelector('.vol-product-card[aria-pressed=true]')?.dataset.product==='nymex_on'",30000);
  await check('the calibrate link opens the product on Vol surfaces','true');
 }finally{await command('Page.removeScriptToEvaluateOnNewDocument',{identifier});}
}
