import assert from 'node:assert/strict';
import { withLiveRate, LiveRateClient } from '../scripts/live-rate.mjs';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';

const thread='00000000-0000-0000-0000-000000000001';
const usage={sources:{session:{id:'session',metrics:[{id:'currentTaskTokens',value:'20'}]}}};
const reading={threadId:thread,sampledAt:10000,speed:{tokPerSecond:42.6,estimated:true}};
const metric=result=>result.sources.session.metrics.find(item=>item.id==='outputSpeed');
assert.equal(metric(withLiveRate(usage,reading,thread,11000)).value,'42.6 tok/s');
assert.equal(metric(withLiveRate(usage,reading,thread,16000)).value,'42.6 tok/s');
assert.match(metric(withLiveRate(usage,reading,thread,16000)).detail,/最近有效读数/);
assert.equal(metric(withLiveRate(usage,{...reading,threadId:'other'},thread,11000)).value,'0.0 tok/s');
assert.equal(metric(withLiveRate(usage,{...reading,speed:{tokPerSecond:0,estimated:false}},thread,11000)).value,'0.0 tok/s');
assert.equal(usage.sources.session.metrics.length,1);
assert.equal(metric(withLiveRate(usage,{threadId:thread,sampledAt:10000,phase:'idle',speed:{}},thread,11000)).value,'0.0 tok/s');
assert.equal(metric(withLiveRate(usage,{threadId:thread,sampledAt:10000,phase:'generating',speed:{}},thread,11000)).value,'0.0 tok/s');
assert.equal(metric(withLiveRate(usage,{threadId:thread,sampledAt:10000,phase:'generating',speed:{lastCompletedTokPerSecond:20}},thread,11000)).value,'20.0 tok/s');
assert.equal(metric(withLiveRate(usage,null,null,11000)).value,'0.0 tok/s');
for(const sample of [reading,null,{...reading,sampledAt:1},{...reading,threadId:'other'}])assert.match(metric(withLiveRate(usage,sample,thread,11000)).value,/^\d+\.\d tok\/s$/);
const client=new LiveRateClient();
client.start();
await client.sample([thread,'not-a-thread']);
assert.equal(client.get(thread)?.threadId,thread);
assert.equal(client.get('not-a-thread'),undefined);
await client.stop();
const selected=spawnSync(process.env.CODEX_USAGE_RATE_PYTHON||'python.exe',['-B',fileURLToPath(new URL('./live-rate-selected.py',import.meta.url))],{encoding:'utf8',windowsHide:true,env:{...process.env,PYTHONUTF8:'1'}});
assert.equal(selected.status,0,selected.stderr||selected.error?.message);
const temporary=await fs.mkdtemp(path.join(os.tmpdir(),'codex-rate-late-'));
assert.ok(path.resolve(temporary).startsWith(path.resolve(os.tmpdir())+path.sep));
const fake=path.join(temporary,'delayed.py');
await fs.writeFile(fake,`import sys,json,time
for index,line in enumerate(sys.stdin):
    request=json.loads(line)
    if index==1:time.sleep(0.2)
    value=10 if index==0 else 999 if index==1 else 42.6
    reading={'threadId':request['threadIds'][0],'sampledAt':int(time.time()*1000),'phase':'generating','speed':{'tokPerSecond':value,'estimated':False}}
    print(json.dumps({'requestId':request['requestId'],'readings':[reading]}),flush=True)
`);
const delayed=new LiveRateClient({scriptPath:fake});delayed.start();
try{
  await delayed.sample([thread]);assert.equal(delayed.get(thread).speed.tokPerSecond,10);
  delayed.timeoutMs=150;
  await delayed.sample([thread]);assert.equal(delayed.get(thread).speed.tokPerSecond,10,'A timeout preserves the latest reading');
  await delayed.sample([thread]);assert.equal(delayed.get(thread).speed.tokPerSecond,42.6,'A late reply cannot satisfy a newer request');
}finally{await delayed.stop();await fs.rm(temporary,{recursive:true,force:true});}
const polling=new LiveRateClient();let polls=0,updates=0;
polling.sample=async ids=>{assert.deepEqual(ids,[thread]);polls++;};
try{
  await new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>reject(new Error('Independent sampler did not refresh')),2000);
    polling.startWatching(()=>[thread],()=>{if(++updates>=3){clearTimeout(timer);resolve();}},5);
  });
  assert.ok(polls>=3);
}finally{await polling.stop();}
console.log('PASS: estimated/measured speed, stale/mismatched task isolation, and real read-only Python bridge.');
