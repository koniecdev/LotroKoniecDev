#!/usr/bin/env bash
# Nightly encrypted dump of both Neon databases to the environment's R2 bucket (ADR-0071, #688).
# Runs ON the prod box as the `deploy` user, from its crontab. CD copies it next to deploy.sh, and an
# operator runs the same command by hand to prove the wiring:
#
#   bash /opt/lotro/backup.sh
#
# For each database, auth first (so an erasure is never newer in the auth dump than in the other):
#   1. a table that is never empty on prod must have rows, so an empty database never counts;
#   2. pg_dump --format=custom in a postgres container, against the direct host (never the pooler);
#   3. pg_restore reads the whole dump, so a file that is not a complete archive never counts;
#   4. AES-256 encryption under BACKUP_ENCRYPTION_KEY, and a decrypt that must give the same bytes;
#   5. a PUT to the bucket, then a HEAD that must show the same size and MD5 (the ETag).
# Only when every database landed does it write the success metric that the agent on the box
# exports and the "backup did not land" alert reads. A failed database does not stop the next one.
#
# Retention is not here: it is the bucket's 30-day lifecycle rule (runbook → Database backups).
# No secret reaches argv or the output: the password goes to the container as an environment
# variable, the key to openssl as `-pass env:`, and the R2 key pair to curl on stdin.
#
# No .ps1 twin: it runs only on the Linux server, like deploy.sh.

set -euo pipefail

STACK_DIR="${STACK_DIR:-/opt/lotro}"
ENV_FILE="${ENV_FILE:-$STACK_DIR/.env}"
BACKUP_DIR="${BACKUP_DIR:-$STACK_DIR/backups}"
# The agent's host exporter reads *.prom files here (.docker/observability/alloy/config.alloy).
METRICS_DIR="${METRICS_DIR:-$BACKUP_DIR/metrics}"
# Must be the Neon server's major or newer: pg_dump refuses a newer server. Pinned by digest, because
# the container gets the prod password every night. No Dependabot entry reads this file: bump by
# hand with `docker buildx imagetools inspect postgres:18.<n>-alpine`. The runbook reads it from here.
POSTGRES_IMAGE="${POSTGRES_IMAGE:-postgres:18.7-alpine@sha256:75dcac3dfb1cd01ca3536910bbc84128759362f8b118bf8c55b38a94f68f3b73}"
DATABASE_KEYS="${DATABASE_KEYS:-ConnectionStrings__AuthDatabase ConnectionStrings__TranslationDatabase}"
# Only the restore drill and the tests set these three; a box derives the EU endpoint of R2.
# An `Ssl Mode` the connection string does not name, or one libpq does not know, becomes `require`.
BACKUP_S3_ENDPOINT="${BACKUP_S3_ENDPOINT:-}"
BACKUP_S3_REGION="${BACKUP_S3_REGION:-auto}"
BACKUP_DOCKER_NETWORK="${BACKUP_DOCKER_NETWORK:-}"

log() { printf '==> %s\n' "$*"; }
fail() { printf 'backup.sh: %s\n' "$*" >&2; }

# One value out of the box .env, read as compose reads it: a trailing CR, trailing spaces and one
# pair of surrounding quotes are not part of the value. Otherwise a quoted key would encrypt under
# other bytes than the password-manager copy. The runbook's helper is the same sed. The file is
# never sourced: its connection strings hold spaces and semicolons.
env_value() {
    sed -n "s/^$1=//p" "$ENV_FILE" | head -1 \
        | sed -e 's/\r$//' -e 's/[[:space:]]*$//' -e "s/^'\(.*\)'\$/\1/" -e 's/^"\(.*\)"$/\1/'
}

if [ ! -f "$ENV_FILE" ]; then
    fail "$ENV_FILE is missing."
    exit 2
fi
if [ ! -r "$ENV_FILE" ]; then
    fail "$ENV_FILE is not readable by $(id -un). Fix: chown deploy:deploy $ENV_FILE && chmod 600 $ENV_FILE."
    exit 2
fi

for tool in docker openssl curl flock cmp; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        fail "'$tool' is not installed on this box."
        exit 2
    fi
done

