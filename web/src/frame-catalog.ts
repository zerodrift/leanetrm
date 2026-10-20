export type FrameCatalog={durableSelection:string;selectionPolicy:string;maximumFrames:number;durableLimit:number;hasMoreDurableCloses:boolean};
export function validFrameCatalog(value:FrameCatalog){return !!value&&value.durableSelection==='exact_recorded_close_pins_v1'&&
 value.selectionPolicy==='preserved_session_plus_latest_visible_closes_v1'&&Number.isInteger(value.maximumFrames)&&value.maximumFrames>0&&
 Number.isInteger(value.durableLimit)&&value.durableLimit>=0&&value.durableLimit<=value.maximumFrames&&typeof value.hasMoreDurableCloses==='boolean';}
export function frameCatalogNote(value:FrameCatalog|undefined){
 if(!value)return '';
 if(!validFrameCatalog(value))throw Error('The market navigation has an unsupported close-catalog policy. Refresh and try again.');
 return (value.hasMoreDurableCloses?`Showing recent saved closes within this session’s ${value.maximumFrames}-close limit. `:'')+'Stored closes retain their original inputs.';
}
