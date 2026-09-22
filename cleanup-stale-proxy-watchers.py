#!/usr/bin/python3
"""Release orphan VS Code proxy watchers; never stop live editor sessions."""
import os
from pathlib import Path
import signal


def cleanup():
    """Terminate same-user singleton orphan watcher scopes; return their count."""
    count = 0
    for proc in Path('/proc').iterdir():
        handle = None
        try:
            if not proc.name.isdigit() or proc.stat().st_uid != os.getuid():
                continue
            expected = b'dconf\0watch\0/system/proxy/'
            if (proc / 'cmdline').read_bytes().rstrip(b'\0') != expected:
                continue
            handle = os.pidfd_open(int(proc.name))
            status = (proc / 'status').read_text().splitlines()
            parent = next(line.split()[1] for line in status if line.startswith('PPid:'))
            if (Path('/proc') / parent / 'comm').read_text().strip() != 'systemd':
                continue
            group = (proc / 'cgroup').read_text().strip().split('::', 1)[1]
            if not Path(group).name.startswith('app-code-'):
                continue
            members = (Path('/sys/fs/cgroup') / group.lstrip('/') / 'cgroup.procs').read_text().split()
            if members != [proc.name]:
                continue
            # A pidfd targets the verified process even if its numeric PID is later reused.
            signal.pidfd_send_signal(handle, signal.SIGTERM)
            count += 1
        except (OSError, ValueError, IndexError, StopIteration):
            continue
        finally:
            if handle is not None:
                os.close(handle)
    return count


if __name__ == '__main__':
    print(f'Released {cleanup()} orphan VS Code proxy watcher(s).')
