"""Windows boot-input feasibility probe, not transactional product acceptance.

Uses the supervisor for lifecycle, APK install, instrumentation and ADB-root
transitions. Explicitly scoped ADB commands provision the disposable guest and
collect independent evidence. No upstream test APK or module setup is executed.
"""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time

REPO = Path(__file__).resolve().parents[1]


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--allow-provisioning-adb-root', action='store_true', required=True)
    args = parser.parse_args()
    root = args.root.resolve()
    if root.exists():
        raise ValueError('A fresh disposable workspace root is required')
    base = REPO / 'artifacts/magisk-lab'
    old = base / 'linux-20261113-110018'
    new = base / 'linux-20261114-124924-baseonly'
    stage = base / 'windows-baseonly-staged'
    stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S')
    out = base / ('windows-baseonly-' + stamp)
    out.mkdir(parents=True)
    cli = REPO / 'src/EmulatorWorkspace.Cli/bin/Release/net10.0/wsctl.exe'
    supervisor = REPO / 'src/EmulatorWorkspace.Supervisor/bin/Release/net10.0-windows/EmulatorWorkspace.Supervisor.exe'
    sdk = Path(os.environ['ANDROID_HOME'])
    adb = sdk / 'platform-tools/adb.exe'
    env = dict(os.environ, EMULATORWORKSPACE_SUPERVISOR_EXE=str(supervisor))
    name = 'magisk-baseonly'
    package = 'dev.emulatorworkspace.suprobe'
    checks, sequence, created = [], 0, False
    record = None

    def save(name, data):
        (out / name).write_text(json.dumps(data, indent=2), encoding='utf-8')

    def check(label, condition):
        checks.append({'check': label, 'passed': bool(condition)})
        print(('PASS ' if condition else 'FAIL ') + label, flush=True)
        if not condition:
            raise RuntimeError(label)

    def run(label, command, timeout=90, allow=False):
        nonlocal sequence
        sequence += 1
        filename = f'{sequence:03d}-{label}.json'
        print(label, flush=True)
        command = [str(x) for x in command]
        start = time.monotonic()
        try:
            result = subprocess.run(command, env=env, capture_output=True,
                                    text=True, encoding='utf-8', errors='replace', timeout=timeout)
            save(filename, {'argv': command, 'exitCode': result.returncode,
                            'seconds': time.monotonic()-start, 'stdout': result.stdout, 'stderr': result.stderr})
        except subprocess.TimeoutExpired as exc:
            save(filename, {'argv': command, 'timeoutSeconds': timeout,
                            'stdout': str(exc.stdout), 'stderr': str(exc.stderr)})
            raise
        if result.returncode and not allow:
            raise RuntimeError(f'{label}: {result.returncode}: {result.stdout[-1000:]} {result.stderr[-1000:]}')
        return result

    def op(operation, payload=None, label=None):
        label = label or operation
        path = out / 'request.json'
        path.write_text(json.dumps(payload or {}), encoding='utf-8')
        result = run(label, [cli, 'call', '--op', operation, '--args-file', path, '--root', root], 620)
        return json.loads(result.stdout)

    def identity(label):
        ident = op('instance.adbIdentity', {'name': name}, label+'-identity')
        transport = str(ident['transportId'])
        check(label+'-owned-pid', ident['enginePid'] == record['pid'] and
              ident['engineStartUtc'] == record['engineStartUtc'])
        serial = run(label+'-serial', [adb, '-t', transport, 'get-serialno']).stdout.strip()
        check(label+'-serial', serial == record['serial'])
        avd = run(label+'-avd', [adb, '-t', transport, 'emu', 'avd', 'name']).stdout.splitlines()[0]
        check(label+'-avd', avd == record['avdName'])
        return transport

    def shell(label, command, timeout=90, allow=False):
        transport = identity(label)
        return run(label, [adb, '-t', transport, 'shell', command], timeout, allow)

    def boot(label, ramdisk):
        nonlocal record
        op('instance.start', {'name': name, 'gpu': 'swiftshader_indirect', 'headless': True,
                             'coldBoot': True, 'ramdisk': str(ramdisk)}, label+'-start')
        op('instance.wait', {'name': name, 'timeoutSeconds': 360}, label+'-wait')
        record = op('instance.status', {'name': name}, label+'-status')
        check(label+'-boot', record['bootCompleted'].strip() == '1')
        check(label+'-image', record['fingerprint'] == 'Android/sdk_phone64_x86_64/emu64x:15/AE3A.240806.019/12368160:userdebug/test-keys')

    def poweroff(label):
        shell(label+'-sync', 'sync')
        shell(label+'-poweroff', 'reboot -p', allow=True)
        for _ in range(60):
            state = op('instance.status', {'name': name}, label+'-shutdown-state')
            if not state['engineAlive']:
                op('instance.stop', {'name': name}, label+'-reconcile-stop')
                return
            time.sleep(2)
        raise RuntimeError('Graceful shutdown deadline exceeded')

    def probe(label, patched, uid):
        identity(label+'-probe')
        result = op('app.instrumentation', {'name': name, 'package': package,
                    'runner': package+'/.SuProbe'}, label+'-app-probe')
        text = result['output']
        check(label+'-app-uid', f'caller_uid={uid}' in text and 'caller_context=u:r:untrusted_app:' in text)
        check(label+'-app-root', ('su_exit=0' in text and 'uid=0(root)' in text and 'context=u:r:magisk:s0' in text)
              if patched else ('su_exit=exception' in text and 'error=13' in text))

    def evidence(label):
        result = shell(label+'-shell', 'id; magisk -V; su -c id; echo su_exit=$?; '
                       'ps -A -o USER,UID,PID,NAME; cat /data/local/tmp/ws-magisk-marker')
        text = result.stdout
        check(label+'-shell-root', text.startswith('uid=2000(shell)') and '\n30700\n' in text
              and 'uid=0(root)' in text and 'su_exit=0' in text)
        check(label+'-adbd-unrooted', re.search(r'shell\s+2000\s+\d+\s+adbd', text))
        check(label+'-marker', 'ws-magisk-windows-baseonly-v1' in text)
        inventory = '''su -c 'echo MODULES; find /data/adb/modules /data/adb/modules_update /data/adb/post-fs-data.d /data/adb/service.d -mindepth 1 2>/dev/null; echo SETTINGS; magisk --sqlite "SELECT * FROM settings"; echo POLICIES; magisk --sqlite "SELECT * FROM policies"; echo ZYGOTE; for p in $(pidof zygote64); do grep -i zygisk /proc/$p/maps; done; echo HOSTS; grep " /system/etc/hosts " /proc/mounts; echo END' '''
        text = shell(label+'-inventory', inventory).stdout
        check(label+'-empty-modules', text.split('MODULES\n')[1].split('SETTINGS')[0].strip() == '')
        check(label+'-zygisk-off', 'key=zygisk|value=0' in text and text.split('ZYGOTE\n')[1].split('HOSTS')[0].strip() == '')
        check(label+'-no-hosts-mount', text.split('HOSTS\n')[1].split('END')[0].strip() == '')
        shell(label+'-logcat', 'logcat -d -s Magisk:V magiskinit:V')

    try:
        pins = {old/'stock-ramdisk.img': '60eb16b9c894514f3792c5bef9f58ea478bd1356f6e0fdb8045dca01ab903a30',
                old/'patched-ramdisk.img': 'd5b47070ee8a63bcb80e7851887831e371d55762cec388195552ac09426faf8f',
                old/'apk/Magisk-v30.7-e8a58776-release.apk': 'd820319e907a16eecd3239be360def9299b351da1155736ac9a28966092dfd7e',
                new/'suprobe/ws-suprobe.apk': 'a6b4837f101c2423d32f801ca6951901f3fb66e2584ad0d59296956bed1e23b1'}
        for path, expected in pins.items():
            check('pin-'+path.name, sha(path) == expected)
        for line in (new/'procedure/staged.sha256').read_text().splitlines():
            expected, rel = line.split(maxsplit=1)
            check('stage-'+rel, sha(stage/rel) == expected)
        version = run('engine-version', [sdk/'emulator/emulator.exe', '-version']).stdout
        check('engine-pin', '37.2.12.0' in version and '16428233' in version)
        save('provenance.json', {'cli': str(cli), 'cliSha256': sha(cli), 'supervisor': str(supervisor),
             'supervisorSha256': sha(supervisor), 'temporaryAdbRootApproved': True,
             'source': subprocess.check_output(['git','rev-parse','HEAD'], text=True).strip()})
        op('doctor', label='doctor')
        op('instance.create', {'name': name})
        created = True
        state = op('supervisor.status')
        ps = run('supervisor-process', ['powershell', '-NoProfile', '-Command',
            f'(Get-Process -Id {int(state["pid"])}).Path']).stdout.strip()
        check('release-supervisor', os.path.normcase(ps) == os.path.normcase(str(supervisor)))
        boot('provision', old/'patched-ramdisk.img')
        shell('marker', 'echo ws-magisk-windows-baseonly-v1 > /data/local/tmp/ws-magisk-marker; sync')
        op('app.install', {'name': name, 'path': str(old/'apk/Magisk-v30.7-e8a58776-release.apk')})
        op('app.install', {'name': name, 'path': str(new/'suprobe/ws-suprobe.apk')})
        uidtext = shell('probe-package', 'pm list packages -U '+package).stdout
        uid = int(re.fullmatch(r'package:dev\.emulatorworkspace\.suprobe uid:(\d+)\s*', uidtext).group(1))
        check('probe-unprivileged-uid', uid >= 10000)
        transport = identity('stage-push')
        run('stage-push', [adb, '-t', transport, 'push', str(stage)+ '/.', '/data/local/tmp/ws-magisk-env/'])
        rootstate = op('instance.adbRoot', {'name': name, 'root': True}, 'provision-adb-root')
        check('provision-adb-root', rootstate['uidAfter'] == '0')
        setup = '''set -e
test "$(id -u)" = 0
test ! -e /data/adb/magisk
mkdir -p /data/adb/magisk
chmod 700 /data/adb
cp -r /data/local/tmp/ws-magisk-env/. /data/adb/magisk/
chmod -R 755 /data/adb/magisk
chown -R 0:0 /data/adb/magisk
/debug_ramdisk/magisk --sqlite 'REPLACE INTO settings (key,value) VALUES("zygisk",0)'
/debug_ramdisk/magisk --sqlite 'REPLACE INTO policies (uid,policy,until,logging,notification) VALUES(2000,2,0,0,0)'
/debug_ramdisk/magisk --sqlite 'REPLACE INTO policies (uid,policy,until,logging,notification) VALUES(PROBEUID,2,0,0,0)'
cd /data/adb/magisk
find . -type f | sort | xargs sha256sum
sync
'''.replace('PROBEUID', str(uid))
        installed = shell('base-only-setup', setup).stdout
        for line in (new/'procedure/staged.sha256').read_text().splitlines():
            expected, rel = line.split(maxsplit=1)
            check('guest-stage-'+rel, expected+'  '+rel in installed)
        unroot = op('instance.adbRoot', {'name': name, 'root': False}, 'provision-adb-unroot')
        check('provision-unroot', unroot['uidAfter'] == '2000')
        poweroff('provision')
        for number in (1,2):
            label = 'patched-'+str(number)
            boot(label, old/'patched-ramdisk.img')
            evidence(label)
            probe(label, True, uid)
            poweroff(label)
        boot('stock-restore', old/'stock-ramdisk.img')
        text = shell('stock-proof', 'id; su -c id; echo su_exit=$?; '
                     'ps -A -o USER,UID,PID,NAME; command -v magisk; cat /data/local/tmp/ws-magisk-marker').stdout
        check('stock-no-magisk', 'magiskd' not in text and 'su_exit=1' in text)
        check('stock-marker', 'ws-magisk-windows-baseonly-v1' in text)
        check('stock-adbd-shell', re.search(r'shell\s+2000\s+\d+\s+adbd', text))
        probe('stock', False, uid)
        poweroff('stock')
        check('stock-input-preserved', sha(old/'stock-ramdisk.img') == pins[old/'stock-ramdisk.img'])
    except Exception as exc:
        checks.append({'check': 'fatal', 'passed': False, 'error': str(exc)})
        print(str(exc), flush=True)
    finally:
        if created:
            try:
                op('instance.stop', {'name': name, 'force': True}, 'cleanup-stop')
                for log in (root/'logs').glob('*'):
                    if log.is_file(): shutil.copy2(log, out/log.name)
                for config in (root/'avd').glob('*.avd/config.ini'):
                    shutil.copy2(config, out/'avd-config.ini')
                op('instance.delete', {'name': name}, 'cleanup-delete')
                remaining = op('instance.list', label='cleanup-list')
                check('cleanup-empty', not remaining['instances'])
            except Exception as exc:
                checks.append({'check': 'cleanup', 'passed': False, 'error': str(exc)})
        try: op('supervisor.shutdown', label='cleanup-supervisor')
        except Exception as exc: checks.append({'check':'shutdown','passed':False,'error':str(exc)})
        save('summary.json', {'root':str(root), 'checks':checks, 'passed':all(c['passed'] for c in checks)})
        print('Evidence: '+str(out), flush=True)
    return 0 if all(c['passed'] for c in checks) else 1


if __name__ == '__main__':
    raise SystemExit(main())
