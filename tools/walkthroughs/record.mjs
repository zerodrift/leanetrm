// Records the scripted walkthroughs against a newly owned synthetic preview.
// Invoke through: tools/run_preview.py --record-walkthroughs --browser /path/to/chromium
import {mkdir,writeFile} from 'node:fs/promises';
import {join,resolve} from 'node:path';
import {withOwnedBrowser} from '../../tests/browser/owned-browser.mjs';
import {stage,gif} from './stage.mjs';
import {clips} from './clips.mjs';

const [target,browser,output,only]=process.argv.slice(2);
if(!target||!browser||!output)throw Error('Usage: record.mjs <owned-target.json> <browser> <output-directory> [clip-name,...]');
const selected=only?clips.filter(clip=>only.split(',').includes(clip.name)):clips;
if(!selected.length)throw Error('No walkthrough matches '+only);
await mkdir(output,{recursive:true});
const summary=[];
for(const [index,clip] of clips.entries()){
  if(!selected.includes(clip))continue;
  // Each clip starts in a new browser profile, so its demo user and choices are explicit.
  await withOwnedBrowser(target,browser,async context=>{
    const s=stage(context);await s.open();
    await s.start(join(context.root,'frames-'+clip.name),`Walkthrough ${index+1}`,clip.title);
    // A failed step leaves a screenshot of what the script saw.
    try{await clip.run(s);}
    catch(error){const shot=await context.command('Page.captureScreenshot',{format:'png'});await writeFile(join(output,clip.name+'-failure.png'),Buffer.from(shot.data,'base64'));throw error;}
    const result=await s.finish(join(resolve(output),clip.name));
    if(clip.gif)await gif(join(output,clip.name+'.mp4'),join(output,clip.name+'.gif'));
    summary.push({...clip,...result});
    console.log(`${clip.name}: ${result.seconds.toFixed(1)} s${result.seconds<60||result.seconds>90?' · outside the 60–90 s target':''}`);
  });
}
const time=seconds=>`${Math.floor(seconds/60)}:${String(Math.floor(seconds%60)).padStart(2,'0')}`;
await writeFile(join(output,'captions.md'),summary.map(clip=>`## ${clip.name} · ${clip.title} (${time(clip.seconds)})\n\n`+
  clip.cues.slice(1).map(cue=>`- ${time(cue.at)} ${cue.text}`).join('\n')).join('\n\n')+'\n');
console.log(`Recorded ${summary.length} walkthroughs in ${resolve(output)}.`);