BACKUP_ENCRYPTION_KEY="$(env_value BACKUP_ENCRYPTION_KEY)"
r2_account_id="$(env_value BACKUP_R2_ACCOUNT_ID)"
bucket="$(env_value BACKUP_R2_BUCKET)"
r2_access_key_id="$(env_value BACKUP_R2_ACCESS_KEY_ID)"
r2_secret_access_key="$(env_value BACKUP_R2_SECRET_ACCESS_KEY)"
export BACKUP_ENCRYPTION_KEY

missing=''
for key in BACKUP_ENCRYPTION_KEY BACKUP_R2_BUCKET BACKUP_R2_ACCESS_KEY_ID BACKUP_R2_SECRET_ACCESS_KEY; do
    if [ -z "$(env_value "$key")" ]; then
        missing="$missing $key"
    fi
done
if [ -z "$BACKUP_S3_ENDPOINT" ] && [ -z "$r2_account_id" ]; then
    missing="$missing BACKUP_R2_ACCOUNT_ID"
fi
if [ -n "$missing" ]; then
    fail "$ENV_FILE has no value for:$missing (runbook → Database backups → Wiring)."
    exit 2
fi
if [ -z "$BACKUP_S3_ENDPOINT" ]; then
    BACKUP_S3_ENDPOINT="https://${r2_account_id}.eu.r2.cloudflarestorage.com"
fi

umask 077
mkdir -p "$BACKUP_DIR" "$METRICS_DIR"
chmod 755 "$METRICS_DIR"

exec 9>"$BACKUP_DIR/.lock"
if ! flock -n 9; then
    fail "another run holds $BACKUP_DIR/.lock; nothing was dumped."
    exit 1
fi

# The work folder sits on disk, never in /tmp (RAM on these boxes). The lock guarantees no live run
# owns what an earlier, killed run left here.
WORK_DIR="$BACKUP_DIR/work"
rm -rf "$WORK_DIR"
mkdir -m 700 "$WORK_DIR"
trap 'rm -rf "$WORK_DIR"' EXIT

trim() {
    local text="$1"
    text="${text#"${text%%[![:space:]]*}"}"
    printf '%s' "${text%"${text##*[![:space:]]}"}"
}

# Npgsql keyword form (`Host=…;Database=…;Username=…;Password=…;Ssl Mode=Require`) into libpq's
# variables, read as Npgsql reads it: key names are case-insensitive and may hold spaces, a plain
# value is trimmed and runs to the next `;`, and a value in single or double quotes may hold `;`,
# with a doubled quote standing for one.
parse_connection_string() {
    local connection_string="$1" length="${#1}" position=0 char quote name value
    PGHOST='' PGPORT='' PGDATABASE='' PGUSER='' PGPASSWORD='' PGSSLMODE=''
    # Unset, not empty: `docker run -e NAME` then leaves it out of the container.
    unset PGSSLROOTCERT
    while [ "$position" -lt "$length" ]; do
        name=''
        while [ "$position" -lt "$length" ] && [ "${connection_string:position:1}" != '=' ] \
                && [ "${connection_string:position:1}" != ';' ]; do
            name+="${connection_string:position:1}"
            position=$((position + 1))
        done
        if [ "${connection_string:position:1}" = ';' ] || [ "$position" -ge "$length" ]; then
            position=$((position + 1))
            continue
        fi
        position=$((position + 1))
        while [ "$position" -lt "$length" ] && [[ "${connection_string:position:1}" == [[:space:]] ]]; do
            position=$((position + 1))
        done
        value=''
        quote="${connection_string:position:1}"
        if [ "$quote" = '"' ] || [ "$quote" = "'" ]; then
            position=$((position + 1))
            while [ "$position" -lt "$length" ]; do
                char="${connection_string:position:1}"
                position=$((position + 1))
                if [ "$char" = "$quote" ]; then
                    if [ "${connection_string:position:1}" != "$quote" ]; then
                        break
                    fi
                    position=$((position + 1))
                fi
                value+="$char"
            done
            while [ "$position" -lt "$length" ] && [ "${connection_string:position:1}" != ';' ]; do
                position=$((position + 1))
            done
        else
            while [ "$position" -lt "$length" ] && [ "${connection_string:position:1}" != ';' ]; do
                value+="${connection_string:position:1}"
                position=$((position + 1))
            done
            value="$(trim "$value")"
        fi
        position=$((position + 1))
        name="$(printf '%s' "$name" | tr -d '[:space:]' | tr '[:upper:]' '[:lower:]')"
        case "$name" in
            host|server) PGHOST="$value" ;;
            port) PGPORT="$value" ;;
            database|db) PGDATABASE="$value" ;;
            username|userid|user) PGUSER="$value" ;;
            password|pwd) PGPASSWORD="$value" ;;
            sslmode) PGSSLMODE="$(printf '%s' "$value" | tr -d ' ' | tr '[:upper:]' '[:lower:]')" ;;
        esac
    done
    # pg_dump holds one long session, which Neon's pooler is not built for.
    PGHOST="${PGHOST/-pooler./.}"
    PGPORT="${PGPORT:-5432}"
    case "$PGSSLMODE" in
        # libpq checks against the image's CA store (as Npgsql uses the OS one) only with
        # verify-full, so a VerifyCA string gets the stricter check.
        verifyfull|verifyca) PGSSLMODE='verify-full' ;;
        disable|allow|prefer|require) ;;
        *) PGSSLMODE='require' ;;
    esac
    case "$PGSSLMODE" in
        verify-full) export PGSSLROOTCERT='system' ;;
    esac
    export PGHOST PGPORT PGDATABASE PGUSER PGPASSWORD PGSSLMODE
}

