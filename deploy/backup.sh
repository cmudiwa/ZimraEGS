#!/bin/sh
# Makes a backup inside the container (database + encryption keys, safe while the app runs),
# copies it to ./backups next to docker-compose.yml, and keeps the newest 30 there.
# Daily at 02:15, for example:  15 2 * * * /opt/fiscal/deploy/backup.sh >> /opt/fiscal/backups/backup.log 2>&1
set -eu
umask 077
cd "$(dirname "$0")/.."
mkdir -p backups
chmod 700 backups
docker compose exec -T app dotnet ZimraEGS.dll backup > /dev/null
latest=$(docker compose exec -T app sh -c 'ls -1t /data/backups/zimraegs-backup-*.tar.gz | head -n 1' | tr -d '\r')
docker compose cp "app:$latest" backups/
chmod 600 "backups/$(basename "$latest")"   # it holds the keys that decrypt the device keys
ls -1t backups/zimraegs-backup-*.tar.gz | tail -n +31 | xargs -r rm -f
echo "$(date '+%Y-%m-%d %H:%M') saved backups/$(basename "$latest")"
