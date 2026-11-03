"""Audit a frozen GUI export without modifying it or awarding unobserved cases."""
import hashlib
import json
from collections import Counter
from datetime import datetime
from pathlib import Path

def main():
    root=Path(__file__).resolve().parents[1]
    report=root/'reports/aict-beta-20261103T100452724Z'
    hashes={}
    for line in (report/'SHA256SUMS.txt').read_text('utf-8-sig').splitlines():
        expected,name=line.split('  ',1);p=(report/name).resolve();p.relative_to(report.resolve())
        assert hashlib.sha256(p.read_bytes()).hexdigest()==expected,name
        hashes[name]=expected
    read=lambda name:json.loads((report/name).read_text('utf-8-sig'))
    status=read('state/status.json')['status'];state=read('rules-and-usage.json')
    assert status['policy']==state['policy'] and status['storageHealthy'] and status['clockConsistent']
    assert status['execution']['confirmed'] and status['execution']['fault'] is None
    assert status['telemetryConnected'] and status['websites']['connectedProfiles']==1
    events=[json.loads(x) for x in (report/'state/events/20261103.jsonl').read_text('utf-8-sig').splitlines()]
    counts=dict(Counter(x['kind'] for x in events));submitted=None;timings=[]
    for event in events:
        if event['kind']=='ChangeSubmitted':submitted=event
        elif event['kind']=='ChangeApplied' and submitted:
            delta=(datetime.fromisoformat(event['atUtc'])-datetime.fromisoformat(submitted['atUtc'])).total_seconds()
            timings.append({'submit_utc':submitted['atUtc'],'applied_utc':event['atUtc'],'elapsed_seconds':delta,'configured_delay_seconds':60,'extra_wait_seconds':max(0,delta-60)})
            submitted=None
        elif event['kind']=='PendingCancelled':submitted=None
    g=status['policy']['groups'][0];cycle=g['cycle']
    phase=(datetime.fromisoformat(status['effectiveUtc'])-datetime.fromisoformat(cycle['anchorUtc'])).total_seconds()%180
    assert phase>=120 and not status['execution']['denied']
    result={'kind':'AICT_INTEGRATED_V1_RETURNED_EXPORT_AUDIT','report':report.relative_to(root).as_posix(),
            'source_sha256':hashes,'files_verified':len(hashes),'event_counts':counts,'change_timings':timings,
            'migration':read('state/migration.json'),'status_at_export':{'backend_replied':True,'storage_healthy':True,'clock_trusted':True,'application_plan_readback_confirmed':True,'collector_connected':True,'browser_profiles':1,'browser_fault':None,'snapshot_cycle_phase_seconds':phase,'snapshot_is_rest_period':True,'pending':status['pending']},
            'operator_reported_app_block_website_block_exception_allow':True,
            'observed_log_app_denials':counts.get('ApplicationLaunchDenied',0),'observed_log_website_denials':counts.get('WebsiteNavigationDenied',0),
            'exception_allow_not_independently_proven_by_deny_events':True,
            'browser_block_scope_is_www_host_not_entire_bilibili_root':g['websiteDomains']==['www.bilibili.com'],
            'scope_note':'space.bilibili.com is outside www.bilibili.com and is allowed even without that exception; use bilibili.com to block the whole root.',
            'full_bypass_reboot_quota_uninstall_matrix_awarded':False,'new_v2_timing_and_update_awarded':False,'raw_sources_unchanged':True}
    assert all(hashlib.sha256((report/n).read_bytes()).hexdigest()==h for n,h in hashes.items())
    output=root/'reports/integrated-v1-audit-20261103T100452724Z.json'
    with output.open('x',encoding='utf-8') as f:json.dump(result,f,ensure_ascii=False,indent=2)
    print(json.dumps({'verified':len(hashes),'app_denials':result['observed_log_app_denials'],'web_denials':result['observed_log_website_denials'],'receipt':str(output)},ensure_ascii=False))

if __name__=='__main__':main()