# The R2 key pair reaches curl through a config on stdin: `--user` would put the secret in argv.
s3_curl() {
    printf 'user = "%s:%s"\n' "$r2_access_key_id" "$r2_secret_access_key" \
        | curl -fsS --max-time 900 --retry 3 --retry-delay 5 --retry-connrefused \
            -K - --aws-sigv4 "aws:amz:${BACKUP_S3_REGION}:s3" "$@"
}

postgres_container() {
    local network_args=()
    if [ -n "$BACKUP_DOCKER_NETWORK" ]; then
        network_args=(--network "$BACKUP_DOCKER_NETWORK")
    fi
    docker run --rm -i "${network_args[@]}" \
        -e PGHOST -e PGPORT -e PGDATABASE -e PGUSER -e PGPASSWORD -e PGSSLMODE -e PGSSLROOTCERT \
        -e PGCONNECT_TIMEOUT=60 \
        "$POSTGRES_IMAGE" "$@"
}

# A table that has rows in every real database of that kind: the seeded admin in auth, the imported
# texts in translation.
required_table() {
    case "$1" in
        ConnectionStrings__AuthDatabase) printf '%s' 'authsystem."Users"' ;;
        ConnectionStrings__TranslationDatabase) printf '%s' 'translation."Translations"' ;;
        *) return 1 ;;
    esac
}

metric_lines=''

