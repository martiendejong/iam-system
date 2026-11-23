#!/usr/bin/env python3
"""Plan and run IAM releases with automatic backup, integration test and rollback.

  release.py stage    <target> [--src DIR]        build image on the target's builder, upload release bundle
  release.py schedule <target> <ID> --at "02:30"  run the staged release unattended via `at` (server-side)
  release.py run      <target> <ID>               run the staged release now (foreground log tail)
  release.py status   <target> [ID]               status + log of a release
  release.py cancel   <target> <ID>               remove the scheduled job

Targets: test, acc, prod. The release itself runs ON the server (release.sh), so it does not
depend on this machine staying on. On any failed step after the swap, the previous image is restored.
Stage never touches the live service. SQL files in docs/manual-migrations newer than --since go along.
"""
import argparse, io, os, subprocess, sys, tarfile, time
import paramiko

KEY = r"C:/Users/HP/.ssh/id_ed25519"
TARGETS = {
    "test": dict(host="46.224.114.149", stack="/opt/iam-test", project="iam-test", db="iam-test-db",
                 url="https://iam-test.martiendejong.nl", base_path="/", builder=None),
    "acc":  dict(host="46.224.114.149", stack="/opt/iam", project="iam", db="iam-db-1",
                 url="https://iam-acc.martiendejong.nl", base_path="/", builder=None),
    "prod": dict(host="46.62.219.14", stack="/opt/iam-prod", project="iam-prod", db="iam-prod-db-1",
                 url="https://maendeleo.martiendejong.nl/auth", base_path="/auth", builder="46.224.114.149"),
}


def conn(host):
    c = paramiko.SSHClient(); c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    c.connect(host, username="root", key_filename=KEY, timeout=30)
    return c


def run(c, cmd, timeout=1800, check=True):
    _i, o, e = c.exec_command(cmd, timeout=timeout)
    out = o.read().decode(errors="replace"); err = e.read().decode(errors="replace")
    rc = o.channel.recv_exit_status()
    if check and rc != 0:
        raise SystemExit(f"command failed ({rc}): {cmd}\n{out}\n{err}")
    return out + err


def source_tar(src):
    buf = io.BytesIO()
    skip = {".git", "node_modules", "bin", "obj", "publish_out", "dist"}
    with tarfile.open(fileobj=buf, mode="w:gz") as t:
        for root, dirs, files in os.walk(src):
            dirs[:] = [d for d in dirs if d not in skip]
            for f in files:
                p = os.path.join(root, f)
                t.add(p, arcname=os.path.relpath(p, src))
    buf.seek(0); return buf


def head_id(src):
    return subprocess.check_output(["git", "-C", src, "rev-parse", "--short", "HEAD"], text=True).strip()


