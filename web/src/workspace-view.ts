// Reloads can reattach to their server-side selection. Browser history holds
// only opaque routing IDs. A new tab always registers its own workspace.
export const workspaceDocument=crypto.randomUUID();
let previousDocument:string|undefined;
let registered=false;
declare global {interface Window {openEtrmWorkspaceView?:string}}
export function workspaceView(popout=false):string {
 if(window.openEtrmWorkspaceView)return window.openEtrmWorkspaceView;
 if(popout){
  const parent=window.opener as Window|null;
  if(!parent||parent.location.origin!==location.origin||!parent.openEtrmWorkspaceView)throw Error('The originating workspace is unavailable.');
  return window.openEtrmWorkspaceView=parent.openEtrmWorkspaceView;
 }
 const navigation=(performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming|undefined)?.type;
 const prior=navigation==='reload'||navigation==='back_forward'?history.state?.openEtrmView:undefined;
 const view=typeof prior==='string'?prior:crypto.randomUUID();
 if(typeof prior==='string'&&typeof history.state?.openEtrmDocument==='string')previousDocument=history.state.openEtrmDocument;
 history.replaceState({...history.state,openEtrmView:view,openEtrmDocument:previousDocument},'');
 return window.openEtrmWorkspaceView=view;
}
export function reattachHeaders():Record<string,string>{
 return previousDocument?{'X-OpenETRM-Reattach':previousDocument}:{};
}
export function workspaceRegistered(){
 registered=true;previousDocument=undefined;rememberWorkspaceView();
}
// Fragment navigation creates a history entry without copying the routing ID.
export function rememberWorkspaceView(){
 const view=window.openEtrmWorkspaceView;
 const owner=registered?workspaceDocument:previousDocument;
 if(view&&(history.state?.openEtrmView!==view||history.state?.openEtrmDocument!==owner))
  history.replaceState({...history.state,openEtrmView:view,openEtrmDocument:owner},'');
}
export function onWorkspaceLeave(release:()=>void){
 window.addEventListener('pagehide',event=>{
  if(event.persisted)document.documentElement.hidden=true;
  release();
 });
 // A suspended document has released its reads and may have lost authority.
 window.addEventListener('pageshow',event=>{if(event.persisted)location.reload();});
}
export function releaseView(root:string,csrf:string,view:string){
 if(csrf)void fetch(root+'workspace/view/close',{method:'POST',credentials:'same-origin',keepalive:true,
  headers:{'X-OpenETRM-View':view,'X-OpenETRM-Document':workspaceDocument,'X-Preview-CSRF':csrf}}).catch(()=>{});
}
export function viewEvents(url:string):EventSource {
 const target=new URL(url,location.origin);
 target.searchParams.set('workspaceView',workspaceView());
 return new EventSource(target.href);
}
