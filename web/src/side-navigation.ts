/** Jump to terms without changing their membership or hiding required form controls. */
export function sideNavigation(label:string,components:{label:string;target:HTMLElement}[]){
 const nav=document.createElement('nav');nav.className='schedule-actions';nav.setAttribute('aria-label',label);
 for(const component of components){
  const b=document.createElement('button');b.type='button';b.className='secondary';b.textContent=component.label;
  component.target.tabIndex=-1;
  b.onclick=()=>{component.target.focus({preventScroll:true});component.target.scrollIntoView({block:'nearest'});};nav.append(b);
 }
 return nav;
}
