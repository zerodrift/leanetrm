import {pageAvailable} from './navigation-groups.js';
type PageStorage=Pick<Storage,'getItem'|'setItem'>;
const key=(owner:string)=>'openetrm:page:v1:'+owner;
// Only the page identifier is remembered, per tab, workspace and account. Business inputs stay in the server session.
export function rememberPage(storage:()=>PageStorage,owner:string,page:string){try{storage().setItem(key(owner),page);}catch{/* Navigation also works when browser storage is unavailable. */}}
export function restoredPage(storage:()=>PageStorage,owner:string,permissions:string[]):string|null{
 try{const page=storage().getItem(key(owner));return page&&pageAvailable(page,permissions)?page:null;}catch{return null;}
}
