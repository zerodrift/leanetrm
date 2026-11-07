export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url}){
 await command('Page.navigate',{url:url+'/'});await wait(idle);await persona('risk');await click('#nav-market');
 await evaluate("[...document.querySelectorAll('.curve-button')].find(button=>button.querySelector('span').textContent==='Gas futures').click()");await wait(idle);
 await check('a curve is read before leaving it for another context',"document.querySelector('#contracts tbody tr')&&document.querySelector('#error').hidden");
 await click('#nav-dashboard');
 await evaluate("document.querySelector('#dashboard-add details').open=true");
 await set('#dashboard-add select[aria-label="Widget kind"]','latest_levels');
 await evaluate("for(const box of document.querySelectorAll('#dashboard-add .dashboard-add-curve input'))box.checked=box.value==='future'");
 await submit('#dashboard-add form');
 await evaluate("document.querySelector('#dashboard-widgets').scrollIntoView({block:'center'})");
 await wait("document.querySelector('.level-tile .kpi-figure')");
 await evaluate(`window.contextOriginalFetch=window.fetch;window.fetch=async(input,init)=>{
  if(String(input)==='/api/market/context'&&window.refuseContextApply)return new Response(JSON.stringify({code:'preview_error',message:'Synthetic context read failed.'}),{status:503,headers:{'Content-Type':'application/json'}});
  return window.contextOriginalFetch(input,init);
 };window.refuseContextApply=true;`);
 await click('#knowledge-context>summary');await set('#date-context-view','2020-12-17T12:00:00');
 await check('an unapplied knowledge edit replaces prior widget values with Apply guidance',"!document.querySelector('.level-tile .kpi-figure')&&document.querySelector('#dashboard-widgets').textContent.includes('Apply the changed context')&&!document.querySelector('#dashboard-widgets').textContent.includes('market calendar is not loaded')");
 await click('#apply-view-context');
 await check('a failed Apply keeps explicit recovery controls and does not blame the calendar',"!document.querySelector('#error').hidden&&document.querySelector('#apply-view-context').checkVisibility()&&!document.querySelector('#current-view-context').disabled&&document.querySelector('#dashboard-widgets').textContent.includes('Apply the changed context')&&!document.querySelector('#dashboard-widgets').textContent.includes('Reload the dashboard')");
 await evaluate("window.refuseContextApply=false;window.fetch=window.contextOriginalFetch");
 await click('#current-view-context');await wait("document.querySelector('.level-tile .kpi-figure')");
 await check('Working data recovers the widget and the close badge after a failed Apply',"document.querySelector('#error').hidden&&document.querySelector('#knowledge-summary').textContent==='Current knowledge'&&document.querySelector('.level-tiles-footer').textContent.includes('Latest')");
 await evaluate('window.scrollTo(0,0)');await shot('context-header-desktop');
 await click('#context-help>summary');await check('expanded context explanations fit the desktop header',fitsWidth);await shot('context-header-help');await click('#context-help>summary');
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await check('grouped context and profile fit a phone without horizontal page overflow',fitsWidth);
 await shot('context-header-mobile');await click('#context-help>summary');await check('context explanations remain reachable on a phone',fitsWidth+"&&document.querySelector('#knowledge-context-help').checkVisibility()");
 await shot('context-header-mobile-help');await click('#context-help>summary');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await click('#knowledge-context>summary');await click('#date-context-latest');await click('#apply-view-context');
 await wait("document.querySelector('.level-tiles-footer')||document.querySelector('.dashboard-refusal')");
 await check('a genuine historical context reads the Dashboard after the injected failure',"document.querySelector('#error').hidden&&document.querySelector('#knowledge-summary').textContent==='Historical · read only'&&document.querySelector('.level-tiles-footer')?.textContent.includes('Historical')","document.querySelector('#dashboard-widgets').textContent");
 for(const page of ['market','risk','physical-operations','jobs']){
  await click('#nav-'+page);
  await check(page+' retains the applied knowledge context',"document.querySelector('#error').hidden&&document.querySelector('#knowledge-summary').textContent==='Historical · read only'");
  if(page==='market')await check('returning to a selected curve reads it at the changed context',"!!document.querySelector('#contracts tbody tr')&&document.querySelector('#error').hidden");
 }
 await click('#current-view-context');await click('#nav-dashboard');
 await check('the shared header returns to current knowledge across pages',"document.querySelector('#error').hidden&&document.querySelector('#knowledge-summary').textContent==='Current knowledge'");
}
