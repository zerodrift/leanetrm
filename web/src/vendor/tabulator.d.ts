export type GridColumn={getField():string;getWidth():number;setWidth(width:number):void;move(to:string,after:boolean):void;show():void;hide():void};
export type GridRow={getData():Record<string,string>;getElement():HTMLElement;getCell(field:string):{setValue(value:string):void}|false;scrollTo(position?:'top'|'center'|'bottom'|'nearest',ifVisible?:boolean):Promise<void>;normalizeHeight():void};
export type GridGroup={getKey():string;getElement():HTMLElement|false;isVisible():boolean;toggle():void};
export class TabulatorFull {
 // Pinned 6.6.1 adapter: redraw/resize re-reads this instance configuration.
 // The real grid checks qualify live changes for one expanded row and reset.
 options:{renderVerticalBuffer:number;groupHeader:unknown};
 constructor(host:HTMLElement,options:Record<string,unknown>);
 on(event:string,callback:(...args:any[])=>void):void;
 destroy():void;
 setFilter(filter:(row:Record<string,string>)=>boolean):void;
 getData(range?:string):Record<string,string>[];
 /** Replaces every row, keeping the table's sort, filters and scroll offset. */
 replaceData(data:Record<string,string>[]):Promise<void>;
 /** Adds rows at the bottom (false) or top (true). */
 addData(data:Record<string,string>[],top:boolean):Promise<unknown>;
 setGroupBy(groupBy:((data:Record<string,string>)=>string)|false):void;
 /** The row whose `index` field holds this value. */
 getRow(index:string):GridRow|false;
 setHeight(height:string):void;
 getColumns():GridColumn[];
 getColumn(field:string):GridColumn|false;
 getSorters():{field:string;dir:'asc'|'desc'}[];
 setSort(sort:{column:string;dir:'asc'|'desc'}[]):void;
 redraw(force?:boolean):void;
 getRows(range?:string):GridRow[];
 getGroups():GridGroup[];
}
