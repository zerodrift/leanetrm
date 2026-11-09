export const riskViews=['risk','risk-scenarios','risk-var','risk-runs'] as const;
export type RiskView=typeof riskViews[number];
export const isRiskView=(page:string):page is RiskView=>(riskViews as readonly string[]).includes(page);
export const riskViewNames:Record<RiskView,string>={risk:'Reports','risk-scenarios':'Scenarios & Greeks','risk-var':'VaR / ES','risk-runs':'Run history'};

// Navigation only: Risk is one page, and its sidebar entries are views of it. From another page an entry opens Risk through the existing
// guards and takes effect once the page has moved; a refused leave changes nothing.
export function setupRiskViews(o:{show:(view:RiskView)=>void;open:()=>void;onPage:()=>boolean;busy:()=>boolean;available:()=>boolean}){
 const button=(view:RiskView)=>document.getElementById('nav-'+view) as HTMLButtonElement;
 let current:RiskView='risk',wanted:RiskView|undefined,shown=false;
 function sync(){for(const view of riskViews){button(view).disabled=o.busy();button(view).classList.toggle('active',shown&&view===current);}}
 function select(view:RiskView){current=view;sync();o.show(view);}
 for(const view of riskViews)button(view).onclick=()=>{
  if(o.busy()||!o.available())return;
  if(!o.onPage()){wanted=view;o.open();}
  else if(view!==current)select(view);
 };
 return {sync,select,current:()=>current,
  // Called as the app moves to a page. An address or the assistant arriving at Risk without choosing a view opens Reports.
  page(page:string){shown=page==='risk';if(shown)select(wanted??'risk');wanted=undefined;sync();},
  reset(){current='risk';wanted=undefined;shown=false;sync();},
 };
}
