// One in-flight read; scope changes and hidden views cannot publish stale results.
// The host chooses the authorized source and its financial context explicitly.
export class IntervalRefresh<T>{
 private generation=0;private active=false;private running=false;private disposed=false;private period=0;
 private timer:ReturnType<typeof setTimeout>|undefined;
 constructor(private load:()=>Promise<T>,private publish:(value:T)=>void,private failed:(error:unknown)=>void){}
 private cancel(){if(this.timer!==undefined)clearTimeout(this.timer);this.timer=undefined;}
 private arm(){this.cancel();if(this.active&&!this.disposed&&this.period)this.timer=setTimeout(()=>void this.refresh(),this.period);}
 setActive(active:boolean){if(this.disposed||this.active===active)return;this.active=active;this.generation++;if(active)this.arm();else this.cancel();}
 setPeriod(milliseconds:number){if(![0,5000,30000].includes(milliseconds))throw Error('Choose Off, 5 seconds or 30 seconds.');this.period=milliseconds;this.arm();}
 invalidate(){this.generation++;this.period=0;this.cancel();}
 async refresh(){
  if(!this.active||this.running||this.disposed)return;
  this.cancel();this.running=true;const generation=this.generation;
  try{const value=await this.load();if(this.active&&!this.disposed&&generation===this.generation)this.publish(value);}
  catch(error){if(this.active&&!this.disposed&&generation===this.generation)this.failed(error);}
  finally{this.running=false;this.arm();}
 }
 dispose(){this.disposed=true;this.active=false;this.generation++;this.cancel();}
}
