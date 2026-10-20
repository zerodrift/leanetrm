// Scripted walkthroughs of the seeded synthetic preview. Each step has one
// caption; `expect` stops the recording if the screen no longer shows the claim.
const shows=(selector,value)=>`[...document.querySelectorAll(${JSON.stringify(selector)})].some(n=>n.getClientRects().length&&n.textContent.includes(${JSON.stringify(value)}))`;
const cell=(selector,label,value)=>`[...document.querySelectorAll(${JSON.stringify(selector+' tr')})].some(r=>r.textContent.includes(${JSON.stringify(label)})&&[...r.cells].some(c=>c.textContent.trim()===${JSON.stringify(value)}))`;

async function persona(s,name){
  await s.click('#profile-open');
  if(!await s.read("document.querySelector('#preview-access').open"))await s.click('#preview-access>summary');
  await s.choose('#profile',name);await s.click('#open');
  await s.until(`document.querySelector('#profile-label').textContent===${JSON.stringify(name)}`);
}

// Dates come from the market calendar; a date with several saved closes takes
// the original synthetic close explicitly.
async function asOf(s,date){
  await s.click('#market-date-open');await s.click(`#market-date-grid button[data-date="${date}"]`);await s.pause(500);
  if(await s.visible('#market-date-closes button'))await s.click('#market-date-closes button','Initial synthetic inputs');
  await s.until("!document.querySelector('#market-date-dialog').open");
}

