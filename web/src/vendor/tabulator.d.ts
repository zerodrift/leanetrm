export type GridColumn={getField():string;getWidth():number};
export type GridRow={getData():Record<string,string>;getElement():HTMLElement;getCell(field:string):{setValue(value:string):void}|false;scrollTo(position?:'top'|'center'|'bottom'|'nearest',ifVisible?:boolean):Promise<void>;normalizeHeight():void};
export type GridGroup={getKey():string;getElement():HTMLElement|false;isVisible():boolean;toggle():void};
export class TabulatorFull {
 // Pinned 6.6.1 adapter: redraw/resize re-reads this instance configuration.
 // The real grid checks qualify live changes for one expanded row and reset.
 options:{renderVerticalBuffer:number};
 constructor(host:HTMLElement,options:Record<string,unknown>);
 on(event:string,callback:(...args:any[])=>void):void;
 destroy():void;
 setFilter(filter:(row:Record<string,string>)=>boolean):void;
 getData(range?:string):Record<string,string>[];
 getColumns():GridColumn[];
 getColumn(field:string):{show():void;hide():void}|false;
 getSorters():{field:string;dir:'asc'|'desc'}[];
 setSort(sort:{column:string;dir:'asc'|'desc'}[]):void;
 redraw(force?:boolean):void;
 getRows():GridRow[];
 getGroups():GridGroup[];
}
