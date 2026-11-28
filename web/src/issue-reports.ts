import {PendingCommand,actorTransport,type RequestCommand} from './pending-command.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Mode='disabled'|'review_required';
type Revision={version:number;mode:Mode;dailyLimit:number|null};
type PolicyDirectory={repository:string|null;policy:{current:Revision|null;history:(Revision&{recordedAt:string})[]}};
export type Report={id:string;kind:string;title:string;repository:string;state:'pending'|'dispatching'|'submitted'|'failed'|'uncertain'|'abandoned';
 attempt:number;issueNumber:number|null;issueUrl:string|null;errorCode:string|null;decisions:string[]};
type Mine={repository:string|null;reports:{mode:Mode|null;submissions:Report[]}};
export type Choice='look'|'resend'|'abandon';

const make=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};

/** The complete policy as entered. Its bounds are the server's to enforce. */
export function policyRequest(mode:string,limit:string):{mode:Mode;dailyLimit:number|null}{
 if(mode==='disabled')return{mode,dailyLimit:null};
 if(mode!=='review_required')throw Error('Choose whether issue reporting is off or on.');
 if(!/^\d+$/.test(limit))throw Error('Supply the daily limit for each person as a whole number.');
 return{mode,dailyLimit:Number(limit)};
}

const labels:Record<Choice,string>={look:'Look for it in the tracker',resend:'Send it again',abandon:'Give it up'};
/** The decisions the server lists for a report, in its order, that this screen can carry out. */
export function offered(report:Report):Choice[]{
 return report.decisions.filter((decision):decision is Choice=>Object.hasOwn(labels,decision));
}

/** A refusal the server gives only after finding no retained request under this identity, so nothing was sent. */
export function neverRetained(code:string|undefined):boolean{
 return code==='issue_report_input'||code==='issue_report_limit'||code==='issue_reporting_disabled';
}

const reasons:Record<string,string>={tracker_refused:'the tracker refused it',tracker_rate_limited:'the tracker is limiting requests',
 tracker_unavailable:'the tracker could not be reached',tracker_authentication:'the tracker connection was not accepted',
 tracker_repository_changed:'the configured repository changed'};
export function reportStatus(report:Report):string{
 switch(report.state){
  case 'submitted':return `Issue #${report.issueNumber} created`;
  case 'failed':return `Not created: ${reasons[report.errorCode??'']??report.errorCode}`;
  case 'uncertain':return 'Not confirmed. The tracker may or may not have created an issue.';
  case 'dispatching':return 'Being sent';
  case 'abandoned':return 'Given up';
  default:return 'Not sent';
 }
}

/** Why the form is not offered, or null when it is. */
export function unavailable(mode:Mode|null,repository:string|null):string|null{
 if(repository===null)return 'This installation has no issue tracker connection.';
 return mode==='review_required'?null:'Issue reporting is not enabled for this workspace.';
}

