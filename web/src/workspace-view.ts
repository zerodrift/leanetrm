// Reloads can reattach to their server-side selection. Browser history holds
// only an opaque routing ID. A new tab always registers its own workspace.
export const workspaceDocument=crypto.randomUUID();
declare global {interface Window {openEtrmWorkspaceView?:string}}
export function workspaceView(popout=false):string {
 if(window.openEtrmWorkspaceView)return window.openEtrmWorkspaceView;
 if(popout){
  const parent=window.opener as Window|null;
  if(!parent||parent.location.origin!==location.origin||!parent.openEtrmWorkspaceView)throw Error('The originating workspace is unavailable.');
  return window.openEtrmWorkspaceView=parent.openEtrmWorkspaceView;
 }
 const reload=(performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming|undefined)?.type==='reload';
 const prior=reload?history.state?.openEtrmView:undefined;
 const view=typeof prior==='string'?prior:crypto.randomUUID();
 history.replaceState({...history.state,openEtrmView:view},'');
 return window.openEtrmWorkspaceView=view;
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
