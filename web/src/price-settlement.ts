/** The backend qualifies indices and their fixing schedules. The editor transports the explicit choice. */
export type PriceSettlement={mode:'curve_target'|'override';index:string|null;projectionPolicy:'forward_curve_no_basis_v1'|null};
export type SettlementIndex={code:string;name:string};
export function readPriceSettlement(mode:string,index:string,policy:string,indices:readonly SettlementIndex[]):PriceSettlement{
 if(mode==='curve_target'){if(index||policy)throw Error('The settlement choices conflict.');return{mode,index:null,projectionPolicy:null};}
 if(mode!=='override')throw Error('Choose a price settlement mode.');
 if(!indices.some(choice=>choice.code===index))throw Error('Choose an offered settlement index.');
 if(policy!=='forward_curve_no_basis_v1')throw Error('Choose the explicit projection policy for the different index.');
 return{mode,index,projectionPolicy:policy};
}
export function mountPriceSettlement(host:HTMLElement,prefix:string,options:{enabled:()=>boolean;indices:()=>readonly SettlementIndex[];changed:()=>void}){
 const field=(label:string,suffix:string)=>{const wrap=document.createElement('div'),caption=document.createElement('label'),select=document.createElement('select');select.id=prefix+'-settlement-'+suffix;caption.htmlFor=select.id;caption.textContent=label;wrap.append(caption,select);host.append(wrap);return select;};
 const mode=field('Price settlement','mode'),index=field('Settlement index','index'),policy=field('Override projection policy','policy');
 mode.append(new Option('Choose…',''),new Option('Curve target index','curve_target'),new Option('Different settlement index','override'));
 policy.append(new Option('Choose…',''),new Option('Use forward curve · basis unmodelled','forward_curve_no_basis_v1'));
 const note=document.createElement('p');note.className='field-help';note.textContent='Before the selected fixing, the chosen forward curve supplies the price. Any basis to the different settlement index is unmodelled.';host.append(note);
 function sync(){const selected=index.value,choices=options.indices();index.replaceChildren(new Option('Choose…',''),...choices.map(choice=>new Option(choice.name,choice.code)));if(selected&&!choices.some(choice=>choice.code===selected)){const retained=new Option(selected+' · unavailable here',selected);retained.disabled=true;index.append(retained);}index.value=selected;const override=mode.value==='override',enabled=options.enabled();index.parentElement!.hidden=policy.parentElement!.hidden=note.hidden=!override;index.required=policy.required=enabled&&override;index.disabled=policy.disabled=!enabled||!override;mode.required=enabled;mode.disabled=!enabled;}
 function changed(){sync();options.changed();}
 mode.onchange=()=>{index.value='';policy.value='';changed();};index.onchange=policy.onchange=changed;
 function restore(value:PriceSettlement|null){mode.value=value?.mode??'';sync();index.value=value?.index??'';if(value?.index&&!options.indices().some(choice=>choice.code===value.index)){const retained=new Option(value.index+' · booked value (unavailable here)',value.index);retained.disabled=true;index.append(retained);index.value=value.index;}policy.value=value?.projectionPolicy??'';sync();}
 sync();
 return{sync,restore,reset:()=>restore(null),selected:()=>{try{return readPriceSettlement(mode.value,index.value,policy.value,options.indices());}catch{return undefined;}},read:()=>readPriceSettlement(mode.value,index.value,policy.value,options.indices()),override:()=>mode.value==='override'};
}
