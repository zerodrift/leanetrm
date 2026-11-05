// The Blotter and Risk share the same authorized membership picker; selection does not invent a scope.
export async function run({command,evaluate,wait,click,set,persona,check,shot,idle,url}){
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);await persona('risk');
 const frame=await evaluate("(async()=>{const b=await(await fetch('/api/workspace')).json();return b.frames.find(f=>f.at.startsWith('2020-12-18')&&b.initialFrameIds.includes(f.id)).id;})()");
 await set('#snapshot',frame);await wait(idle);await click('#nav-futures');await wait("document.querySelector('#blotter-books').options.length>0&&"+idle);
 await check('personal Blotter pins replace fixed physical-product shortcuts',"!document.querySelector('#nav-gas')&&!document.querySelector('#nav-crude')&&document.querySelector('#blotter-sidebar').parentElement.textContent.includes('Pin to sidebar')");
 await click('#blotter-scope>summary');await click('#blotter-portfolios-picker>summary');
 const portfolio=await evaluate("document.querySelector('#blotter-portfolios-picker input[type=checkbox]').value");
 await click(`#blotter-portfolios-picker input[value="${portfolio}"]`);await click('#blotter-books-picker>summary');
 const members=await evaluate(`Array.from(document.querySelector('#blotter-books').options).filter(o=>o.dataset.portfolioId===${JSON.stringify(portfolio)}).map(o=>o.value)`);
 await check('portfolio selection offers exactly its authorized books',`JSON.stringify([...document.querySelectorAll('#blotter-books-picker .scope-option input')].map(n=>n.value))===${JSON.stringify(JSON.stringify(members))}`);
 const book=members[0],name=await evaluate(`Array.from(document.querySelector('#blotter-books').options).find(o=>o.value===${JSON.stringify(book)}).text`);
 await set('#blotter-books-picker input[type=search]',name);await click(`#blotter-books-picker input[type=checkbox][value="${book}"]`);
 await shot('blotter-scope-picker');await click('#blotter-scope-apply');await wait(idle);
 await check('applying the shared picker retains the exact chosen portfolio and book',`document.querySelector('#blotter-books').selectedOptions[0]?.value===${JSON.stringify(book)}&&document.querySelector('#blotter-portfolios').selectedOptions[0]?.value===${JSON.stringify(portfolio)}&&document.querySelector('#error').hidden`);
 await click('#nav-risk');await wait(idle);
 await click('#risk-workspace-portfolios-picker>summary');await click(`#risk-workspace-portfolios-picker input[value="${portfolio}"]`);await wait(idle);await click('#risk-workspace-books-picker>summary');
 await check('Risk uses the same dependent book choices',`JSON.stringify([...document.querySelectorAll('#risk-workspace-books-picker .scope-option input')].map(n=>n.value))===${JSON.stringify(JSON.stringify(members))}`);
 await shot('risk-scope-picker');
 await click('#risk-jobs-new');await wait("!document.querySelector('#workspace-risk-fields').disabled&&"+idle);
 await click('#workspace-risk-form input[value=custom]');
 await evaluate("const n=document.querySelector('#workspace-risk-bump');n.focus();n.select()");await command('Input.insertText',{text:'0.07'});
 const ids="id=>[...document.getElementById(id).selectedOptions].map(o=>o.value)",before=await evaluate(`JSON.stringify(['risk-workspace-books','risk-workspace-portfolios'].map(${ids}))`);
 const change=`document.querySelector('#risk-workspace-books-picker input[type=checkbox][value="${book}"]').click();true`;
 await evaluate(change);await wait("!document.querySelector('#risk-page-prompt').hidden");await click('#risk-page-prompt button.secondary');
 await check('keeping an edited Risk setup restores exact scope IDs and visible picker choices',`JSON.stringify(['risk-workspace-books','risk-workspace-portfolios'].map(${ids}))===${JSON.stringify(before)}&&document.querySelector('#risk-workspace-books-picker .scope-value').textContent==='All included books'&&!document.querySelector('#risk-workspace-books-picker .scope-chip')&&document.querySelector('#workspace-risk-bump').value==='0.07'`);
 await evaluate(change);await wait("!document.querySelector('#risk-page-prompt').hidden");await click('#risk-page-prompt button:not(.secondary)');await wait(idle);
 await check('discarding Risk setup edits retains the explicitly chosen intersection',`document.querySelector('#risk-workspace-books').selectedOptions[0]?.value===${JSON.stringify(book)}&&document.querySelector('#risk-workspace-portfolios').selectedOptions[0]?.value===${JSON.stringify(portfolio)}&&document.querySelector('#risk-workspace-books-picker .scope-value').textContent===${JSON.stringify(name)}`);
 await click('#nav-futures');await wait(idle);await click('#blotter-dock [aria-label="Maximize Summary"]');
 await persona('trader');await wait(idle);
 await check('another account starts with its own scope and unmaximized panes',"!document.querySelector('#blotter-dock').hasAttribute('data-maximized')&&document.querySelector('#blotter-books').selectedOptions.length===0&&document.querySelector('#blotter-portfolios').selectedOptions.length===0");
}
