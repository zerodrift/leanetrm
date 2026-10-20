// Presentation helpers for recorded walkthroughs: a caption bar and visible
// cursor drawn in the page's top layer, real input events, and screencast capture.
import {spawn} from 'node:child_process';
import {mkdir,rm,writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {setTimeout as delay} from 'node:timers/promises';

const size={width:1920,height:1080};
const idle="document.body?.getAttribute('aria-busy')==='false'";

// The overlay is a manual popover so it can be raised above modal dialogs.
const overlay=`(()=>{if(window.__walkthrough)return;
const layer=document.createElement('div');layer.id='walkthrough-layer';layer.setAttribute('popover','manual');
layer.innerHTML='<div class="wt-title"><small></small><strong></strong></div><div class="wt-ring"></div><div class="wt-caption"><span class="wt-step"></span><span class="wt-text"></span></div><svg class="wt-cursor" viewBox="0 0 24 24" width="30" height="30"><path d="M3 2l17 10.5-7.6 1.3 4.4 8-3 1.6-4.4-8L3 21z" fill="#111" stroke="#fff" stroke-width="1.6" stroke-linejoin="round"/></svg>';
// A constructed stylesheet leaves the page's style-src policy untouched.
const sheet=new CSSStyleSheet();sheet.replaceSync(\`
body::after,dialog[open]::after{content:'';display:block;height:180px}
#walkthrough-layer{position:fixed;inset:0;width:100vw;height:100vh;max-width:none;max-height:none;margin:0;padding:0;border:0;background:transparent;overflow:visible;pointer-events:none;font-family:Inter,system-ui,sans-serif}
#walkthrough-layer .wt-caption{position:absolute;left:0;right:0;bottom:40px;width:max-content;max-width:1840px;margin-inline:auto;white-space:nowrap;display:flex;gap:18px;align-items:center;padding:18px 30px;border-radius:14px;background:rgba(14,32,26,.92);color:#fff;font-size:28px;line-height:1.3;box-shadow:0 12px 40px rgba(0,0,0,.3);transition:opacity .25s}
#walkthrough-layer .wt-caption.off{opacity:0}
#walkthrough-layer .wt-step{flex:none;padding:4px 12px;border-radius:999px;background:#3f9f7a;font-size:20px;font-weight:600}
#walkthrough-layer .wt-title{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:16px;background:rgba(14,32,26,.94);color:#fff;transition:opacity .5s}
#walkthrough-layer .wt-title small{font-size:26px;letter-spacing:.12em;text-transform:uppercase;color:#9fd6bf}
#walkthrough-layer .wt-title strong{font-size:58px;font-weight:600;max-width:1500px;text-align:center}
#walkthrough-layer .wt-title.off{opacity:0}
#walkthrough-layer .wt-cursor{position:absolute;left:0;top:0;transform:translate(960px,600px);transition-property:transform;transition-timing-function:cubic-bezier(.4,0,.2,1);filter:drop-shadow(0 2px 3px rgba(0,0,0,.35))}
#walkthrough-layer .wt-ring{position:absolute;border:3px solid #f2a541;border-radius:10px;box-shadow:0 0 0 6px rgba(242,165,65,.25);opacity:0;transition:opacity .2s}
#walkthrough-layer .wt-ring.on{opacity:1}\`);
document.adoptedStyleSheets=[...document.adoptedStyleSheets,sheet];document.body.append(layer);layer.showPopover();
const part=selector=>layer.querySelector(selector);
window.__walkthrough={
  raise(){layer.hidePopover();layer.showPopover();},
  title(kicker,text){part('.wt-title small').textContent=kicker;part('.wt-title strong').textContent=text;part('.wt-title').classList.remove('off');},
  untitle(){part('.wt-title').classList.add('off');},
  caption(step,text){const c=part('.wt-caption');c.classList.toggle('off',!text);part('.wt-step').textContent=step;part('.wt-text').textContent=text;},
  move(x,y,ms){const c=part('.wt-cursor');c.style.transitionDuration=ms+'ms';c.style.transform='translate('+x+'px,'+y+'px)';},
  ring(r){const n=part('.wt-ring');if(!r){n.classList.remove('on');return;}Object.assign(n.style,{left:r.left-6+'px',top:r.top-6+'px',width:r.width+12+'px',height:r.height+12+'px'});n.classList.add('on');}
};})()`;

// Visible elements matching a selector, narrowed to the innermost ones containing some text.
const find=(selector,text)=>`(()=>{const m=[...document.querySelectorAll(${JSON.stringify(selector)})].filter(n=>n.getClientRects().length&&n.textContent.includes(${JSON.stringify(text??'')}));return m.filter(n=>!m.some(o=>o!==n&&n.contains(o)));})()`;

// Returns the one matching element's viewport rectangle after scrolling it into view.
const locate=(selector,text,block)=>`(async()=>{const matches=${find(selector,text)};
if(matches.length!==1)return {count:matches.length};const node=matches[0];
const before=node.getBoundingClientRect();const visible=before.top>=90&&before.bottom<=innerHeight-150&&before.left>=0&&before.right<=innerWidth;
if(!visible||${JSON.stringify(block??null)}!==null){node.scrollIntoView({behavior:'smooth',block:${JSON.stringify(block??'center')}});await new Promise(r=>setTimeout(r,700));}
const r=node.getBoundingClientRect();return {count:1,left:r.left,top:r.top,width:r.width,height:r.height};})()`;

export function stage({command,on,evaluate,url}){
  let frames=[],writes=[],cues=[],started=0,folder,cursor={x:960,y:600};
  on('Page.screencastFrame',({data,sessionId})=>{
    void command('Page.screencastFrameAck',{sessionId}).catch(()=>{});
    if(!folder)return;
    const file=join(folder,`f${String(frames.length).padStart(6,'0')}.jpg`);
    frames.push({file,at:performance.now()});writes.push(writeFile(file,Buffer.from(data,'base64')));
  });
  async function until(expression,timeout=15000){
    for(const end=performance.now()+timeout;performance.now()<end;){if(await evaluate(expression))return;await delay(60);}
    throw Error('Walkthrough timed out waiting for: '+expression);
  }
  async function target(selector,text,block){
    const box=await evaluate(locate(selector,text,block));
    if(box.count!==1)throw Error(`Expected one visible ${selector}${text?` containing "${text}"`:''}; found ${box.count}`);
    return box;
  }
  async function glide(box,ms=550){
    const x=Math.round(box.left+Math.min(box.width/2,box.width-8)),y=Math.round(box.top+box.height/2);
    const distance=Math.hypot(x-cursor.x,y-cursor.y),duration=Math.round(Math.min(ms,150+distance*.5));
    await evaluate(`__walkthrough.raise();__walkthrough.move(${x},${y},${duration})`);await delay(duration+80);cursor={x,y};return {x,y};
  }
  async function press({x,y}){
    for(const type of ['mouseMoved','mousePressed','mouseReleased'])await command('Input.dispatchMouseEvent',{type,x,y,button:'left',buttons:type==='mousePressed'?1:0,clickCount:1});
  }
  return {
    pause:ms=>delay(ms),until,
    async open(){
      await command('Page.enable');await command('Runtime.enable');
      await command('Emulation.setDeviceMetricsOverride',{...size,deviceScaleFactor:1,mobile:false});
      await command('Page.addScriptToEvaluateOnNewDocument',{source:`addEventListener('DOMContentLoaded',()=>${overlay})`});
      await command('Page.navigate',{url:url+'/'});await until(`document.querySelectorAll('.curve-button').length>0&&${idle}&&!!window.__walkthrough`,30000);
    },
    async start(directory,kicker,title){
      folder=directory;await rm(folder,{recursive:true,force:true});await mkdir(folder,{recursive:true});frames=[];writes=[];cues=[];
      await evaluate(`__walkthrough.raise();__walkthrough.caption('','');__walkthrough.title(${JSON.stringify(kicker)},${JSON.stringify(title)})`);
      await command('Page.startScreencast',{format:'jpeg',quality:88,maxWidth:size.width,maxHeight:size.height,everyNthFrame:1});
      started=performance.now();cues.push({at:0,text:title});await delay(3000);
      await evaluate('__walkthrough.untitle()');await delay(600);
    },
    async caption(step,text){
      cues.push({at:performance.now()-started,text});
      // Reading time grows with the caption so a voice-over can keep pace.
      await evaluate(`__walkthrough.raise();__walkthrough.caption(${JSON.stringify(step)},${JSON.stringify(text)})`);await delay(1000+30*text.length);
    },
    async click(selector,text,{block}={}){await press(await glide(await target(selector,text,block)));await delay(250);await until(idle);await evaluate('__walkthrough.raise()');},
    async point(selector,text,{block,hold=1600}={}){
      const box=await target(selector,text,block);await glide(box);
      await evaluate(`__walkthrough.ring(${JSON.stringify(box)})`);await delay(hold);await evaluate('__walkthrough.ring(null)');
    },
    async scroll(selector,text,block='start'){await target(selector,text,block);await delay(300);},
    // Replaces a text field's value with real keystrokes.
    async type(selector,value){
      await press(await glide(await target(selector)));
      await evaluate(`document.querySelector(${JSON.stringify(selector)}).select()`);
      for(const character of value){await command('Input.insertText',{text:character});await delay(65);}
      await evaluate(`document.querySelector(${JSON.stringify(selector)}).dispatchEvent(new Event('change',{bubbles:true}))`);await delay(300);await until(idle);
    },
    // Date and time inputs take their value directly, shown with the same highlight.
    async fill(selector,value){
      const box=await target(selector);await glide(box);await evaluate(`__walkthrough.ring(${JSON.stringify(box)})`);
      await evaluate(`(()=>{const n=document.querySelector(${JSON.stringify(selector)});n.value=${JSON.stringify(value)};n.dispatchEvent(new Event('input',{bubbles:true}));n.dispatchEvent(new Event('change',{bubbles:true}));})()`);
      await delay(380);await evaluate('__walkthrough.ring(null)');await until(idle);
    },
    read:expression=>evaluate(expression),
    // Native select popups are not painted in headless capture, so the choice is
    // shown with a highlight around the control instead.
    async choose(selector,label){
      const box=await target(selector);await glide(box);await evaluate(`__walkthrough.ring(${JSON.stringify(box)})`);
      const chosen=await evaluate(`(()=>{const s=document.querySelector(${JSON.stringify(selector)});const o=[...s.options].filter(o=>o.value&&o.textContent.includes(${JSON.stringify(label)}));if(o.length!==1)return o.length;s.value=o[0].value;s.dispatchEvent(new Event('input',{bubbles:true}));s.dispatchEvent(new Event('change',{bubbles:true}));return 1;})()`);
      if(chosen!==1)throw Error(`Expected one "${label}" option in ${selector}; found ${chosen}`);
      await delay(380);await evaluate('__walkthrough.ring(null)');await until(idle);
    },
    visible:selector=>evaluate(`[...document.querySelectorAll(${JSON.stringify(selector)})].some(n=>n.getClientRects().length)`),
    // Names the innermost visible element containing some text, for later scoped selectors.
    async mark(selector,text,name){
      const count=await evaluate(`(()=>{document.querySelectorAll('[data-walkthrough="${name}"]').forEach(n=>delete n.dataset.walkthrough);const m=${find(selector,text)};if(m.length===1)m[0].dataset.walkthrough=${JSON.stringify(name)};return m.length;})()`);
      if(count!==1)throw Error(`Expected one visible ${selector} containing "${text}"; found ${count}`);
      return `[data-walkthrough="${name}"]`;
    },
    async expect(label,expression){if(!await evaluate(expression))throw Error('Walkthrough no longer shows: '+label);},
    async finish(output,{hold=2500}={}){
      cues.push({at:performance.now()-started,text:''});
      await evaluate(`__walkthrough.caption('','')`);await delay(hold);
      await command('Page.stopScreencast');const ended=performance.now();await delay(200);folder=undefined;await Promise.all(writes);
      const list=frames.map((frame,i)=>`file '${frame.file}'\nduration ${(((frames[i+1]?.at??ended)-frame.at)/1000).toFixed(4)}`);
      // The concat demuxer needs the last frame repeated to show its hold; -t trims the repeat.
      await writeFile(join(frames[0].file,'..','frames.txt'),`ffconcat version 1.0\n${list.join('\n')}\nfile '${frames.at(-1).file}'\n`);
      const length=(ended-frames[0].at)/1000;
      await ffmpeg(['-f','concat','-safe','0','-i',join(frames[0].file,'..','frames.txt'),'-vf',`fps=30,scale=${size.width}:${size.height},format=yuv420p`,'-c:v','libx264','-preset','medium','-crf','20','-movflags','+faststart','-t',length.toFixed(3),output+'.mp4']);
      await writeFile(output+'.vtt','WEBVTT\n\n'+cues.slice(0,-1).map((cue,i)=>`${clock(cue.at)} --> ${clock(cues[i+1].at)}\n${cue.text}`).join('\n\n')+'\n');
      await rm(join(frames[0].file,'..'),{recursive:true,force:true});
      return {seconds:length,cues:cues.slice(0,-1).map(c=>({at:c.at/1000,text:c.text}))};
    }
  };
}

export async function gif(video,output){
  await ffmpeg(['-i',video,'-vf','fps=10,scale=1280:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle',output]);
}

function clock(ms){const t=Math.max(0,Math.round(ms));return [Math.floor(t/3600000),Math.floor(t/60000)%60,Math.floor(t/1000)%60].map(v=>String(v).padStart(2,'0')).join(':')+'.'+String(t%1000).padStart(3,'0');}

function ffmpeg(args){
  return new Promise((accept,reject)=>{
    const child=spawn('ffmpeg',['-y','-loglevel','error',...args],{stdio:['ignore','ignore','pipe']});let error='';
    child.stderr.on('data',chunk=>error+=chunk);child.on('error',cause=>reject(Error('Recording needs ffmpeg on PATH.',{cause})));
    child.on('exit',code=>code===0?accept():reject(Error('ffmpeg failed: '+error)));
  });
}