export const clips=[
  {name:'01-mark-correction',title:'Correct a gas future and watch dependent power reprice',gif:true,async run(s){
    await s.caption('1/7','Switch to the Market Data Editor demo user.');
    await persona(s,'Market Data Editor');
    await s.caption('2/7','Choose the Dec 18, 2020 close in the As of calendar.');
    await asOf(s,'2020-12-18');
    await s.caption('3/7','Local power forwards: January 2021 is priced at 29.1 USD/MWh.');
    await s.click('.curve-button','Local power forwards');
    await s.expect('local power at 29.1',cell('#contracts','Jan 2021','29.1'));
    await s.point('#contracts tr','Jan 2021');
    await s.caption('4/7','Price sources trace that price back to the January gas future at 3.');
    await s.click('[data-view=inputs]');await s.scroll('#source-flow');
    await s.point('#source-flow svg g','Gas futures',{hold:2000});await s.point('#source-flow .flow-summary',undefined,{hold:1600});
    await s.caption('5/7','Correct the gas future from 3 to 3.1. The original version is kept.');
    await s.click(await s.mark('#inputs tr','Gas futures','source')+' button');
    await s.type('#correction-value','3.1');await s.click('#save');
    await s.until(shows('#revisions','Change 1'));await s.point('#revisions tr','Change 1',{hold:1400});
    await s.caption('6/7','Local power reprices from 29.1 to 29.8 through its declared dependencies.');
    await s.scroll('#source-flow');
    await s.expect('local power at 29.8',shows('#source-flow .flow-summary','29.8 USD / MWh'));
    await s.point('#source-flow svg g','Gas futures',{hold:1000});await s.point('#source-flow .flow-summary',undefined,{hold:1800});
    await s.caption('7/7','The independent all-in curve has no gas input and stays at 29.1.');
    await s.click('.curve-button','Local power · all-in prices');await s.click('[data-view=prices]');
    await s.expect('all-in curve unchanged',cell('#contracts','Jan 2021','29.1'));
    await s.point('#contracts tr','Jan 2021',{hold:3500});
  }},
  {name:'02-swap-amendment',title:'Book a gas swap, amend it and review its history',async run(s){
    await s.caption('1/9','As a Trader, open the blotter on the Dec 18, 2020 close.');
    await persona(s,'Trader');await asOf(s,'2020-12-18');await s.click('#nav-futures');
    await s.caption('2/9','Start a new financial swap.');
    await s.click('#futures-new');await s.click('#trade-kind-swap');await s.until("document.querySelector('#swap-editor').open");
    await s.caption('3/9','Choose the book, legal entity and counterparty.');
    await s.choose('#swap-book','Book B');await s.choose('#swap-legal-entity','Synthetic entity 1');await s.choose('#swap-counterparty','Synthetic party 1');
    await s.caption('4/9','Synthetic gas, floating on the gas forward index for January 2021.');
    await s.choose('#swap-commodity','Synthetic gas');await s.choose('#swap-discounting','Explicitly undiscounted');
    await s.choose('#swap-index','Price');await s.fill('#swap-floating-from','2021-01-01');await s.fill('#swap-floating-to','2021-02-01');
    await s.choose('#swap-reset-frequency','Monthly');await s.choose('#swap-projection','Gas forwards');await s.choose('#swap-averaging','No averaging');
    await s.choose('#swap-floating-currency','USD');
    await s.caption('5/9','Pay a fixed 3.05 USD/MMBtu on 10,000 MMBtu.');
    await s.choose('#swap-quantity-unit','MMBtu (IT)');await s.type('#swap-quantity','10000');
    await s.choose('#swap-direction','Pay fixed');await s.choose('#swap-fixed-currency','USD');await s.type('#swap-price','3.05');
    await s.caption('6/9','Each side carries its own explicit payment rule: five days after delivery.');
    for(const side of ['fixed','floating']){
      await s.choose(`#swap-${side}-payment-frequency`,'Monthly');await s.choose(`#swap-${side}-payment-rule`,'Days after last delivery day');
      await s.type(`#swap-${side}-payment-number`,'5');await s.choose(`#swap-${side}-day-basis`,'Calendar');await s.choose(`#swap-${side}-payment-adjustment`,'Unadjusted');
    }
    await s.caption('7/9','Prepare the booking: both sides value independently. Confirm, then book.');
    await s.click('#swap-preview');await s.until("!document.querySelector('#swap-confirmation').hidden");
    await s.expect('swap PV',shows('#swap-result','Trade PV: 500.000 USD'));
    await s.point('#swap-result p','Trade PV',{hold:1400});
    await s.click('#swap-confirm');await s.click('#swap-submit');await s.until("!document.querySelector('#swap-editor').open");
    await s.caption('8/9','Sort the blotter newest first, reopen the swap and amend it to 15,000 MMBtu.');
    await s.click('#futures-holdings .tabulator-col','Trade ID');await s.click('#futures-holdings .tabulator-col','Trade ID');
    const trade=await s.read("document.querySelector('#futures-holdings .tabulator-row .tabulator-cell').textContent.trim()");
    await s.expect('newest trade is the walkthrough swap',`[...document.querySelectorAll('#futures-holdings .tabulator-row')].find(r=>r.textContent.includes(${JSON.stringify(trade)}))?.textContent.includes('10,000 MMBtu')`);
    await s.click('#futures-holdings .tabulator-row',trade);
    await s.until("document.querySelector('#swap-editor').open");
    await s.type('#swap-quantity','15000');await s.type('#swap-reason','Counterparty confirmed 15,000 MMBtu');
    await s.click('#swap-preview');await s.until("!document.querySelector('#swap-confirmation').hidden");
    await s.click('#swap-confirm');await s.click('#swap-submit');await s.until("!document.querySelector('#swap-editor').open");
    await s.caption('9/9','Revision history keeps the original booking and the amendment, with its reason.');
    await s.click('#futures-holdings .tabulator-row',trade);await s.until("document.querySelector('#swap-editor').open");
    await s.expect('two revisions',`document.querySelectorAll('#swap-history tbody tr').length===2&&${shows('#swap-history','Counterparty confirmed')}`);
    await s.scroll('#swap-history','','center');
    await s.point('#swap-history tr','Initial booking',{hold:1300});await s.point('#swap-history tr','Counterparty confirmed',{hold:1800});
  }},
  {name:'03-price-shock',title:'Run a shared price shock on the futures book',async run(s){
    await s.caption('1/7','As a Risk Analyst, open the blotter on the Dec 18, 2020 close.');
    await persona(s,'Risk Analyst');await asOf(s,'2020-12-18');await s.click('#nav-futures');
    await s.caption('2/7','Futures shocks apply to books without swaps, so select Book A, the futures book.');
    await s.click('#blotter-scope>summary');await s.choose('#blotter-books','Book A');await s.click('#blotter-scope-apply');
    await s.caption('3/7','Book A holds four gas futures on the shared January 2021 contract.');
    await s.expect('futures-only book',"!document.querySelector('#blotter-risk').hidden&&document.querySelectorAll('#futures-holdings .tabulator-row').length===4");
    await s.point('#futures-holdings .tabulator-tableholder',undefined,{hold:2000});
    await s.caption('4/7','Open Futures shocks and bump the January gas future by an explicit 0.1 USD/MMBtu.');
    await s.click('#blotter-risk');await s.scroll('#risk-card');
    await s.choose('#risk-factor','Gas futures · Jan 2021');await s.type('#risk-bump','0.1');
    await s.click('#risk-form button[type=submit]');await s.until("document.querySelector('#risk-results .tabulator-row')");
    await s.caption('5/7','One shared base, up and down scenario set revalues every holding in the book.');
    await s.point('#risk-work',undefined,{hold:1800});await s.point('#risk-results .tabulator-tableholder',undefined,{hold:1800});
    await s.caption('6/7','Delta and gamma come out in explicit units; linear futures show zero gamma.');
    await s.expect('book delta and gamma',`${shows('#risk-results','Delta')}&&${shows('#risk-results','Gamma')}`);
    await s.point('#risk-results .tabulator-col','Delta',{hold:1800});await s.point('#risk-results .tabulator-col','Gamma',{hold:1800});
    await s.scroll('#risk-plots','','center');await s.point('#risk-plots',undefined,{hold:2200});
    await s.caption('7/7','Individual holding results stay available beneath the book totals.');
    await s.click('#risk-results details>summary');await s.until("document.querySelectorAll('#risk-results .standard-data-grid').length===2");
    await s.point('#risk-results details .tabulator-tableholder',undefined,{hold:2600});
  }},
  {name:'04-as-of-view',title:'Open an as-of view of a past market date',async run(s){
    await s.caption('1/7','As a read-only Viewer, open local power forwards on the Dec 18, 2020 close.');
    await persona(s,'Viewer');await asOf(s,'2020-12-18');await s.click('.curve-button','Local power forwards');
    await s.point('#contracts tr','Jan 2021');
    await s.caption('2/7','The As of calendar offers only dates with saved closes.');
    await s.click('#market-date-open');await s.point('#market-date-grid',undefined,{hold:1800});
    await s.caption('3/7','Choose Dec 16, 2020: January was 28.8288 at that close.');
    await s.click('#market-date-grid button[data-date="2020-12-16"]');await s.until("!document.querySelector('#market-date-dialog').open");
    await s.expect('Dec 16 price',cell('#contracts','Jan 2021','28.8288'));await s.point('#contracts tr','Jan 2021',{hold:1800});
    await s.caption('4/7','Price history follows the January contract across market dates.');
    await s.click(await s.mark('#contracts tr','Jan 2021','contract')+' button');await s.scroll('#history-card','','center');
    await s.point('#history-chart',undefined,{hold:2200});await s.click('#history-close');
    await s.caption('5/7','History & knowledge time pins an effective date and a knowledge cutoff.');
    await s.click('#date-context>summary');await s.fill('#date-context-on','2020-12-16');await s.click('#date-context-latest');
    await s.caption('6/7','View this context: the workspace becomes a read-only historical view.');
    await s.click('#apply-view-context');await s.click('#close-view-context');
    await asOf(s,'2020-12-16');
    await s.expect('historical banner',shows('main','Historical view: Dec 16, 2020'));
    await s.point('main p, main div','Historical view: Dec 16, 2020',{hold:2200});await s.point('#contracts tr','Jan 2021',{hold:1500});
    await s.caption('7/7','Return to current inputs to leave; corrections after the cutoff never change this view.');
    await s.click('#date-context>summary');await s.click('#current-view-context');await s.pause(1200);
  }},
  {name:'05-custom-peak-block',title:'Build a custom peak block and check its settlement',async run(s){
    await s.caption('1/8','As a Market Data Editor, open Power TOUs and its published ISO peak blocks.');
    await persona(s,'Market Data Editor');await s.click('#nav-power');await s.point('#tou-list',undefined,{hold:1600});
    await s.caption('2/8','Draft a custom super-peak: Monday–Friday, HE15–HE20, Eastern prevailing time.');
    await s.click('#tou-new');await s.type('#tou-code','synthetic_super_peak');await s.type('#tou-name','Synthetic super-peak');
    await s.choose('#tou-clock','Synthetic Eastern');await s.choose('#tou-start','HE15 first');await s.choose('#tou-end','HE20 last');await s.choose('#tou-saturday','Monday–Friday');
    await s.click('#tou-save');await s.until(shows('#tou-version','draft'));
    await s.caption('3/8','Calculations use published versions only, so an approver publishes the draft.');
    await persona(s,'Market Data Approver');await s.click('#nav-power');
    await s.click(await s.mark('#tou-list tr','Synthetic super-peak','product')+' button');await s.click('#tou-publish');
    await s.until(shows('#tou-version','published'));await s.point('#tou-list tr','Synthetic super-peak',{hold:1400});
    await s.caption('4/8','In the hours & settlement check, choose the Dec 18 close and local power for January 2021.');
    await asOf(s,'2020-12-18');await s.choose('#power-curve','Local power forwards');await s.choose('#power-contract','2021-01-01');
    await s.caption('5/8','Select the new super-peak product, on-peak hours and hourly intervals.');
    await s.choose('#power-product','Synthetic super-peak');await s.choose('#power-block','On-peak');await s.choose('#power-minutes','Hourly');
    await s.point('#power-normal-grid',undefined,{hold:1800});
    await s.caption('6/8','Settle a flat 25 MW across the block.');
    await s.choose('#power-quantity-basis','Flat MW');await s.type('#power-mw','25');
    await s.type('#power-price-type','Synthetic index');await s.type('#power-timing','End of day');
    await s.click('#power-form button[type=submit]');await s.until("!document.querySelector('#power-result').hidden");
    await s.caption('7/8','120 peak hours and 3,000 MWh at 29.1 USD/MWh settle to 87,300 USD; Jan 1 is a holiday.');
    await s.expect('settlement summary',shows('#power-summary','120 hours · 3,000 MWh · 29.1 USD/MWh · amount 87,300.000 USD'));
    await s.point('#power-summary',undefined,{hold:2000});await s.point('#power-holiday-note',undefined,{hold:1400});
    await s.caption('8/8','Each settlement interval lists its UTC bounds, price and energy.');
    await s.scroll('#power-intervals','','start');await s.pause(2500);
  }}
];