backup_database() {
    local env_key="$1" connection_string table has_rows dump enc object_key object_url upload_failed size \
        etag_expected head_response etag_actual size_actual
    connection_string="$(env_value "$env_key")"
    if [ -z "$connection_string" ]; then
        fail "$ENV_FILE has no value for $env_key."
        return 1
    fi
    parse_connection_string "$connection_string"
    if [ -z "$PGHOST" ] || [ -z "$PGDATABASE" ] || [ -z "$PGUSER" ] || [ -z "$PGPASSWORD" ]; then
        fail "$env_key needs Host, Database, Username and Password."
        return 1
    fi

    if ! table="$(required_table "$env_key")"; then
        fail "$env_key names no table that must have rows."
        return 1
    fi

    dump="$WORK_DIR/$PGDATABASE.dump"
    enc="$dump.enc"
    object_key="$PGDATABASE/$PGDATABASE-$(date -u +%Y%m%dT%H%M%SZ).dump.enc"
    object_url="${BACKUP_S3_ENDPOINT}/${bucket}/${object_key}"

    # An empty database dumps into a valid archive too. Counted as good every night, it would let
    # the 30-day lifecycle rule remove every real dump.
    if ! has_rows="$(postgres_container psql -X -At -v ON_ERROR_STOP=1 \
            -c "SELECT EXISTS (SELECT 1 FROM $table)" < /dev/null)" || [ "$has_rows" != t ]; then
        fail "$PGDATABASE: $table is missing or empty, so $env_key does not name the real database."
        return 1
    fi

    log "$PGDATABASE: dumping"
    if ! postgres_container pg_dump --format=custom < /dev/null > "$dump"; then
        fail "$PGDATABASE: pg_dump failed."
        return 1
    fi
    # Without a database pg_restore writes the whole restore script, so it reads every data block.
    if ! postgres_container pg_restore --file=/dev/null < "$dump"; then
        fail "$PGDATABASE: the dump is not a complete archive."
        return 1
    fi

    log "$PGDATABASE: encrypting"
    if ! openssl enc -aes-256-cbc -md sha256 -pbkdf2 -iter 600000 -salt \
            -pass env:BACKUP_ENCRYPTION_KEY -in "$dump" -out "$enc"; then
        fail "$PGDATABASE: encryption failed."
        return 1
    fi
    if ! openssl enc -d -aes-256-cbc -md sha256 -pbkdf2 -iter 600000 \
            -pass env:BACKUP_ENCRYPTION_KEY -in "$enc" | cmp -s - "$dump"; then
        fail "$PGDATABASE: the encrypted file does not decrypt to the dump."
        return 1
    fi
    rm -f "$dump"

    size="$(stat -c %s "$enc")"
    etag_expected="$(openssl dgst -md5 -r "$enc" | cut -d' ' -f1)"

    log "$PGDATABASE: uploading $object_key ($size bytes)"
    # The read-back decides, not curl's exit code: when a PUT landed but its answer was lost, curl's
    # retry is refused by the bucket lock, and the object is still good.
    upload_failed=''
    if ! s3_curl -T "$enc" -o /dev/null "$object_url"; then
        upload_failed=1
        log "$PGDATABASE: the upload reported an error; checking whether the object landed"
    fi
    # A single-part PUT's ETag is the MD5 of the body, on R2 as on S3.
    if ! head_response="$(s3_curl -I "$object_url")"; then
        if [ -n "$upload_failed" ]; then
            fail "$PGDATABASE: the upload failed."
        else
            fail "$PGDATABASE: the uploaded object cannot be read back."
        fi
        return 1
    fi
    head_response="$(printf '%s' "$head_response" | tr -d '\r')"
    etag_actual="$(printf '%s\n' "$head_response" | awk 'tolower($1) == "etag:" { gsub(/"/, "", $2); print $2; exit }')"
    size_actual="$(printf '%s\n' "$head_response" | awk 'tolower($1) == "content-length:" { print $2; exit }')"
    if [ "$etag_actual" != "$etag_expected" ] || [ "$size_actual" != "$size" ]; then
        fail "$PGDATABASE: the object in the bucket is not what was sent (ETag ${etag_actual:-none} vs $etag_expected, size ${size_actual:-none} vs $size)."
        return 1
    fi
    rm -f "$enc"

    metric_lines="${metric_lines}lotro_backup_object_size_bytes{database=\"$PGDATABASE\"} $size"$'\n'
    log "$PGDATABASE: done"
}

failed=''
for env_key in $DATABASE_KEYS; do
    if ! backup_database "$env_key"; then
        failed="$failed $env_key"
    fi
done

if [ -n "$failed" ]; then
    fail "not every database landed (failed:$failed); the success metric was not updated."
    exit 1
fi

metric_file="$METRICS_DIR/lotro_backup.prom"
{
    echo '# HELP lotro_backup_last_success_timestamp_seconds Unix time of the last run in which every database dump landed in the bucket.'
    echo '# TYPE lotro_backup_last_success_timestamp_seconds gauge'
    echo "lotro_backup_last_success_timestamp_seconds $(date -u +%s)"
    echo '# HELP lotro_backup_object_size_bytes Size of the encrypted dump that the last good run uploaded.'
    echo '# TYPE lotro_backup_object_size_bytes gauge'
    printf '%s' "$metric_lines"
} > "$metric_file.tmp"
chmod 644 "$metric_file.tmp"
mv -f "$metric_file.tmp" "$metric_file"
log "every database landed in ${bucket}"
