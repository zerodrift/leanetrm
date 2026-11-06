// D334: the header opens on the person's remembered close session, offers only that session's closes as closes and lists a
// day's replaced closes as history, never as a choice.
export async function run({command,evaluate,wait,persona,check,idle,url}){
 await command('Page.navigate',{url:url+'/'});await wait(idle,30000);
 await persona('viewer');await wait(idle,30000);
 await check('the header names the remembered close session',
  "(()=>{const s=document.querySelector('#close-session');return s&&s.value==='demo_close_1600'&&s.selectedOptions[0].textContent.includes('16:00')&&document.querySelector('#close-session-status').hidden;})()");
 const offered=await evaluate(`(async()=>{const w=await(await fetch('/api/workspace')).json();return {closes:w.frames.filter(f=>f.close?.session==='demo_close_1600').map(f=>f.id),working:w.frames.filter(f=>!f.close).map(f=>f.id)};})()`);
 await check('the As-of choice offers the session closes, and working snapshots only as working data',
  `(()=>{const options=[...document.querySelectorAll('#snapshot option')].filter(o=>o.value);const closes=${JSON.stringify(offered.closes)};
   return options.length>0&&options.every(o=>closes.includes(o.value)||o.textContent.startsWith('Working snapshot · '));})()`);
 await check('a close with no replaced history shows no history list',"document.querySelector('#close-history').hidden");
 // The header wraps below 1190px; the session control keeps its compact width there too.
 await command('Emulation.setDeviceMetricsOverride',{width:1000,height:900,deviceScaleFactor:1,mobile:false});await wait(idle);
 await check('the session control fits the header',"(()=>{const s=document.querySelector('#close-session');const r=s.getBoundingClientRect();return r.width>0&&r.right<=document.documentElement.clientWidth;})()",
  "(()=>{const r=document.querySelector('#close-session').getBoundingClientRect();return {left:r.left,right:r.right,width:r.width,viewport:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth};})()");
}
