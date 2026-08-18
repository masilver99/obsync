#!/usr/bin/env bash
set -Eeuo pipefail

image_ref="${1:?Usage: deploy.sh ghcr.io/masilver99/obsync:<commit-sha>}"
deploy_path="${2:-$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)}"
compose_file="$deploy_path/compose.production.yml"
env_file="$deploy_path/.env"

if [[ ! "$image_ref" =~ ^ghcr\.io/masilver99/obsync:[0-9a-f]{40}$ ]]; then
  echo "Refusing to deploy an image outside the immutable obsync SHA format." >&2
  exit 1
fi

for required_file in "$compose_file" "$env_file"; do
  if [[ ! -f "$required_file" ]]; then
    echo "Missing deployment file: $required_file" >&2
    exit 1
  fi
done

# Load only the trusted, server-local deployment configuration. It is never
# copied into the repository or sent to GitHub.
set -a
# shellcheck disable=SC1090
source "$env_file"
set +a

export OBSYNC_IMAGE="$image_ref"
backup_path="${OBSYNC_BACKUP_PATH:-$deploy_path/backups}"
health_url="${OBSYNC_HEALTH_URL:-http://127.0.0.1:8080/health}"
mkdir -p "$backup_path"

compose=(docker compose --env-file "$env_file" -f "$compose_file")
old_container="$("${compose[@]}" ps -q sync 2>/dev/null || true)"
old_image=""
if [[ -n "$old_container" ]]; then
  old_image="$(docker inspect --format '{{.Config.Image}}' "$old_container" 2>/dev/null || true)"
fi

restore_previous() {
  if [[ -n "$old_image" ]]; then
    echo "Restoring $old_image." >&2
    OBSYNC_IMAGE="$old_image" "${compose[@]}" up -d --remove-orphans sync || true
  fi
}

data_path="${OBSYNC_DATA_PATH:-$deploy_path/data}"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"

# Obsync uses SQLite WAL mode. Stop the service before archiving the database
# and content objects so the backup is a coherent point-in-time snapshot.
"${compose[@]}" stop sync >/dev/null 2>&1 || true
if [[ -d "$data_path" ]]; then
  if ! tar -C "$data_path" -czf "$backup_path/obsync-$timestamp.tar.gz" .; then
    echo "The deployment data backup failed; leaving the previous image in place." >&2
    restore_previous
    exit 1
  fi
fi

if ! "${compose[@]}" pull sync; then
  restore_previous
  exit 1
fi

if ! "${compose[@]}" up -d --remove-orphans sync; then
  restore_previous
  exit 1
fi

healthy=false
for attempt in {1..30}; do
  if curl --fail --silent --show-error "$health_url" >/dev/null; then
    healthy=true
    break
  fi
  sleep 2
done

if [[ "$healthy" != true ]]; then
  echo "The new container did not become healthy." >&2
  restore_previous
  exit 1
fi

# Retain the seven newest snapshots by filename. The backup directory must
# contain only deployment backups, not arbitrary user data.
find "$backup_path" -maxdepth 1 -type f -name 'obsync-*.tar.gz' -printf '%T@ %p\n' \
  | sort -nr \
  | tail -n +8 \
  | cut -d' ' -f2- \
  | xargs -r rm -f

echo "Deployed $image_ref successfully."
