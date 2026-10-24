"""Teardown for a subprocess explicitly started with start_new_session=True."""
import os
import signal
import subprocess
import time


def live_group_members(group):
    listing = subprocess.run(['ps', '-axo', 'pid=,pgid=,stat='], text=True,
                             capture_output=True, check=True, timeout=5)
    return [int(parts[0]) for line in listing.stdout.splitlines()
            if len(parts := line.split()) == 3 and int(parts[1]) == group
            and not parts[2].startswith('Z')]


def stop_owned_group(process, grace_seconds=3):
    # The caller creates a new session whose group ID is this exact child PID.
    # Refuse the caller's process group; never accept an arbitrary PID from a user.
    group = process.pid
    if group <= 1 or group == os.getpgrp():
        raise RuntimeError('Refusing an unowned process group')
    for action in [signal.SIGTERM, signal.SIGKILL]:
        if not live_group_members(group):
            process.wait(timeout=grace_seconds)
            return
        try:
            os.killpg(group, action)
        except (ProcessLookupError, PermissionError):
            # macOS refuses to signal a group whose remaining members are already exiting;
            # whether every member is gone is decided below, as after any signal.
            pass
        deadline = time.monotonic() + grace_seconds
        while time.monotonic() < deadline:
            if not live_group_members(group):
                process.wait(timeout=grace_seconds)
                return
            time.sleep(.05)
    raise RuntimeError('Owned browser process group did not stop; retain its files')
