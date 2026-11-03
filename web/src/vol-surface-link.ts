// The series a person is pointing at on the Vol surfaces page, shared by its views: hovering or focusing a series in the heat map,
// a smile or the 3D view sets it, and every view, the shared readout among them, highlights the same series. A series is named by
// its exact code, as the surface read lists it; null is none.
export type SurfaceLink={current():string|null;set(code:string|null):void;subscribe(listener:(code:string|null)=>void):()=>void};

export function surfaceLink():SurfaceLink{
 let current:string|null=null;
 const listeners=new Set<(code:string|null)=>void>();
 return{
  current:()=>current,
  // Each listener hears a change once; setting the series already shown changes nothing.
  set(code){if(code===current)return;current=code;for(const listener of [...listeners])listener(code);},
  subscribe(listener){listeners.add(listener);return()=>{listeners.delete(listener);};},
 };
}
