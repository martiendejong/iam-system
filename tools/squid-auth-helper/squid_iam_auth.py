#!/usr/bin/env python3
"""
Jengo IAM basic-auth helper for Squid proxy.

Protocol: Squid writes "username password\n" on stdin, expects "OK\n" or "ERR\n" on stdout.
Config: environment variables or squid_iam_auth.conf next to this script.

Required env vars (or config file keys):
  IAM_TOKEN_URL     e.g. https://maendeleo.martiendejong.nl/auth/connect/token
  IAM_CLIENT_ID     the registered client_id (e.g. squid-proxy)
  IAM_CLIENT_SECRET the client secret

Optional:
  IAM_SCOPE         space-separated scopes (default: openid)
  IAM_TIMEOUT       HTTP timeout in seconds (default: 10)
"""

import sys
import os
import urllib.request
import urllib.parse
import urllib.error
import json
import configparser
import pathlib


def load_config():
    cfg = {
        'IAM_TOKEN_URL': os.environ.get('IAM_TOKEN_URL', ''),
        'IAM_CLIENT_ID': os.environ.get('IAM_CLIENT_ID', ''),
        'IAM_CLIENT_SECRET': os.environ.get('IAM_CLIENT_SECRET', ''),
        'IAM_SCOPE': os.environ.get('IAM_SCOPE', 'openid'),
        'IAM_TIMEOUT': int(os.environ.get('IAM_TIMEOUT', '10')),
    }

    # Try config file next to this script
    conf_path = pathlib.Path(__file__).parent / 'squid_iam_auth.conf'
    if conf_path.exists():
        p = configparser.ConfigParser()
        p.read(str(conf_path))
        if p.has_section('iam'):
            for key in ('IAM_TOKEN_URL', 'IAM_CLIENT_ID', 'IAM_CLIENT_SECRET', 'IAM_SCOPE'):
                if p.has_option('iam', key) and not os.environ.get(key):
                    cfg[key] = p.get('iam', key)
            if p.has_option('iam', 'IAM_TIMEOUT') and not os.environ.get('IAM_TIMEOUT'):
                cfg['IAM_TIMEOUT'] = int(p.get('iam', 'IAM_TIMEOUT'))

    return cfg


def validate_user(cfg, username, password):
    data = urllib.parse.urlencode({
        'grant_type': 'password',
        'username': username,
        'password': password,
        'client_id': cfg['IAM_CLIENT_ID'],
        'client_secret': cfg['IAM_CLIENT_SECRET'],
        'scope': cfg['IAM_SCOPE'],
    }).encode('utf-8')

    req = urllib.request.Request(
        cfg['IAM_TOKEN_URL'],
        data=data,
        headers={'Content-Type': 'application/x-www-form-urlencoded'},
        method='POST',
    )

    try:
        with urllib.request.urlopen(req, timeout=cfg['IAM_TIMEOUT']) as resp:
            return resp.status == 200
    except urllib.error.HTTPError:
        return False
    except Exception as e:
        print(f"ERR {e}", file=sys.stderr)
        return False


def main():
    cfg = load_config()
    if not cfg['IAM_TOKEN_URL'] or not cfg['IAM_CLIENT_ID']:
        print("ERR IAM_TOKEN_URL and IAM_CLIENT_ID must be configured", file=sys.stderr)
        sys.exit(1)

    # Squid sends lines one at a time; flush after each response
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        parts = line.split(' ', 1)
        if len(parts) != 2:
            sys.stdout.write('ERR\n')
            sys.stdout.flush()
            continue

        username, password = parts[0], parts[1]
        ok = validate_user(cfg, username, password)
        sys.stdout.write('OK\n' if ok else 'ERR\n')
        sys.stdout.flush()


if __name__ == '__main__':
    main()