/** The workspace's issue reporting policy, for an administrator. */
export function setupIssueReportPolicy(root:HTMLElement,o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;identity:()=>string;can:()=>boolean;busy:()=>boolean}){
 const path='settings/issue-reports';
 const pending=new PendingCommand(code=>['issue_report_policy_input','issue_report_policy_conflict','issue_report_policy_version_conflict'].includes(code??''));
 const transport=actorTransport<RequestCommand>(()=>`openetrm.issue-report-policy.${o.identity()}`,()=>sessionStorage);
 root.dataset.ownControls='';
 let generation=0;
 function sync(){
  root.querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button').forEach(control=>
   control.disabled=o.busy()||(control instanceof HTMLButtonElement&&control.type==='submit'&&!!pending.command)||control.dataset.off==='true');
 }
 async function load(){
  // A reload after a save keeps the card as the person left it, so the new version is in view.
  const version=++generation,owner=o.identity(),open=root.querySelector('details')?.open===true;root.replaceChildren();root.hidden=!o.can();if(root.hidden)return;
  const detail=make('details'),summary=make('summary','Issue reporting');detail.className='card';detail.open=open;detail.append(summary);root.append(detail);
  const directory=await o.api<PolicyDirectory>(path);if(version!==generation||owner!==o.identity())return;
  const current=directory.policy.current;
  const kept=transport.read();pending.command=kept?.path===path?kept:undefined;
  detail.append(make('p',directory.repository===null
   ?'This installation has no issue tracker connection, so nothing can be sent whatever is saved here.'
   :`People who may submit can send a report about the software to ${directory.repository}. That tracker is public. Each person reads their own text before it is sent.`));
  const form=make('form'),fields=make('div');fields.className='schedule-fields';
  const modeLabel=make('label','Issue reporting'),mode=make('select');mode.required=true;mode.dataset.issueReportField='mode';
  for(const [value,text] of [['','Choose…'],['disabled','Off'],['review_required','On: each person reviews and sends their own report']])mode.append(new Option(text,value));
  mode.value=current?.mode??'';modeLabel.append(mode);
  const limitLabel=make('label','Reports per person in 24 hours'),limit=make('input');limit.type='number';limit.step='1';limit.dataset.issueReportField='dailyLimit';
  limit.value=current?.dailyLimit==null?'':String(current.dailyLimit);limitLabel.append(limit);
  const follow=()=>{limit.dataset.off=String(mode.value!=='review_required');limit.required=mode.value==='review_required';if(mode.value!=='review_required')limit.value='';sync();};
  mode.onchange=follow;fields.append(modeLabel,limitLabel);
  const actions=make('div');actions.className='schedule-actions';const save=make('button','Save policy'),retry=make('button','Retry original request'),reload=make('button','Reload policy');
  save.type='submit';retry.type=reload.type='button';retry.hidden=!pending.command;reload.className=retry.className='secondary';
  const status=make('p',current?`Version ${current.version}`:'No policy has been saved, so issue reporting is off.');status.setAttribute('role','status');
  actions.append(save,retry,reload);form.append(fields,actions,status);detail.append(form);
  const history=make('details');history.append(make('summary','Recent policy revisions'));
  for(const revision of directory.policy.history)history.append(make('p',`Version ${revision.version} · ${revision.recordedAt} · ${revision.mode==='disabled'?'Off':`On, ${revision.dailyLimit} per person`}`));
  detail.append(history);follow();
  async function send(){
   if(version!==generation||owner!==o.identity())throw Error('The account changed. Reopen its settings.');
   const command=pending.command!;const uncertain=pending.sending();transport.write(pending.command);sync();
   try{
    await o.api(command.path,{...command.body,operationId:command.operationId});
    if(version!==generation||owner!==o.identity())return;
    pending.clear();transport.write(undefined);await load();
   }catch(error){
    if(version!==generation||owner!==o.identity())throw error;
    pending.failed((error as {code?:string}).code,uncertain);transport.write(pending.command);retry.hidden=!pending.command;sync();
    status.textContent=error instanceof Error?error.message:'The policy save could not be confirmed. Retry the original request.';throw error;
   }
  }
  form.onsubmit=event=>{event.preventDefault();void o.act(async()=>{pending.begin(path,{expectedVersion:current?.version??0,...policyRequest(mode.value,limit.value)});await send();});};
  retry.onclick=()=>void o.act(send);reload.onclick=()=>void o.act(load);
 }
 return{load,sync,reset(){generation++;pending.clear();root.replaceChildren();root.hidden=true;},unconfirmed:()=>!!pending.command};
}