def stage(a):
    t = TARGETS[a.target]; src = os.path.abspath(a.src)
    rid = time.strftime("%Y%m%d-%H%M") + "-" + head_id(src)
    image = f"iam-system:rel-{rid}"
    b = conn(t["builder"] or t["host"])
    print(f"release id {rid}; building {image} on {t['builder'] or t['host']} (VITE_BASE_PATH={t['base_path']})")
    s = b.open_sftp(); s.putfo(source_tar(src), "/tmp/iam-src.tgz")
    run(b, f"rm -rf /tmp/iam-build && mkdir /tmp/iam-build && tar xzf /tmp/iam-src.tgz -C /tmp/iam-build && "
           f"cd /tmp/iam-build && nice -n 10 docker build --build-arg VITE_BASE_PATH={t['base_path']} -t {image} . > /tmp/iam-build-{rid}.log 2>&1; "
           f"tail -2 /tmp/iam-build-{rid}.log; docker image inspect {image} >/dev/null", timeout=3600)
    d = conn(t["host"])
    if t["builder"]:
        free = int(run(d, "df -Pm / | awk 'NR==2{print $4}'").strip())
        if free < 1200:
            raise SystemExit(f"STOP: target has only {free}MB free; free up disk before shipping the image")
        print("shipping image to target ...")
        # relay through this machine so the servers need no keys to each other
        _i, so, _e = b.exec_command(f"docker save {image} | gzip", timeout=3600)
        si, _o, se = d.exec_command("gunzip | docker load", timeout=3600)
        total = 0
        while True:
            chunk = so.channel.recv(1 << 20)
            if not chunk: break
            si.write(chunk); total += len(chunk)
        si.channel.shutdown_write()
        print(f"shipped {total // 1048576} MB;", _o.read().decode().strip()[-200:], se.read().decode()[-200:])
    base = f"/opt/releases/{rid}"
    run(d, f"mkdir -p {base}/sql")
    ds = d.open_sftp()
    sqldir = os.path.join(src, "docs", "manual-migrations")
    for f in sorted(os.listdir(sqldir)) if os.path.isdir(sqldir) else []:
        if f.endswith(".sql") and f >= a.since:
            ds.put(os.path.join(sqldir, f), f"{base}/sql/{f}"); print("sql:", f)
    here = os.path.dirname(os.path.abspath(__file__))
    with ds.open(f"{base}/release.sh", "wb") as fh:  # force LF: a CRLF checkout would break bash on the server
        fh.write(open(os.path.join(here, "release.sh"), "rb").read().replace(b"\r\n", b"\n"))
    env = (f"STACK_DIR={t['stack']}\nPROJECT={t['project']}\nDB_CONTAINER={t['db']}\nAPI_SERVICE=api\n"
           f"BASE_URL={t['url']}\nNEW_IMAGE={image}\nBACKUP_DIR=/opt/backups\nMIN_FREE_MB=1500\n")
    with ds.open(f"{base}/release.env", "w") as fh: fh.write(env)
    run(d, f"chmod +x {base}/release.sh; echo STAGED > {base}/status")
    print(f"staged. schedule with:  release.py schedule {a.target} {rid} --at 02:30")


def schedule(a):
    d = conn(TARGETS[a.target]["host"])
    print(run(d, f"echo '/opt/releases/{a.id}/release.sh {a.id}' | at {a.at} 2>&1; echo SCHEDULED > /opt/releases/{a.id}/status; atq"))


def now(a):
    d = conn(TARGETS[a.target]["host"])
    run(d, f"/opt/releases/{a.id}/release.sh {a.id}", check=False)
    status(a)


def status(a):
    d = conn(TARGETS[a.target]["host"])
    rid = a.id or run(d, "ls -1t /opt/releases | head -1").strip()
    print(run(d, f"echo release {rid}: $(cat /opt/releases/{rid}/status); tail -40 /opt/releases/{rid}/release.log 2>/dev/null; atq"))


def cancel(a):
    d = conn(TARGETS[a.target]["host"])
    for line in run(d, "atq").splitlines():
        jid = line.split()[0]
        if a.id in run(d, f"at -c {jid}", check=False):
            run(d, f"atrm {jid}; echo CANCELLED > /opt/releases/{a.id}/status"); print("cancelled job", jid)


if __name__ == "__main__":
    p = argparse.ArgumentParser(); sp = p.add_subparsers(dest="cmd", required=True)
    for n, fn in [("stage", stage), ("schedule", schedule), ("run", now), ("status", status), ("cancel", cancel)]:
        q = sp.add_parser(n); q.set_defaults(fn=fn); q.add_argument("target", choices=TARGETS)
        if n in ("schedule", "run", "cancel"): q.add_argument("id")
        if n == "status": q.add_argument("id", nargs="?")
        if n == "schedule": q.add_argument("--at", required=True)
        if n == "stage":
            q.add_argument("--src", default=".")
            q.add_argument("--since", default="", help="only SQL files with a name >= this")
    a = p.parse_args(); a.fn(a)
