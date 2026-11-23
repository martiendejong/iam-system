#!/bin/bash
# Server-side IAM release: backup, SQL, image swap, integration test, automatic rollback.
# Run by `at` (scheduled) or by hand. Config comes from /opt/releases/<id>/release.env.
# Usage: release.sh <release-id>
set -u
ID="$1"
REL=/opt/releases/$ID
. "$REL/release.env"   # STACK_DIR PROJECT DB_CONTAINER API_SERVICE BASE_URL NEW_IMAGE BACKUP_DIR MIN_FREE_MB
LOG=$REL/release.log
STATUS=$REL/status
exec >>"$LOG" 2>&1
log() { echo "[$(date -u +%FT%TZ)] $*"; }
setstatus() { echo "$1" > "$STATUS"; log "STATUS $1"; }
IMAGE=iam-system
PREV_TAG=prev-$ID

recreate() { (cd "$STACK_DIR" && docker compose -p "$PROJECT" up -d --force-recreate "$API_SERVICE"); }

wait_up() {
  for i in $(seq 1 40); do
    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$BASE_URL/.well-known/openid-configuration")
    [ "$code" = 200 ] && return 0
    sleep 3
  done
  return 1
}

# Integration test: every line must pass. Extend here when IAM grows new critical paths.
integration() {
  local ok=0
  chk() { # name expected actual
    if [ "$2" = "$3" ]; then log "  PASS $1 ($3)"; else log "  FAIL $1 want=$2 got=$3"; ok=1; fi
  }
  chk discovery 200 "$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$BASE_URL/.well-known/openid-configuration")"
  chk jwks 200 "$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$BASE_URL/.well-known/jwks")"
  chk admin-ui 200 "$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$BASE_URL/")"
  chk me-unauth 401 "$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$BASE_URL/api/users/me")"
  # a bad login must be a clean 400, not a 500 (a 500 means a schema/config problem on the token path)
  chk login-bad 400 "$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 -H 'Content-Type: application/json' -d '{"email":"release-probe@invalid.example","password":"Xx123456!x"}' "$BASE_URL/api/auth/login")"
  chk refresh-bad 401 "$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 -H 'Content-Type: application/json' -d '{"refreshToken":"release-probe"}' "$BASE_URL/api/auth/refresh")"
  # the schema the new build needs is really there
  chk schema-FamilyId 1 "$(docker exec "$DB_CONTAINER" sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "select count(*) from information_schema.columns where table_name='"'"'RefreshTokens'"'"' and column_name='"'"'FamilyId'"'"'"')"
  # no crash loop
  chk api-running true "$(docker inspect -f '{{.State.Running}}' "$(cd "$STACK_DIR" && docker compose -p "$PROJECT" ps -q "$API_SERVICE")")"
  [ "${FORCE_INTEGRATION_FAIL:-0}" = 1 ] && { log "  FAIL forced (rehearsal)"; ok=1; }
  return $ok
}

rollback() {
  log "ROLLBACK: restoring previous image"
  if docker image inspect "$IMAGE:$PREV_TAG" >/dev/null 2>&1; then
    docker tag "$IMAGE:$PREV_TAG" "$IMAGE:latest"
    recreate
    if wait_up; then setstatus ROLLED_BACK; else setstatus ROLLBACK_FAILED_CHECK_SERVER; fi
  else
    setstatus ROLLBACK_FAILED_NO_PREVIOUS_IMAGE
  fi
}

setstatus RUNNING
log "release $ID start, target $BASE_URL, image $NEW_IMAGE"

# 0. preflight: nothing is touched when these fail
free_mb=$(df -Pm / | awk 'NR==2{print $4}')
[ "$free_mb" -ge "${MIN_FREE_MB:-1500}" ] || { log "ABORT: only ${free_mb}MB free, need ${MIN_FREE_MB:-1500}"; setstatus ABORTED_PREFLIGHT; exit 1; }
docker image inspect "$NEW_IMAGE" >/dev/null 2>&1 || { log "ABORT: image $NEW_IMAGE not on this server"; setstatus ABORTED_PREFLIGHT; exit 1; }
wait_up || { log "ABORT: target not healthy before release, not touching it"; setstatus ABORTED_PREFLIGHT; exit 1; }

# 1. verified backup
mkdir -p "$BACKUP_DIR"
DUMP=$BACKUP_DIR/$ID.dump
docker exec "$DB_CONTAINER" sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' > "$DUMP" || { log "ABORT: pg_dump failed"; setstatus ABORTED_BACKUP; exit 1; }
if ! docker exec -i "$DB_CONTAINER" pg_restore -l < "$DUMP" >/dev/null 2>&1 || [ ! -s "$DUMP" ]; then
  log "ABORT: backup $DUMP unreadable"; setstatus ABORTED_BACKUP; exit 1
fi
log "backup ok: $DUMP ($(du -h "$DUMP" | cut -f1))"

# 2. additive SQL (idempotent files, ON_ERROR_STOP)
for f in "$REL"/sql/*.sql; do
  [ -e "$f" ] || continue
  log "apply $(basename "$f")"
  if ! docker exec -i "$DB_CONTAINER" sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1' < "$f"; then
    log "ABORT: SQL failed (transaction rolled back, nothing swapped)"; setstatus ABORTED_SQL; exit 1
  fi
done

# 3. swap
docker tag "$IMAGE:latest" "$IMAGE:$PREV_TAG"
docker tag "$NEW_IMAGE" "$IMAGE:latest"
recreate
if ! wait_up; then log "new build did not come up"; rollback; exit 1; fi

# 4. integration test, with retries for slow warm-up
passed=1
for attempt in 1 2 3; do
  log "integration test attempt $attempt"
  if integration; then passed=0; break; fi
  sleep 10
done
if [ $passed -ne 0 ]; then log "integration test failed"; rollback; exit 1; fi

setstatus DONE
log "release $ID done; previous image kept as $IMAGE:$PREV_TAG, DB backup $DUMP"
