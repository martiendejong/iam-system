#!/usr/bin/env bash
# Run once on the jengo-test server to set up the IAM stack.
# Assumes: Docker + docker compose + proxy_net already exist (bootstrap-server.sh done).
# Usage:  ssh root@49.13.157.166 "bash -s" < setup-server.sh
set -euo pipefail

IAM_DIR=/opt/iam
REPO_URL=https://github.com/martiendejong/iam-system.git
BRANCH=develop

echo "=== Clone / update IAM repo ==="
if [ -d "$IAM_DIR/src/.git" ]; then
    git -C "$IAM_DIR/src" fetch origin "$BRANCH"
    git -C "$IAM_DIR/src" checkout "$BRANCH"
    git -C "$IAM_DIR/src" merge --ff-only "origin/$BRANCH"
else
    mkdir -p "$IAM_DIR/src"
    git clone --branch "$BRANCH" "$REPO_URL" "$IAM_DIR/src"
fi

echo "=== Build Docker image ==="
docker build -t iam-system:latest "$IAM_DIR/src"

echo "=== Start IAM stack ==="
# .env must exist at $IAM_DIR/.env — copy deploy/.env.example and fill values
if [ ! -f "$IAM_DIR/.env" ]; then
    echo "ERROR: $IAM_DIR/.env not found. Copy deploy/.env.example and fill in values first."
    exit 1
fi
docker compose -f "$IAM_DIR/src/deploy/docker-compose.yml" --env-file "$IAM_DIR/.env" up -d

echo "=== Start gateway ==="
GATEWAY_DIR=/opt/gateway
mkdir -p "$GATEWAY_DIR"
cp "$IAM_DIR/src/deploy/caddy/Caddyfile" "$GATEWAY_DIR/Caddyfile"
cp "$IAM_DIR/src/deploy/caddy/docker-compose.yml" "$GATEWAY_DIR/docker-compose.yml"
docker compose -f "$GATEWAY_DIR/docker-compose.yml" up -d

echo "=== Done ==="
echo "Wait ~60s for startup, then smoke-test:"
echo "  curl https://iam-test.martiendejong.nl/.well-known/openid-configuration"
