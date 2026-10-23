// The backend's executable presets are the sole source of supported combinations.
export function powerChoices(presets:readonly {product:string;block:string;minutes:number}[],product='',block=''){
 return {products:[...new Set(presets.map(p=>p.product))],
  blocks:[...new Set(presets.filter(p=>p.product===product).map(p=>p.block))],
  intervals:[...new Set(presets.filter(p=>p.product===product&&p.block===block).map(p=>p.minutes))]};
}
