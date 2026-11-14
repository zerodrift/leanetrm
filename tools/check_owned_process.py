"""Exercise real owned child/grandchild teardown without a database or browser."""
import errno
import subprocess
import sys
from unittest import mock
from owned_process import live_group_members, stop_owned_group, stop_owned_groups

# macOS refuses to signal a process group whose remaining members are already exiting.
EXITING = PermissionError(errno.EPERM, 'Operation not permitted')


def check(label, code, exits_first=False, refused=False):
    process = subprocess.Popen([sys.executable, '-u', '-c', code],
                               stdout=subprocess.PIPE, text=True, start_new_session=True)
    try:
        assert process.stdout.readline().strip() == 'ready'
        if exits_first:
            process.wait(timeout=5)
            assert live_group_members(process.pid), 'Grandchild must outlive its parent for this check'
        if refused:
            with mock.patch('owned_process.os.killpg', side_effect=EXITING):
                stop_owned_group(process, grace_seconds=3)
        else:
            stop_owned_group(process, grace_seconds=.3)
        assert not live_group_members(process.pid)
        print('PASS '+label)
    finally:
        stop_owned_group(process, grace_seconds=.3)
        process.stdout.close()


def check_refused_survivor():
    process = subprocess.Popen([sys.executable, '-u', '-c', 'import time;print("ready");time.sleep(60)'],
                               stdout=subprocess.PIPE, text=True, start_new_session=True)
    try:
        assert process.stdout.readline().strip() == 'ready'
        with mock.patch('owned_process.os.killpg', side_effect=EXITING):
            try:
                stop_owned_group(process, grace_seconds=.3)
            except RuntimeError:
                pass
            else:
                raise AssertionError('A group that outlives refused signals must not be reported stopped')
        assert live_group_members(process.pid)
        print('PASS a group that outlives refused signals is reported, not assumed stopped')
    finally:
        stop_owned_group(process, grace_seconds=.3)
        process.stdout.close()


def check_partial_cleanup():
    groups=[subprocess.Popen([sys.executable,'-u','-c','import time;print("ready");time.sleep(60)'],
        stdout=subprocess.PIPE,text=True,start_new_session=True)for _ in range(3)]
    remaining=groups[:];signal_group=__import__('os').killpg
    try:
        for process in groups:assert process.stdout.readline().strip()=='ready'
        def selective(group,signal):
            if group==groups[1].pid:raise EXITING
            return signal_group(group,signal)
        with mock.patch('owned_process.os.killpg',side_effect=selective):
            try:stop_owned_groups(remaining,grace_seconds=.3)
            except ExceptionGroup as error:assert len(error.exceptions)==1
            else:raise AssertionError('Incomplete cleanup was reported successful')
        assert remaining==[groups[1]],'Every stopped group must be removed, and an unconfirmed group must retain its owned files'
        assert not live_group_members(groups[0].pid) and live_group_members(groups[1].pid) and not live_group_members(groups[2].pid)
        print('PASS partial cleanup stops every other owned group and retains the exact unconfirmed owner')
    finally:
        for process in groups:stop_owned_group(process,grace_seconds=.3);process.stdout.close()


if __name__ == '__main__':
    check('normal group termination', 'import time;print("ready");time.sleep(60)')
    check('SIGTERM-resistant descendants are awaited after SIGKILL',
          'import signal,subprocess,sys,time;signal.signal(signal.SIGTERM,signal.SIG_IGN);'
          'subprocess.Popen([sys.executable,"-c","import signal,time;signal.signal(signal.SIGTERM,signal.SIG_IGN);time.sleep(60)"]);'
          'print("ready");time.sleep(60)')
    check('orphaned descendants stop even after the launcher exits',
          'import subprocess,sys;subprocess.Popen([sys.executable,"-c","import time;time.sleep(60)"]);print("ready")', True)
    check('members that refuse signals while exiting are awaited until they are gone',
          'import subprocess,sys,time;subprocess.Popen([sys.executable,"-c","import time;time.sleep(.5)"]);print("ready");time.sleep(.5)',
          refused=True)
    check_refused_survivor()
    check_partial_cleanup()
    print('6 owned-process cleanup checks passed.')
