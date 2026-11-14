"""Real Windows supervisor Magisk transaction acceptance (requires approved ADB-root provisioning)."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

REPO = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--allow-provisioning-adb-root', action='store_true', required=True)
    parser.add_argument('--mode', choices=['normal', 'corrupt', 'interrupted-staging', 'crash-staging', 'timeout', 'data-recovery'], default='normal')
    parser.add_argument('--desktop-recovery', action='store_true')
    args = parser.parse_args()
    root = args.root.resolve()
    if root.exists(): raise ValueError('Fresh root required')
    out = REPO/'artifacts/magisk-lab'/('transaction-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f'))
    out.mkdir(parents=True)
    cli = REPO/'src/EmulatorWorkspace.Cli/bin/Release/net10.0/wsctl.exe'
    supervisor = REPO/'src/EmulatorWorkspace.Supervisor/bin/Release/net10.0-windows/EmulatorWorkspace.Supervisor.exe'
    env = dict(os.environ, EMULATORWORKSPACE_SUPERVISOR_EXE=str(supervisor))
    payloads = [cli, supervisor, supervisor.with_suffix('.dll'), supervisor.parent/'EmulatorWorkspace.Core.dll']
    (out/'payloads.json').write_text(json.dumps({str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in payloads}, indent=2))
    name, sequence, created, recovered, checks = 'transaction-lab', 0, False, False, []
    transaction = None

    def op(operation, payload=None, expected=None):
        nonlocal sequence
        sequence += 1
        print(operation, flush=True)
        request = out/'request.json'
        request.write_text(json.dumps(payload or {}))
        r = subprocess.run([str(cli),'call','--op',operation,'--args-file',str(request),'--root',str(root)],
                           env=env, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=650)
        log = {'op': operation, 'exitCode': r.returncode, 'stdout': r.stdout, 'stderr': r.stderr}
        # Approval credentials are consumed by this harness, not published in evidence.
        if operation == 'magisk.approve' and r.returncode == 0:
            clean = json.loads(r.stdout); clean['approval'] = '[redacted]'; log['stdout'] = json.dumps(clean)
        (out/f'{sequence:03d}-{operation}.json').write_text(json.dumps(log,indent=2),encoding='utf-8')
        if expected:
            assert r.returncode != 0 and '['+expected+']' in r.stderr, log
            checks.append({'check':operation+'-'+expected,'passed':True})
            return None
        if r.returncode: raise RuntimeError(r.stderr or r.stdout)
        if operation == 'supervisor.shutdown':
            deadline = time.monotonic() + 15
            while (root/'supervisor.lock').exists():
                try:
                    with (root/'supervisor.lock').open('rb'): break
                except PermissionError:
                    if time.monotonic() >= deadline: raise TimeoutError('Supervisor lock did not release')
                    time.sleep(0.2)
        return json.loads(r.stdout)

    def approve(action):
        return op('magisk.approve', {'name':name,'transaction':transaction,'action':action})['approval']

    try:
        create_args = {'name':name}
        if args.mode == 'crash-staging': create_args.update(console=5590, grpc=8590)
        op('instance.create', create_args); created = True
        old = REPO/'artifacts/magisk-lab/linux-20261113-110018'
        new = REPO/'artifacts/magisk-lab/linux-20261114-124924-baseonly'
        prepare_args = {'name':name,'patch':str(old/'patched-ramdisk.img'),
            'apk':str(old/'apk/Magisk-v30.7-e8a58776-release.apk'), 'probe':str(new/'suprobe/ws-suprobe.apk'),
            'allowProvisioningAdbRoot':True}
        if args.mode == 'timeout': prepare_args['bootTimeoutSeconds'] = 1
        if args.mode == 'crash-staging':
            import ctypes
            from ctypes import wintypes
            kernel = ctypes.WinDLL('kernel32', use_last_error=True)
            kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
            kernel.OpenProcess.restype = wintypes.HANDLE
            kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE,wintypes.DWORD,wintypes.LPWSTR,ctypes.POINTER(wintypes.DWORD)]
            kernel.TerminateProcess.argtypes = [wintypes.HANDLE,wintypes.UINT]
            kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE,wintypes.DWORD]
            kernel.CloseHandle.argtypes = [wintypes.HANDLE]
            identity = op('supervisor.status')
            handle = kernel.OpenProcess(0x100001 | 0x1000, False, identity['pid'])
            if not handle: raise ctypes.WinError(ctypes.get_last_error())
            try:
                exe = ctypes.create_unicode_buffer(32768); size = wintypes.DWORD(len(exe))
                assert kernel.QueryFullProcessImageNameW(handle,0,exe,ctypes.byref(size))
                assert Path(exe.value).resolve() == supervisor.resolve(), exe.value
                request = out/'crash-prepare-request.json'; request.write_text(json.dumps(prepare_args))
                child = subprocess.Popen([str(cli),'call','--op','magisk.prepare','--args-file',str(request),'--root',str(root)],
                    env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
                journal = root/'magisk-lab'/(hashlib.sha256(name.encode()).hexdigest()+'.json')
                deadline = time.monotonic()+90
                while not journal.exists():
                    if child.poll() is not None or time.monotonic()>deadline: raise RuntimeError('No staging journal appeared')
                    time.sleep(0.002)
                observed = json.loads(journal.read_text())
                assert observed['phase']=='staging', observed['phase']
                assert kernel.TerminateProcess(handle,99)
                assert kernel.WaitForSingleObject(handle,15000)==0
                stdout,stderr=child.communicate(timeout=30)
                (out/'crash-evidence.json').write_text(json.dumps({'pid':identity['pid'],'exe':exe.value,
                    'phaseBeforeTermination':observed['phase'],'cliExit':child.returncode,'stdout':stdout,'stderr':stderr},indent=2))
            finally: kernel.CloseHandle(handle)
            prepared = op('magisk.status', {'name':name})
            assert prepared['phase']=='staging', prepared['phase']
        elif args.mode == 'interrupted-staging':
            prepare_args['apk'] = str(out/'missing.apk')
            op('magisk.prepare', prepare_args, 'invalid_request')
            op('supervisor.shutdown')
            prepared = op('magisk.status', {'name':name})
            assert prepared['phase'] == 'staging'
        else:
            prepared = op('magisk.prepare', prepare_args)
        transaction = prepared['transaction']
        if args.mode in ('interrupted-staging', 'crash-staging'):
            op('instance.start', {'name':name}, 'capability_unavailable')
            restored = op('magisk.recover', {'name':name,'transaction':transaction,'approval':approve('recover')})
            assert restored['recoveryOutcome'] == 'staging_aborted_no_guest_changes'
            checks.append({'check':'partial-staging-restart-recovery','passed':True})
            recovered = True
            op('instance.delete', {'name':name}); created = False
            return 0
        op('magisk.install', {'name':name,'transaction':transaction,'approval':'bad'}, 'approval_required')
        op('instance.start', {'name':name}, 'capability_unavailable')
        token = approve('install')
        if args.mode == 'corrupt':
            patch = root/'magisk-lab'/transaction/'patched.img'
            patch.write_bytes(b'intentionally corrupt disposable staged patch')
            op('magisk.install', {'name':name,'transaction':transaction,'approval':token}, 'invalid_request')
            op('supervisor.shutdown')
            restored = op('magisk.recover', {'name':name,'transaction':transaction,'approval':approve('recover')})
            assert restored['recoveryOutcome'] == 'staging_aborted_no_guest_changes'
            checks.append({'check':'corrupt-patch-rejected-and-recovered','passed':True})
            recovered = True
            op('instance.delete', {'name':name}); created = False
            return 0
        if args.mode == 'timeout':
            op('magisk.install', {'name':name,'transaction':transaction,'approval':token}, 'adb_failure')
            restored = op('magisk.status', {'name':name})
            assert restored['phase'] == 'recovered' and restored['stockProof']['verified'], restored
            assert 'interactive readiness within 1s' in restored['failure'], restored
            checks.append({'check':'patched-boot-timeout-auto-rollback','passed':True})
            recovered = True
            op('instance.delete', {'name':name}); created = False
            return 0
        installed = op('magisk.install', {'name':name,'transaction':transaction,'approval':token})
        assert installed['phase'] == 'verified' and len(installed['boots']) == 2, installed
        root_proof = installed['adbProvisioning']
        assert root_proof['rootRequested'] and root_proof['root']['uidAfter'] == '0'
        assert root_proof['unrootAttempted'] and root_proof['unroot']['uidAfter'] == '2000'
        assert root_proof['adbdUid2000Verified'] is True
        assert all(b['appProof']['verified'] and b['appProof']['rootGranted'] and
                   b['appProof']['callerUid'] == installed['provisioning']['probeUid'] for b in installed['boots'])
        checks.append({'check':'journaled-adb-unroot-and-parsed-app-proofs','passed':True})
        checks.append({'check':'two-supervised-cold-boots','passed':True})
        op('magisk.install', {'name':name,'transaction':transaction,'approval':token}, 'approval_required')
        op('snapshot.load', {'name':name,'tag':'stock'}, 'capability_unavailable')
        if args.mode == 'data-recovery':
            # Only this fresh disposable guest, after install returned stopped.
            status = op('instance.status', {'name':name})
            assert status['state'].lower() == 'stopped', status
            overlays = list(root.rglob('userdata-qemu.img.qcow2'))
            overlays = [p for p in overlays if 'magisk-lab' not in p.parts]
            assert len(overlays) == 1, overlays
            overlays[0].write_bytes(b'intentionally corrupt disposable guest overlay')
        if args.desktop_recovery:
            (out/'desktop-ready.json').write_text(json.dumps({'root':str(root),'name':name,'transaction':transaction}))
            print('READY FOR DESKTOP RECOVERY: '+str(out), flush=True)
            journal = root/'magisk-lab'/(hashlib.sha256(name.encode()).hexdigest()+'.json')
            deadline = time.monotonic()+1000
            while json.loads(journal.read_text())['phase'] != 'recovered':
                if time.monotonic() > deadline: raise TimeoutError('Desktop recovery deadline')
                time.sleep(1)
            restored = op('magisk.status', {'name':name})
        else:
            token = approve('recover')
            restored = op('magisk.recover', {'name':name,'transaction':transaction,'approval':token})
        assert restored['phase'] == 'recovered' and restored['stockProof']['verified'], restored
        assert restored['recoveryAdbUnroot']['uidAfter'] == '2000'
        if args.mode != 'data-recovery':
            assert restored['stockProof']['appProof']['verified'] and not restored['stockProof']['appProof']['rootGranted']
        if args.mode == 'data-recovery': assert restored['dataRecovery'] == 'verified_baseline', restored
        recovered = True
        checks.append({'check':'stock-recovery-from-compatible-data-backup' if args.mode == 'data-recovery' else 'stock-recovery-with-app-denial','passed':True})
        op('instance.delete', {'name':name}); created = False
        assert not op('instance.list')['instances']
        checks.append({'check':'guest-cleanup','passed':True})
    except Exception as exc:
        checks.append({'check':'fatal','passed':False,'error':str(exc)});print(str(exc),flush=True)
    finally:
        if created:
            try:
                op('instance.stop', {'name':name,'force':True})
                state = op('magisk.status', {'name':name})
                if state['phase'] != 'recovered':
                    transaction = state['transaction']
                    token = approve('recover')
                    state = op('magisk.recover', {'name':name,'transaction':transaction,'approval':token})
                if state['phase'] == 'recovered':op('instance.delete', {'name':name})
            except Exception as exc:checks.append({'check':'cleanup','passed':False,'error':str(exc)})
        for log in (root/'logs').glob('*'):
            if log.is_file():shutil.copy2(log,out/log.name)
        try:op('supervisor.shutdown')
        except Exception as exc:checks.append({'check':'shutdown','passed':False,'error':str(exc)})
        (out/'request.json').unlink(missing_ok=True)
        (out/'summary.json').write_text(json.dumps({'root':str(root),'mode':args.mode,'checks':checks,
            'passed':all(c['passed'] for c in checks)},indent=2))
        print('Evidence: '+str(out),flush=True)
        if not all(c['passed'] for c in checks):
            raise SystemExit(1)
    return 0 if all(c['passed'] for c in checks) else 1


if __name__ == '__main__':raise SystemExit(main())
