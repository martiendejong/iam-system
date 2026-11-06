"""Deploy the IAM system to the dev server (maendeleo.martiendejong.nl/auth).

Secrets live in service-scoped environment variables and the Jengo vault, NOT in
appsettings.Production.json — see docs/DEPLOYMENT-SECRETS.md (task 5801). This script
therefore never uploads or touches appsettings*.json.

Steps: build admin-ui + publish backend (needs the Hazina checkout), stop the
IAMSystem service, make a server-side backup of the stopped binaries, upload,
start, smoke-check. Usage: python deploy-iam.py [--skip-build]
"""
import importlib.util
import os
import subprocess
import sys
import time
from datetime import date
from pathlib import Path

import paramiko
import requests

ROOT = Path(__file__).parent
HAZINA_ROOT = os.environ.get("HAZINA_ROOT", r"E:\projects\hazina-develop-wt" + "\\")
REMOTE_DIR = "C:/Services/IAM"
SERVICE = "IAMSystem"
SKIP_UPLOAD = {"appsettings.json", "appsettings.development.json", "appsettings.production.json"}
SMOKE = [
    ("https://maendeleo.martiendejong.nl/auth/", 200),
    ("https://maendeleo.martiendejong.nl/auth/api/users/me", 401),
    ("https://maendeleo.martiendejong.nl/auth/.well-known/openid-configuration", 200),
]
# Env vars the service must have (values live in the vault, project 6 — see docs).
REQUIRED_ENV = [
    "ConnectionStrings__DefaultConnection", "Jwt__SecretKey", "Email__SmtpPassword",
    "SecretsVault__MasterKey", "OpenIddict__SigningCertificatePassword",
    "OpenIddict__EncryptionCertificatePassword", "Clients__JengoVpsMcp__Secret",
    "Clients__OpenWebUi__Secret", "Ca__CertificatePassword",
]


def _vault_dev_ssh():
    spec = importlib.util.spec_from_file_location(
        "vault_api", r"E:\projects\jengo\jengo-system-private\tools\vault-api.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    cred = mod._fetch_credential(6, 50, reason="deploy-iam.py: deploy IAM to dev server")
    return "212.227.6.226", "administrator", cred["password"]


def build():
    print("== build admin-ui ==")
    subprocess.run("npm run build", cwd=ROOT / "admin-ui", shell=True, check=True)
    print("== publish backend ==")
    env = dict(os.environ, HAZINA_ROOT=HAZINA_ROOT)
    subprocess.run(
        "dotnet publish src/IAM.API -c Release -o publish_out -v q",
        cwd=ROOT, shell=True, check=True, env=env)


def run(ssh, cmd, timeout=300):
    _i, o, e = ssh.exec_command(cmd, timeout=timeout)
    return (o.read().decode(errors="replace").replace("\x00", ""),
            e.read().decode(errors="replace").replace("\x00", ""))


def main():
    if "--skip-build" not in sys.argv:
        build()

    host, user, password = _vault_dev_ssh()
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(host, username=user, password=password, timeout=30)
    sftp = ssh.open_sftp()

    print("== required env vars present on the service? ==")
    o, _ = run(ssh, 'powershell -NoProfile -Command "'
                    "(Get-ItemProperty HKLM:\\SYSTEM\\CurrentControlSet\\Services\\IAMSystem)"
                    ".Environment -join [char]10" + '"')
    names = {l.split("=", 1)[0] for l in o.strip().split("\n") if "=" in l}
    missing = [n for n in REQUIRED_ENV if n not in names]
    if missing:
        raise SystemExit(f"ABORT: service env vars missing (set from vault first): {missing}")
    print(f"all {len(REQUIRED_ENV)} present")

    print("== stop service ==")
    run(ssh, f"sc stop {SERVICE}")
    time.sleep(5)

    stamp = date.today().strftime("%Y%m%d")
    print("== backup stopped binaries ==")
    run(ssh, 'powershell -NoProfile -Command "Compress-Archive -Force '
             f"-Path {REMOTE_DIR}/* -DestinationPath C:/Services/IAM-backup-{stamp}.zip\"",
        timeout=600)

    print("== upload ==")
    local = ROOT / "publish_out"
    count = 0
    for root, _dirs, files in os.walk(local):
        rel = os.path.relpath(root, local)
        rr = REMOTE_DIR if rel == "." else REMOTE_DIR + "/" + rel.replace("\\", "/")
        try:
            sftp.stat(rr)
        except FileNotFoundError:
            sftp.mkdir(rr)
        for f in files:
            if rel == "." and f.lower() in SKIP_UPLOAD:
                continue
            sftp.put(os.path.join(root, f), rr + "/" + f)
            count += 1
    print(f"uploaded {count} files (appsettings untouched)")

    print("== start service ==")
    run(ssh, f"sc start {SERVICE}")
    time.sleep(12)
    o, _ = run(ssh, f"sc query {SERVICE} | findstr STATE")
    print("state:", o.strip())
    sftp.close()
    ssh.close()

    print("== smoke ==")
    ok = True
    for url, expected in SMOKE:
        code = requests.get(url, timeout=20, allow_redirects=False).status_code
        mark = "OK" if code == expected else "FAIL"
        if code != expected:
            ok = False
        print(f"  {mark} {code} (want {expected}) {url}")
    if not ok:
        raise SystemExit("Smoke failed — check Windows Event Log on the server "
                         "(providers '.NET Runtime'/'Application Error'); restore from the backup zip if needed.")
    print("Deploy done.")


if __name__ == "__main__":
    main()
