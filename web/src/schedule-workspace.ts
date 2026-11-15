import {setupScheduleCalendar} from './schedule-calendar.js';
export type ScheduleView='exchange'|'contracts';
/**
 * Date schedules and Date rules are entries of their own in the Reference Data menu and share this desk, which retains both editors and
 * their unsaved state while one shows.
 */
export function setupScheduleWorkspace(){
 const get=(id:string)=>document.getElementById(id)!;
 const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
 const host=get('schedules-page'),templates=get('curve-date-editor'),generators=el('div');
 templates.classList.remove('card');templates.hidden=false;
 generators.append(...Array.from(host.children));
 const intro=el('div');intro.className='schedule-workspace-intro';intro.append(el('p','Upload or generate dates, then reference them by name in date mappings.'));
 const path=el('div');path.className='schedule-path';for(const [index,text] of ['Saved dates','Symbolic mappings','Contracts & curves'].entries()){const step=el('span');step.append(el('b',String(index+1)),el('span',text));path.append(step);}intro.append(path);
 const content=el('div');content.className='schedule-workspace-content';const editor=el('div');editor.className='schedule-workspace-editor';const aside=el('aside');aside.setAttribute('aria-label','Date explorer');
 const panels:{view:ScheduleView;panel:HTMLElement}[]=[{view:'exchange',panel:generators},{view:'contracts',panel:templates}];
 let active:ScheduleView='exchange';
 function show(view:ScheduleView=active){active=view;for(const p of panels)p.panel.hidden=p.view!==view;}
 editor.append(generators,templates);content.append(editor,aside);host.replaceChildren(intro,content);const calendar=setupScheduleCalendar(aside);show();
 return {show,view:()=>active,calendar,reset(){calendar.reset();show('exchange');}};
}