/** Report an issue: a dialog any page can open, with the person's own reports and what each still needs. */
export function setupIssueReports(dialog:HTMLDialogElement,open:HTMLButtonElement,o:{api:Api;identity:()=>string;can:()=>boolean}){
 const path='issue-reports';
 const pending=new PendingCommand(neverRetained);
 const transport=actorTransport<RequestCommand>(()=>`openetrm.issue-report.${o.identity()}`,()=>sessionStorage);
 // `working` is one exchange with the server at a time. The draft is what the person has typed and not yet sent;
 // it survives every redraw of the dialog.
 let generation=0,working=false,draft={kind:'',title:'',body:''};
 const heading=make('div'),title=make('h2','Report an issue'),close=make('button','×');heading.className='dialog-heading';title.id='issue-report-title';
 close.type='button';close.className='icon-button';close.setAttribute('aria-label','Close');close.onclick=()=>dialog.close();
 const titles=make('div');titles.append(Object.assign(make('div','HELP'),{className:'eyebrow'}),title);heading.append(titles,close);
 // The outcome is shown directly under the heading, where it stays in view however long the form and list are.
 const body=make('div'),status=make('p');status.setAttribute('role','status');status.className='issue-report-status';
 dialog.replaceChildren(heading,status,body);
 const message=(error:unknown)=>error instanceof Error?error.message:'The request could not be confirmed.';

 async function load(){
  const version=++generation,owner=o.identity();if(!body.hasChildNodes())body.replaceChildren(make('p','Loading…'));
  const mine=await o.api<Mine>(path);if(version!==generation||owner!==o.identity())return;
  const kept=transport.read();pending.command=kept?.path===path?kept:undefined;
  const parts:HTMLElement[]=[];const reason=unavailable(mine.reports.mode,mine.repository);
  if(reason!==null)parts.push(make('p',reason));
  else{
   const form=make('form');
   const notice=make('p',`Your report is posted to ${mine.repository}, which anyone can read. Describe the problem with made-up examples. Leave out trades, counterparties, prices and account details.`);notice.className='field-help';
   const kindLabel=make('label','What kind of report is it?'),kind=make('select');kind.required=true;kind.name='kind';
   for(const [value,text] of [['','Choose…'],['bug','Something is wrong'],['request','Something is missing'],['question','A question']])kind.append(new Option(text,value));kindLabel.append(kind);
   const titleLabel=make('label','Title'),titleInput=make('input');titleInput.name='title';titleInput.required=true;titleLabel.append(titleInput);
   const textLabel=make('label','What happened, and what did you expect?'),text=make('textarea');text.name='body';text.required=true;text.rows=8;textLabel.append(text);
   const consent=make('input');consent.type='checkbox';consent.required=true;const consentLabel=make('label');consentLabel.append(consent,document.createTextNode(' I have read this text and it can be posted publicly.'));
   const actions=make('div');actions.className='schedule-actions';const submit=make('button','Send report'),retry=make('button','Retry original request');submit.type='submit';retry.type='button';retry.className='secondary';
   // A kept request shows exactly what was sent; otherwise the form shows what the person had typed.
   const shown=pending.command?pending.command.body as typeof draft:draft;
   kind.value=shown.kind;titleInput.value=shown.title;text.value=shown.body;
   const remember=()=>{if(!pending.command)draft={kind:kind.value,title:titleInput.value,body:text.value};};
   kind.onchange=titleInput.oninput=text.oninput=remember;
   const held=!!pending.command;for(const control of [kind,titleInput,text,consent])control.disabled=held||working;submit.disabled=held||working;retry.hidden=!held;retry.disabled=working;
   form.onsubmit=event=>{event.preventDefault();if(working)return;pending.begin(path,{kind:kind.value,title:titleInput.value,body:text.value});void send();};
   retry.onclick=()=>{if(!working)void send();};
   actions.append(submit,retry);form.append(notice,kindLabel,titleLabel,textLabel,consentLabel,actions);parts.push(form);
  }
  const list=make('section');list.append(make('h3','Your reports'));
  if(mine.reports.submissions.length===0)list.append(make('p','You have not sent a report.'));
  for(const report of mine.reports.submissions){
   const row=make('div');row.className='issue-report';row.dataset.issueReport=report.id;
   const line=make('p');line.append(make('strong',report.title),document.createTextNode(' · '+reportStatus(report)));
   if(report.state==='submitted'&&report.issueUrl){
    const link=make('a','Open issue');link.href=report.issueUrl;link.target='_blank';link.rel='noopener noreferrer';link.setAttribute('aria-label',`Open issue ${report.issueNumber}: ${report.title}`);
    line.append(document.createTextNode(' · '),link);
   }
   row.append(line);
   const choices=offered(report);
   if(choices.length){
    const buttons=make('div');buttons.className='schedule-actions';
    for(const choice of choices){
     const button=make('button',labels[choice]);button.type='button';button.className='secondary';button.dataset.issueReportChoice=choice;button.disabled=working;
     button.setAttribute('aria-label',`${labels[choice]}: ${report.title}`);button.onclick=()=>void decide(report,choice);buttons.append(button);
    }
    row.append(buttons);
   }
   list.append(row);
  }
  parts.push(list);body.replaceChildren(...parts);
 }
 // One exchange for one account. Whatever the dialog did meanwhile, the exchange ends by settling the kept request
 // and redrawing, so a closed and reopened dialog is never left waiting on an answer that already arrived.
 async function exchange(work:()=>Promise<string>,failed:(error:unknown)=>string){
  if(working)return;const owner=o.identity();working=true;
  body.querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLTextAreaElement|HTMLButtonElement>('input,select,textarea,button').forEach(control=>control.disabled=true);
  let result:string;
  try{result=await work();}catch(error){if(owner!==o.identity())return;result=failed(error);}
  if(owner!==o.identity())return;
  working=false;await load().catch(()=>undefined);
  if(owner===o.identity())status.textContent=result;
 }
 function send(){
  const sending=pending.command!;const uncertain=pending.sending();transport.write(pending.command);status.textContent='Sending your report…';
  return exchange(async()=>{
   const report=await o.api<Report>(sending.path,{...sending.body,operationId:sending.operationId});
   pending.clear();transport.write(undefined);draft={kind:'',title:'',body:''};return reportStatus(report);
  },error=>{
   const code=(error as {code?:string}).code;
   // An answer that proves the request was never retained releases it even after an uncertain attempt.
   if(neverRetained(code))pending.clear();else pending.failed(code,uncertain);
   transport.write(pending.command);
   return message(error)+(pending.command?' Retry sends the same report under the same request, so it cannot be created twice.':'');
  });
 }
 function decide(report:Report,choice:Choice){
  status.textContent='Asking…';
  return exchange(async()=>{
   if(choice!=='look')return reportStatus(await o.api<Report>(path+'/resolve',{id:report.id,expectedAttempt:report.attempt,resolution:choice}));
   const found=await o.api<{report:Report;lookup:'found'|'absent'|'incomplete'|'not_needed'}>(path+'/reconcile',{id:report.id});
   return found.lookup==='found'||found.lookup==='not_needed'?reportStatus(found.report)
    :found.lookup==='absent'?'The tracker has no issue for this report yet. It may still appear; look again later, send it again or give it up.'
    :'The tracker could not be read to the end, so nothing is known yet. Look again later.';
  },message);
 }
 open.onclick=()=>{if(!working)status.textContent='';dialog.showModal();void load().catch(error=>{body.replaceChildren();status.textContent=message(error);});};
 return{
  sync(){open.hidden=!o.can();},
  // Another account starts clean: nothing typed, kept or in flight for the previous one is shown or acted on.
  reset(){generation++;pending.clear();working=false;draft={kind:'',title:'',body:''};if(dialog.open)dialog.close();body.replaceChildren();status.textContent='';}
 };
}
