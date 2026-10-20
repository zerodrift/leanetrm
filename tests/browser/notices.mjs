// A saved-change notice describes one action; the next action clears it.
export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,url}){
  await command('Runtime.enable');await command('Page.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
  await persona('editor');
  const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>w.initialFrameIds.includes(f.id)&&f.at.startsWith('2020-12-15')).id;})()");
  await set('#snapshot',frame);await wait(idle);
  await evaluate("[...document.querySelectorAll('.curve-button')].find(b=>b.textContent.startsWith('Gas futures')).click()");await wait(idle);
  await click('[data-view=inputs]');await click('#inputs tbody tr:first-child button');
  // Earlier checks may already have corrected this price; corrections apply to its latest version.
  await evaluate("[...document.querySelectorAll('#revisions tbody tr')].at(-1).querySelector('button')?.click()");await wait(idle);
  await set('#correction-value',await evaluate("[...document.querySelectorAll('#revisions tbody tr')].at(-1).cells[1].textContent.trim()==='2.5'?'2.6':'2.5'"));await submit('#correction-form');
  await check('saving a correction confirms it with a notice',"!document.querySelector('#notice').hidden&&document.querySelector('#notice').textContent.includes('Your change was saved')","[document.querySelector('#error').textContent,document.querySelector('#snapshot').selectedOptions[0]?.textContent,document.querySelector('#revision-card').hidden]");
  await click('#nav-schedules');
  await check('the next action clears the earlier saved-change notice',"document.querySelector('#notice').hidden");
  await click('#nav-market');await persona('trader');
  await check('another demo user never sees the earlier saved-change notice',"document.querySelector('#notice').hidden&&document.querySelector('#profile-label').textContent==='Trader'");
}
