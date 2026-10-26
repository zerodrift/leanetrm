export async function run({command,wait,click,set,submit,check,evaluate,idle,url}){
 await command('Runtime.enable');await command('Page.enable');
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await click('#profile-open');await set('#profile','trader');await click('#open');await click('#example');await click('#nav-futures');
 await wait("!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')");
 // Desk figures are backend display text, signed where they are signed; their units and measures stand beside them.
 const figures="[...document.querySelectorAll('#desk-positions .desk-figure')].every(n=>/^[+\\u2212]?[\\d,]+(?:\\.\\d+)?$/.test(n.textContent))";
 await check('the desk names the quantity unit with each size and the measure and currency beside each value',"[...document.querySelectorAll('#desk-positions .desk-unit')].some(n=>n.textContent==='MMBtu (IT)')&&[...document.querySelectorAll('#desk-positions [tabulator-field=c5] .desk-sub')].some(n=>n.textContent==='P&L · USD')&&"+figures);
 // A price and a mark name the unit they are in, beside their label: the position's currency per its quantity unit.
 await check('each price and mark states the price unit it is in, beside its label',"(()=>{const rows=[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')],sub=(row,field)=>row.querySelector(`[tabulator-field=${field}] .desk-sub`)?.textContent,unit=row=>row.querySelector('[tabulator-field=c2] .desk-unit').textContent;const priced=rows.filter(r=>sub(r,'c3')&&sub(r,'c3')!=='No fixed price'),marked=rows.filter(r=>sub(r,'c4'));return priced.length>0&&marked.length>0&&priced.every(r=>sub(r,'c3')===(r.querySelector('.desk-pill').textContent==='FUT'?'Executed':'Fixed')+' · USD/'+unit(r))&&marked.every(r=>sub(r,'c4')==='Mark · USD/'+unit(r));})()","[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].map(r=>[r.querySelector('[tabulator-field=c3] .desk-sub')?.textContent,r.querySelector('[tabulator-field=c4] .desk-sub')?.textContent])");
 await click('#blotter-columns>summary');await click('#blotter-column-choices input[value="priceUnit"]');
 await check('price units occupy their own column when chosen',"/USD\\/MMBtu/.test(document.querySelector('#desk-positions').innerText)");
 await click('#blotter-column-choices input[value="priceUnit"]');await click('#blotter-columns>summary');
 await check('futures decimal prices retain their 0.025 tick precision',"[...document.querySelectorAll('#desk-positions [tabulator-field=c3] .desk-figure')].some(n=>/^-?\\d+\\.\\d{3}$/.test(n.textContent))");
 await click('#profile-open');await check('Profile contains no amount precision controls',"!document.querySelector('#precision-money')&&!document.querySelector('#precision-quantity')");await click('#profile-close');
 await click('#desk-positions .tabulator-row:not(.tabulator-group)');await click('#side-panel-amend');await wait("!document.querySelector('#trade-editing').hidden&&!document.querySelector('#trade-amount-format').hidden");
 await check('money display settings are collapsed below the main trade workflow',"!document.querySelector('#trade-amount-format details').open&&document.querySelector('#trade-amount-format-trade-money').value==='3'&&!document.querySelector('#trade-amount-format-trade-price')&&!document.querySelector('#trade-amount-format-trade-quantity')&&Boolean(document.querySelector('#trade-amend-form').compareDocumentPosition(document.querySelector('#trade-amount-format'))&Node.DOCUMENT_POSITION_FOLLOWING)");
 const revision=await evaluate("document.querySelector('#trade-revision').value");
 await click('#trade-amount-format summary');await set('#trade-amount-format-trade-money','1');await click('#trade-amount-format button[data-role="trade"]');
 await check('money display saves independently of a financial amendment',"document.querySelector('#trade-amount-format [role=status]').textContent.includes('saved')&&document.querySelector('#trade-revision').value==="+JSON.stringify(revision));
 await click('#side-panel-close');await click('#futures-refresh');
 await wait("!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')");
 await check('noncash quantities stay numeric while prices retain tick places',figures+"&&[...document.querySelectorAll('#desk-positions [tabulator-field=c3] .desk-figure')].some(n=>/^-?\\d+\\.\\d{3}$/.test(n.textContent))");
 await click('#desk-positions .tabulator-row:not(.tabulator-group)');await click('#side-panel-amend');await wait("!document.querySelector('#trade-editing').hidden&&!document.querySelector('#trade-amount-format').hidden");
 await check('reopening a deal keeps advanced settings closed and the saved money display',"!document.querySelector('#trade-amount-format details').open&&document.querySelector('#trade-amount-format-trade-money').value==='1'");
 await click('#side-panel-close');
 await check('numeric grid retains raw exact strings separately from formatted cell text',"[...document.querySelectorAll('#desk-positions .desk-figure[title]')].some(s=>s.title!==s.textContent&&/^-?\\d+(\\.\\d+)?$/.test(s.title))");
 await click('#nav-reference');await set('#reference-table','units');await wait(idle);
 await check('common unit catalog and quantity conversion controls are visible',"!document.querySelector('#unit-converter').hidden&&document.querySelector('#reference').textContent.includes('MMBtu (IT)')&&document.querySelector('#reference').textContent.includes('bbl (42 US gal)')");
 await set('#unit-commodity','test_power');
 const supplemental=await evaluate("[...document.querySelector('#unit-from').options].some(o=>o.value==='test_power_catalog_only')");
 if(supplemental){await click('#nav-reference');await set('#reference-table','quantity_specifications');await wait(idle);await check('supplemental quantity definitions resolve labels in their own exact unit catalog',"document.querySelector('#reference').innerText.includes('Catalog kWh')&&document.querySelector('#error').hidden");await click('#nav-reference');await set('#reference-table','units');await wait(idle);}
 await set('#unit-commodity','test_power');await set('#unit-from','test_power_gigajoule');await set('#unit-to','test_power_quantity');await set('#unit-value','1');await submit('#unit-conversion-form');
 await check('nonterminating conversion fails visibly with no successful default result',"!document.querySelector('#error').hidden&&document.querySelector('#unit-conversion-result').textContent===''");
 await set('#unit-rounding','round');await set('#unit-decimals','6');await submit('#unit-conversion-form');
 await check('explicit final precision displays the converted amount with its actual unit',"document.querySelector('#unit-conversion-result').textContent==='0.277778 MWh · rounded using the selected precision'&&document.querySelector('#error').hidden");
 await set('#unit-value','2');await check('editing conversion inputs immediately invalidates the prior result',"document.querySelector('#unit-conversion-result').textContent===''");
 await set('#unit-commodity','test_liquid');await set('#unit-from','test_liquid_petroleum_barrel');await set('#unit-to','test_liquid_us_gallon');await set('#unit-rounding','exact');await submit('#unit-conversion-form');
 await check('exact barrel conversion displays gallons without an inferred heating value',"document.querySelector('#unit-conversion-result').textContent==='84 US gal · exact'");
}
