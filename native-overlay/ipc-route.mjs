// Protocol reference: soleillevant0125/codex-token-overlay (MIT), b3a38d7.
import net from 'node:net';
import crypto from 'node:crypto';

const UUID_RE = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;

export class RouteState {
  constructor() {
    this.routes = new Map();
    this.connected = false;
    this.seq = 0;
    this.lastKnownThreadId = null;
  }

  reset() {
    this.routes.clear();
    this.connected = false;
    this.lastKnownThreadId = null;
  }

  update(message){
    const p=message.params||{};
    if(message.type!=='broadcast')return;
    if(message.method==='client-status-changed'&&p.status==='disconnected'){
      if(typeof p.clientId==='string'&&p.clientId.length>0){
        for(const key of [...this.routes.keys()]){
          if(key.startsWith(p.clientId+'\x1f'))this.routes.delete(key);
        }
      }
      if(this.routes.size===0)this.lastKnownThreadId=null;
      return;
    }
    if(message.method!=='thread-stream-following-changed'||!message.sourceClientId||p.hostId!=='local'||!UUID_RE.test(p.conversationId||''))return;
    const id=String(p.conversationId).toLowerCase();
    const key=message.sourceClientId+'\x1f'+p.hostId;
    if(p.following===true){
      this.seq+=1;
      this.routes.set(key,{threadId:id,seq:this.seq});
      this.lastKnownThreadId=id;
    }else if(p.following===false&&this.routes.get(key)?.threadId===id){
      this.routes.delete(key);
    }
  }

  get activeThreadId(){
    if(!this.connected)return null;
    let best=null;
    for(const item of this.routes.values()){
      if(!best||item.seq>best.seq)best=item;
    }
    return best?best.threadId:null;
  }

  get threadId(){
    if(!this.connected)return null;
    return this.activeThreadId||this.lastKnownThreadId;
  }
}

export class IpcRoute extends RouteState {
  constructor(onChange=()=>{},clientType='codex-usage-companion'){
    super();
    this.onChange=onChange;
    this.clientType=clientType;
    this.stopped=false;
    this.socket=null;
    this.timer=null;
    this.delay=350;
  }
  start(){
    if(this.stopped)return;
    const socket=net.createConnection('\\\\.\\pipe\\codex-ipc');this.socket=socket;
    let buffer=Buffer.alloc(0),skip=0;
    const notify=()=>this.onChange({connected:this.connected,threadId:this.threadId,activeThreadId:this.activeThreadId,routeCount:this.routes.size});
    socket.on('connect',()=>{
      this.reset();this.connected=true;this.delay=350;notify();
      const body=Buffer.from(JSON.stringify({type:'request',requestId:crypto.randomUUID(),sourceClientId:'initializing-client',version:0,method:'initialize',params:{clientType:this.clientType}}));
      const header=Buffer.alloc(4);header.writeUInt32LE(body.length);socket.write(Buffer.concat([header,body]));
    });
    socket.on('data',chunk=>{
      if(skip){const n=Math.min(skip,chunk.length);skip-=n;chunk=chunk.subarray(n);}
      buffer=Buffer.concat([buffer,chunk]);
      while(buffer.length>=4){
        const length=buffer.readUInt32LE(0);
        if(length===0||length>256*1024*1024){socket.destroy();return;}
        if(length>4*1024*1024){const consumed=Math.min(length,buffer.length-4);skip=length-consumed;buffer=buffer.subarray(4+consumed);continue;}
        if(buffer.length<4+length)break;
        const payload=buffer.subarray(4,4+length);buffer=buffer.subarray(4+length);
        try{this.update(JSON.parse(payload));notify();}catch{}
      }
    });
    socket.on('error',()=>{});
    socket.on('close',()=>{this.reset();notify();if(!this.stopped){this.timer=setTimeout(()=>this.start(),this.delay);this.delay=Math.min(5000,this.delay*2);}});
  }
  stop(){this.stopped=true;clearTimeout(this.timer);this.socket?.destroy();this.reset();}
}
