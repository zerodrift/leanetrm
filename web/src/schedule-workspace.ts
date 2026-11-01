import {setupScheduleCalendar} from './schedule-calendar.js';
type View='exchange'|'contracts'|'generators'|'templates';
/** A single desk retains the existing editors and their unsaved state while switching views. */
export function setupScheduleWorkspace(){
 const get=(id:string)=>document.getElementById(id)!;
 const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
 const host=get('schedules-page'),templates=get('curve-date-editor'),generators=el('div');
 templates.classList.remove('card');templates.hidden=false;
 generators.append(...Array.from(host.children));
 const intro=el('div');intro.className='schedule-workspace-intro';intro.append(el('p','DATES'),el('h2','Schedules & date mappings'),el('p','Upload or generate dates, then reference them by name in date mappings.'));
 const path=el('div');path.className='schedule-path';for(const [index,text] of ['Saved dates','Symbolic mappings','Contracts & curves'].entries()){const step=el('span');step.append(el('b',String(index+1)),el('span',text));path.append(step);}intro.append(path);
 const tabs=el('div');tabs.className='schedule-tabs';tabs.setAttribute('role','tablist');tabs.setAttribute('aria-label','Schedules and date mappings');
 const content=el('div');content.className='schedule-workspace-content';const editor=el('div');editor.className='schedule-workspace-editor';const aside=el('aside');aside.setAttribute('aria-label','Date explorer');
 const panels:{view:View;label:string;panel:HTMLElement}[]=[{view:'exchange',label:'Schedules',panel:generators},{view:'contracts',label:'Date mappings',panel:templates}];
 let active:View='exchange';const buttons=new Map<View,HTMLButtonElement>();
 function show(view:View=active){if(view==='generators')view='exchange';if(view==='templates')view='contracts';active=view;for(const p of panels){p.panel.hidden=p.view!==view;const button=buttons.get(p.view)!;button.setAttribute('aria-selected',String(p.view===view));button.tabIndex=p.view===view?0:-1;}}
 for(const p of panels){const button=el('button',p.label);button.type='button';button.id='schedule-tab-'+p.view;button.setAttribute('role','tab');button.setAttribute('aria-controls',p.panel.id||('schedule-panel-'+p.view));p.panel.id||=('schedule-panel-'+p.view);p.panel.setAttribute('role','tabpanel');p.panel.setAttribute('aria-labelledby',button.id);button.onclick=()=>show(p.view);buttons.set(p.view,button);tabs.append(button);editor.append(p.panel);
  button.onkeydown=event=>{const offset=event.key==='ArrowRight'?1:event.key==='ArrowLeft'?-1:0;if(!offset)return;event.preventDefault();const index=panels.findIndex(x=>x.view===active);const view=panels[(index+offset+panels.length)%panels.length].view;show(view);buttons.get(view)!.focus();};}
 content.append(editor,aside);host.replaceChildren(intro,tabs,content);const calendar=setupScheduleCalendar(aside);show();
 return {show,calendar,reset(){calendar.reset();show('exchange');}};
}
