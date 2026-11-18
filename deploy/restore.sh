#!/bin/sh
# Puts a backup made by backup.sh back: stops the app, replaces the database and keys in the data
# volume with the backup's, and starts the app again. Everything after the backup is lost.
#   ./deploy/restore.sh backups/zimraegs-backup-20261118-021500.tar.gz
set -eu
cd "$(dirname "$0")/.."
file=${1:?Give the backup file, for example backups/zimraegs-backup-20261118-021500.tar.gz}
[ -f "$file" ] || { echo "No such file: $file"; exit 1; }
printf 'This replaces ALL current data with %s. Type RESTORE to continue: ' "$file"
read answer
[ "$answer" = "RESTORE" ] || { echo "Nothing changed."; exit 1; }
dir=$(cd "$(dirname "$file")" && pwd)
docker compose stop app
docker compose run --rm --no-deps -T --user root -v "$dir:/restore:ro" --entrypoint sh app -c \
  "rm -f /data/zimraegs.db /data/zimraegs.db-wal /data/zimraegs.db-shm && rm -rf /data/keys && tar xzf /restore/$(basename "$file") -C /data && chown -R app:app /data"
docker compose start app
echo "Restored $file."
