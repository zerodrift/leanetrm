import {describe,test} from 'node:test';
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {once} from 'node:events';
import {mkdtemp,readFile,rm,stat,writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {delimiter,join} from 'node:path';
import {fileURLToPath} from 'node:url';

const syntheticBrowser=fileURLToPath(new URL('./fixtures/unresponsive-browser.mjs',import.meta.url));
// The failure is printed as a test report shows it, with the failures it combines.
const pageCall=`import {inspect} from 'node:util';
import {withBrowserPage} from ${JSON.stringify(new URL('./browser-page.mjs',import.meta.url).href)};
await withBrowserPage('<div id="host"></div>',async()=>{}).then(()=>console.log('resolved'),error=>console.log('rejected: '+inspect(error)));`;
const quoted=text=>`'${text.replaceAll("'","'\\''")}'`;
// Longer than the waits a failing call makes in turn: for a command's reply, then for a browser that ignores the request to stop.
const bound=45000;

// A page call runs in a process of its own, as a test file does, against a synthetic browser whose debugger fails in the named way.
// The process has to end by itself: a call that never settles, or leaves a browser, server or socket open, keeps it alive.
async function pageCallAgainst(t,failure,{failingProcessListing=false}={}){
  const directory=await mkdtemp(join(tmpdir(),'openetrm-page-failure-')),browser=join(directory,'browser'),record=join(directory,'record.json');
  const started=()=>readFile(record,'utf8').then(JSON.parse,()=>undefined);
  t.after(async()=>{
    // A failed check may leave the synthetic browser it started, and that browser's profile.
    const left=await started();
    if(left){
      try{process.kill(left.pid,'SIGKILL');}catch(error){if(error.code!=='ESRCH')throw error;}
      await rm(left.profile,{recursive:true,force:true});
    }
    await rm(directory,{recursive:true,force:true});
  });
  await writeFile(browser,`#!/bin/sh\nexec ${[process.execPath,syntheticBrowser,failure,record].map(quoted).join(' ')} "$@"\n`,{mode:0o755});
  // Stopping a browser lists its process group with `ps`. One that fails stands for a group that cannot be confirmed stopped.
  if(failingProcessListing)await writeFile(join(directory,'ps'),"#!/bin/sh\necho 'Synthetic process listing failure' >&2\nexit 1\n",{mode:0o755});
  const call=spawn(process.execPath,['--input-type=module','-e',pageCall],
    {env:{...process.env,CHROME_BIN:browser,PATH:directory+delimiter+process.env.PATH},stdio:['ignore','pipe','inherit']});
  let output='';call.stdout.setEncoding('utf8').on('data',text=>{output+=text;});
  const overdue=setTimeout(()=>call.kill('SIGKILL'),bound);
  const [code,signal]=await once(call,'close');clearTimeout(overdue);
  return {ended:{code,signal},output,browser:await started()};
}

describe('a failing page call ends its process by itself',{concurrency:true},()=>{
  for(const [failure,name,reason] of [
    ['never-opens','never completes the debugger connection',/Browser connection timed out/],
    ['never-answers','stops answering debugger commands',/Browser command timed out: Page\.enable/],
    ['closes','closes the debugger connection under a waiting command',/Browser connection closed/],
  ])test(`with its reason and no browser or profile left, when the browser ${name}`,{timeout:60000},async t=>{
    const {ended,output,browser}=await pageCallAgainst(t,failure);
    assert.deepEqual(ended,{code:0,signal:null},'the page call must end by itself within its bounded waits');
    assert.match(output,reason);
    assert.throws(()=>process.kill(browser.pid,0),{code:'ESRCH'},'the browser must be stopped');
    await assert.rejects(stat(browser.profile),{code:'ENOENT'},'its profile must be removed');
  });

  test('with its own failure still reported when stopping its browser fails too',{timeout:60000},async t=>{
    const {ended,output,browser}=await pageCallAgainst(t,'closes',{failingProcessListing:true});
    assert.deepEqual(ended,{code:0,signal:null},'the page call must end by itself although its cleanup failed');
    assert.match(output,/Browser connection closed/,'the failure of the call itself must stay in the report');
    assert.match(output,/Synthetic process listing failure/,'beside the reason its browser could not be confirmed stopped');
    assert.throws(()=>process.kill(browser.pid,0),{code:'ESRCH'},'the browser must be stopped');
  });
});
