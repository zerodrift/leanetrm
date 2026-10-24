export type GridColumn={getField():string;getWidth():number};
export class TabulatorFull {
 constructor(host:HTMLElement,options:Record<string,unknown>);
 on(event:string,callback:(...args:any[])=>void):void;
 destroy():void;
 setFilter(filter:(row:Record<string,string>)=>boolean):void;
 getData(range?:string):Record<string,string>[];
 getColumns():GridColumn[];
 getSorters():{field:string;dir:'asc'|'desc'}[];
 setSort(sort:{column:string;dir:'asc'|'desc'}[]):void;
 redraw(force?:boolean):void;
 getRows():{getData():Record<string,string>;getCell(field:string):{setValue(value:string):void}|false}[];
}
