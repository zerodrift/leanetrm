import {friendly} from './market-view.js';
// Side names describe their terms; direction is a separate selection.
const names:Record<string,string>={energy_delivery:'Power',certificate_delivery:'REC',capacity_delivery:'Capacity',strike_payment:'Fixed price',indexed_payment:'Index price',fixed_charge:'Fee',certificate_payment:'REC price',capacity_payment:'Capacity price'};
export const ppaSideLabel=(role:string)=>names[role]??friendly(role);
