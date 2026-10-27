"""Read-only JSONL bridge for the monitor. No model calls, account or HTTP server."""
import json
import re
import sys
import time

from rate_metrics import MetricsCollector

UUID = re.compile(r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$',re.I)
collector = MetricsCollector()
for line in sys.stdin:
    result = []
    request_id = None
    try:
        request = json.loads(line) if len(line) <= 4096 else []
        request_id = request.get('requestId') if isinstance(request,dict) else None
        selected = request.get('threadIds',[]) if isinstance(request,dict) else request
        if not isinstance(selected,list):
            selected = []
        watched = {thread for thread in selected[:16] if isinstance(thread,str) and UUID.fullmatch(thread)}
        if watched != collector.selected:
            collector.selected = watched
            collector.last_scan = 0
        collector.tick()
        for thread in selected[:16]:
            if not isinstance(thread,str) or not UUID.fullmatch(thread):
                continue
            snapshot = collector.snapshot(thread)
            result.append({'threadId':thread,'sampledAt':int(time.time()*1000),
                           'speed':snapshot['speed'] if snapshot['thread'] else {},
                           'phase':snapshot['thread']['phase'] if snapshot['thread'] else 'unknown'})
    except (OSError,ValueError,TypeError):
        pass
    response = {'requestId':request_id,'readings':result} if request_id is not None else result
    print(json.dumps(response,separators=(',',':'),allow_nan=False),flush=True)
