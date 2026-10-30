/** Presentation of the backend operator output types; no curve classification or calculation. */
const types={
 curve:{label:'Curve',path:'M2 15h16M3 12l4-6 5 3 5-6'},
 contract:{label:'Dated contract',path:'M5 2h7l4 4v12H5zM12 2v5h4M8 11h5M8 14h5'},
 selection:{label:'Contract selection',path:'M2 4h6v6H2zM12 4h6v6h-6zM5 13v4h10v-4M10 13v4'},
 dates:{label:'Date sequence',path:'M3 5h14v13H3zM6 2v5M14 2v5M3 9h14M6 12h2M12 12h2M6 15h2'},
 panel:{label:'Price panel',path:'M3 3h14v14H3zM3 8h14M8 3v14M12 11h3M12 14h3'},
 curve_result:{label:'Curve output',path:'M2 15h10M3 12l3-5 4 2M12 5h6M15 2l3 3-3 3'},
} as const;
export type ComponentType=keyof typeof types;
function known(type:string){if(!Object.hasOwn(types,type))throw Error('Unknown component type');return types[type as ComponentType];}
export const componentTypeLabel=(type:string)=>known(type).label;
export function componentTypeBadge(type:string){
 const {label,path}=known(type),badge=document.createElement('span'),ns='http://www.w3.org/2000/svg',svg=document.createElementNS(ns,'svg'),shape=document.createElementNS(ns,'path'),text=document.createElement('span');
 badge.className='curve-component-type';badge.dataset.componentType=type;
 svg.setAttribute('viewBox','0 0 20 20');svg.setAttribute('aria-hidden','true');svg.setAttribute('focusable','false');
 shape.setAttribute('d',path);svg.append(shape);text.textContent=label;badge.append(svg,text);return badge;
}
