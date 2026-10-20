export type RequestCommand={operationId:string;path:string;body:Record<string,unknown>;uncertain?:boolean};

// Once sent, a command keeps its identity and economics through uncertain replies.
export class PendingCommand {
  command:RequestCommand|undefined;
  constructor(private readonly knownRejection:(code:string|undefined)=>boolean=()=>false){}
  begin(path:string,body:Record<string,unknown>,newId:()=>string=()=>crypto.randomUUID()):RequestCommand{
    if(this.command){
      if(this.command.path!==path||JSON.stringify(this.command.body)!==JSON.stringify(body))throw Error('Review the outstanding request before changing its details. You can retry the original request or discard it after checking recent changes.');
      return this.command;
    }
    return this.command={operationId:newId(),path,body:structuredClone(body)};
  }
  clear(){this.command=undefined;}
  sending(){
    const previouslyUncertain=this.command?.uncertain===true;
    if(this.command)this.command.uncertain=true;
    return previouslyUncertain;
  }
  failed(code:string|undefined,previouslyUncertain=this.command?.uncertain===true){
    if(!this.command)return;
    // A denied retry says nothing about the preceding uncertain transaction.
    if(this.knownRejection(code)&&!previouslyUncertain)this.clear();
    else this.command.uncertain=true;
  }
}
