// Owned synthetic browser fixture only. Financial assertions compare retained backend values.
const canonical=value=>JSON.stringify(value,(_key,v)=>v&&typeof v==='object'&&!Array.isArray(v)?Object.fromEntries(Object.entries(v).sort(([a],[b])=>a.localeCompare(b))):v);
export async function runJointChecks({command,evaluate,wait,click,set,check,shot,idle,url,frame}){
 await click('#nav-risk');await wait(idle);await click('#risk-jobs-new');await wait("!document.querySelector('#workspace-risk-fields').disabled&&"+idle);
 const ids=await evaluate("(()=>{const c=window.__workspaceRiskReads.findLast(r=>r.path==='/api/portfolio/risk/catalog').data,a=c.factors[0],b=c.factors.find(f=>f.seriesId!==a.seriesId);return [a.id,b.id];})()");
 await click('#workspace-risk-form input[value=scenarios]');await set('#workspace-risk-joint-name','Synthetic named set');await set('#workspace-risk-joint-version','1');
 async function shift(id,size){await set('#workspace-risk-joint-factor',id);await set('#workspace-risk-joint-shift',size);await click('#workspace-risk-joint-add-shift');await wait(idle);}
 await shift(ids[0],'0.05');await shift(ids[1],'-0.03');await set('#workspace-risk-joint-label','Joint curve move');await click('#workspace-risk-joint-add-scenario');await wait(idle);
 await shift(ids[0],'-0.05');await set('#workspace-risk-joint-label','Independent reverse move');await click('#workspace-risk-joint-add-scenario');await wait(idle);
 await check('Risk builds named joint cases from explicit targets and signed shifts without prefilled financial sizes',
  "document.querySelectorAll('#workspace-risk-joint .joint-risk-list li').length===2&&document.querySelector('#workspace-risk-joint-shift').value===''&&document.querySelector('#workspace-risk-parent-context').textContent.includes('unchanged market')");
 await click('#workspace-risk-run');await wait("document.querySelector('#risk-workspace-results canvas')&&document.querySelector('#risk-workspace-results h3')?.textContent==='Joint scenario report'&&"+idle,60000);
 const report=await evaluate("window.__workspaceRiskReads.findLast(r=>r.path==='/api/portfolio/risk').data");
 await check('named reports show the backend joint loss, exact accessible tables and both complete cases',
  `(()=>{const r=${JSON.stringify(report)},root=document.querySelector('#risk-workspace-results');return r.joint.samples.length===2&&r.joint.request.scenarios.scenarios[0].shifts.length===2&&root.textContent.includes('2 named scenarios')&&!!root.querySelector('[data-exact]')&&r.joint.samples[0].totals.every(t=>root.textContent.includes(t.values.display.loss));})()`);
 await set('#risk-workspace-results select[aria-label="Joint risk graphic"]','heatmap');
 await check('joint scenarios have a graphical heatmap and trade/instrument binding drill-through',
  "!!document.querySelector('#risk-workspace-results [data-chart-kind=heatmap] canvas')&&document.querySelector('#risk-workspace-results .risk-factor-detail').textContent.includes('Trade and instrument bindings')");
 await evaluate("document.querySelector('#risk-workspace-results').scrollIntoView({block:'start'});true");await shot('risk-joint-scenarios');
 await evaluate('window.__hideWorkspaceHistory=false;true');await click('#workspace-risk-queue');await wait(idle);
 const scenarioJob=await evaluate("window.__workspaceRiskReads.findLast(r=>r.path==='/api/portfolio/risk/jobs'&&r.data?.id).data.id");
 await wait(`window.__workspaceRiskReads.some(r=>r.data?.id===${JSON.stringify(scenarioJob)}&&r.data?.state==='completed'&&r.data?.result?.joint)&&${idle}`,60000);
 await evaluate(`location.hash=${JSON.stringify('#risk/job/'+scenarioJob)};true`);
 await wait("document.querySelector('#risk-workspace-results canvas')&&document.querySelector('#risk-workspace-context-note').textContent.startsWith('Retained report:')&&"+idle,60000);
 const before=await evaluate('performance.timeOrigin');await command('Page.reload');
 try{await wait(`performance.timeOrigin!==${before}&&document.querySelector('#risk-workspace-results canvas')&&${idle}`,60000);}
 catch(error){console.error('JOINT_RELOAD_STATE',JSON.stringify(await evaluate(`({origin:performance.timeOrigin,before:${before},hash:location.hash,busy:document.body.getAttribute('aria-busy'),error:document.querySelector('#error')?.textContent,note:document.querySelector('#risk-workspace-note')?.textContent,result:document.querySelector('#risk-workspace-results')?.textContent,reads:window.__workspaceRiskReads?.map(r=>({path:r.path,kind:r.data?.kind,id:r.data?.id,state:r.data?.state}))})`)));await shot('risk-joint-reload-failure');throw error;}
 await check('a retained named-scenario link reloads its captured complete cases without requiring a header date',
  `(()=>{const j=window.__workspaceRiskReads.findLast(r=>r.data?.id===${JSON.stringify(scenarioJob)}&&r.data?.result).data;return j.kind==='scenarios'&&j.scenarioCount===2&&(${canonical.toString()})(j.result.joint.samples)===${JSON.stringify(canonical(report.joint.samples))}&&document.querySelector('#risk-workspace-results').textContent.includes('Joint scenario report');})()`);
 await set('#snapshot',frame);await wait(idle);await click('#risk-jobs-new');await wait("!document.querySelector('#workspace-risk-fields').disabled&&"+idle);
 await click('#workspace-risk-form input[value=derivatives]');
 for(const kind of ['delta','gamma','mixed'])for(const part of ['absolute','relative'])await set('#workspace-risk-joint-'+kind+'-'+part,'0');
 for(const id of ids){await set('#workspace-risk-joint-factor',id);await set('#workspace-risk-joint-bump','0.05');await click('#workspace-risk-joint-add-axis');await wait(idle);}
 const axes=await evaluate("[...document.querySelector('#workspace-risk-joint-first').options].filter(o=>o.value).map(o=>o.value)");
 await set('#workspace-risk-joint-first',axes[0]);await set('#workspace-risk-joint-second',axes[1]);await click('#workspace-risk-joint-add-pair');await wait(idle);
 await click('#workspace-risk-run');await wait("document.querySelector('#risk-workspace-results h3')?.textContent==='Selected Greeks and cross-gamma'&&document.querySelector('#risk-workspace-results canvas')&&"+idle,60000);
 const greek=await evaluate("window.__workspaceRiskReads.findLast(r=>r.path==='/api/portfolio/risk').data");
 await check('selected Greek reports retain the requested pair and server half/nominal/double estimates and agreement',
  `(()=>{const r=${JSON.stringify(greek)},root=document.querySelector('#risk-workspace-results');return r.joint.derivatives.length===5&&r.joint.request.derivatives.mixed.length===1&&root.textContent.includes('Half step')&&root.textContent.includes('Double step')&&root.textContent.includes('Step agreement')&&r.joint.derivatives[0].totals.every(t=>root.textContent.includes(t.values.display.nominal));})()`);
 await click('#risk-workspace-results [data-joint-metric=mixedGamma]');await set('#risk-workspace-results select[aria-label="Joint risk graphic"]','heatmap');
 await check('cross-gamma has its own native-unit graphic, exact table and trade contribution detail',
  "!!document.querySelector('#risk-workspace-results [data-chart-kind=heatmap] canvas')&&document.querySelector('#risk-workspace-results').textContent.includes('Cross-gamma')&&document.querySelector('#risk-workspace-results .risk-factor-detail').textContent.includes('Trade and instrument bindings')");
 await evaluate("document.querySelector('#risk-workspace-results').scrollIntoView({block:'start'});true");await shot('risk-joint-cross-gamma');
 await click('#workspace-risk-queue');await wait(idle);const greekJob=await evaluate("window.__workspaceRiskReads.findLast(r=>r.path==='/api/portfolio/risk/jobs'&&r.data?.id).data.id");
 await wait(`window.__workspaceRiskReads.some(r=>r.data?.id===${JSON.stringify(greekJob)}&&r.data?.state==='completed'&&r.data?.result?.joint)&&${idle}`,60000);
 await check('queued selected derivatives reproduce the same backend financial estimates and typed job count',
  `(()=>{const j=window.__workspaceRiskReads.findLast(r=>r.data?.id===${JSON.stringify(greekJob)}&&r.data?.result).data;return j.kind==='derivatives'&&j.derivativeCount===5&&(${canonical.toString()})(j.result.joint.derivatives)===${JSON.stringify(canonical(greek.joint.derivatives))};})()`);
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await evaluate("document.querySelector('#risk-workspace-results').scrollIntoView({block:'start'});true");await shot('risk-joint-phone');
 await check('joint reports and their editor fit a phone viewport',"document.documentElement.scrollWidth<=document.documentElement.clientWidth+1");
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1150,deviceScaleFactor:1,mobile:false});
}
