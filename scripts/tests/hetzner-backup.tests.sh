#!/usr/bin/env bash
# Test suite for the nightly database backup (scripts/hetzner/backup.sh, ADR-0071, #688).
#
# Its real inputs (Neon, R2) exist nowhere in CI, so each case builds a throwaway stack dir and puts
# stub `docker` and `curl` commands first on PATH. The docker stub plays pg_dump and pg_restore, the
# curl stub plays the bucket in a local folder. openssl, flock and cmp are the real ones, so the
# encryption and the lock are tested as they run on the box.
#
# What the alert depends on: the success metric is written only when every database landed, and
# never after an empty database, a failed dump, a dump that is not an archive, a failed upload or a
# wrong read-back.

set -euo pipefail

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here (#1048).
SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
BACKUP_SH="$SCRIPTS_DIR/hetzner/backup.sh"
REAL_OPENSSL="$(command -v openssl)"
export REAL_OPENSSL
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

DB_PASSWORD='npg_SecretDbPassword42'
ENCRYPTION_KEY='testkey-testkey-testkey'
R2_SECRET='R2SecretAccessKey-0123456789abcdef'
OLD_METRIC='lotro_backup_last_success_timestamp_seconds 1000'

cases=0
CASE=""
LAST_OUTPUT=""
LAST_STATUS=0
STACK=""

fail() {
    printf '✗ [%s] %s\n' "$CASE" "$1"
    printf '  --- calls ---\n'
    sed 's/^/    /' "$STACK/calls.log" 2>/dev/null || true
    printf '  --- backup.sh output ---\n%s\n' "$LAST_OUTPUT" | sed 's/^/    /'
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

make_stubs() {
    local bin="$1"
    mkdir -p "$bin"

    # docker run … <image> psql|pg_dump|pg_restore …: the last words decide what it plays.
    cat > "$bin/docker" <<'STUB'
#!/usr/bin/env bash
printf 'docker %s\n' "$*" >> "$STUB_STATE/calls.log"
case " $* " in
    *" psql "*)
        printf 'rows-check %s: %s\n' "$PGDATABASE" "${*: -1}" >> "$STUB_STATE/env.log"
        if [ "${MISSING_TABLE_DB:-}" = "$PGDATABASE" ]; then
            echo 'ERROR:  relation does not exist' >&2
            exit 1
        fi
        if [ "${EMPTY_DB:-}" = "$PGDATABASE" ]; then echo f; else echo t; fi
        ;;
    *" pg_dump "*)
        printf 'PGHOST=%s PGPORT=%s PGDATABASE=%s PGUSER=%s PGSSLMODE=%s PGSSLROOTCERT=%s\n' \
            "$PGHOST" "$PGPORT" "$PGDATABASE" "$PGUSER" "$PGSSLMODE" "${PGSSLROOTCERT-<unset>}" >> "$STUB_STATE/env.log"
        [ -n "$PGPASSWORD" ] && echo "password-set $PGDATABASE" >> "$STUB_STATE/env.log"
        [ "$PGPASSWORD" = "${EXPECTED_PGPASSWORD:-<none>}" ] && echo "password-matches $PGDATABASE" >> "$STUB_STATE/env.log"
        if [ "${FAIL_DUMP_DB:-}" = "$PGDATABASE" ]; then
            echo "pg_dump: error: connection failed" >&2
            exit 1
        fi
        if [ "${GARBAGE_DUMP_DB:-}" = "$PGDATABASE" ]; then
            echo "not an archive"
            exit 0
        fi
        printf 'PGDMP-archive-of-%s-%s\n' "$PGDATABASE" "zażółć gęślą jaźń"
        ;;
    *" pg_restore --file=/dev/null "*)
        first="$(head -c 5)"
        cat > /dev/null
        [ "$first" = "PGDMP" ] || { echo "pg_restore: error: input file does not appear to be a valid archive" >&2; exit 1; }
        ;;
    *)
        echo "unexpected docker call: $*" >&2
        exit 99
        ;;
esac
STUB

    # curl … -K - … -T <file> <url> | curl … -I <url>: the bucket is $STUB_STATE/bucket.
    cat > "$bin/curl" <<'STUB'
#!/usr/bin/env bash
printf 'curl %s\n' "$*" >> "$STUB_STATE/calls.log"
config="$(cat)"
case "$config" in
    'user = "'*':'*'"') echo "credentials-on-stdin" >> "$STUB_STATE/curl-config.log" ;;
esac
upload='' head=0 url=''
while [ $# -gt 0 ]; do
    case "$1" in
        -T) upload="$2"; shift ;;
        -I) head=1 ;;
        http*) url="$1" ;;
    esac
    shift
done
object="$STUB_STATE/bucket/${url#*://*/}"
if [ -n "$upload" ]; then
    case "$url" in
        *"${FAIL_UPLOAD_DB:-<none>}"*) echo "curl: (22) The requested URL returned error: 403" >&2; exit 22 ;;
    esac
    mkdir -p "$(dirname "$object")"
    cp "$upload" "$object"
    case "$url" in
        *"${STORE_THEN_FAIL_UPLOAD_DB:-<none>}"*) echo "curl: (28) Operation timed out" >&2; exit 28 ;;
    esac
    exit 0
fi
if [ "$head" = 1 ]; then
    [ -z "${FAIL_HEAD:-}" ] || { echo "curl: (28) Operation timed out" >&2; exit 28; }
    [ -f "$object" ] || { echo "curl: (22) The requested URL returned error: 404" >&2; exit 22; }
    etag="$("$REAL_OPENSSL" dgst -md5 -r "$object" | cut -d' ' -f1)"
    size="$(stat -c %s "$object")"
    [ -n "${WRONG_ETAG:-}" ] && etag='00000000000000000000000000000000'
    [ -n "${WRONG_SIZE:-}" ] && size=$((size + 1))
    # With --retry, curl prints the failed attempt's block before the one that counted.
    if [ -n "${RETRIED_HEAD:-}" ]; then
        printf 'HTTP/2 503\r\netag: "ffffffffffffffffffffffffffffffff"\r\ncontent-length: 55\r\n\r\n'
    fi
    if [ -n "${HTTP1_HEAD:-}" ]; then
        printf 'HTTP/1.1 200 OK\r\nETag: "%s"\r\nContent-Length: %s\r\n\r\n' "$etag" "$size"
    else
        # What R2 sends: HTTP/2, lower-case header names.
        printf 'HTTP/2 200\r\netag: "%s"\r\ncontent-length: %s\r\n\r\n' "$etag" "$size"
    fi
    exit 0
fi
echo "unexpected curl call" >&2
exit 99
STUB
    # The real openssl, except where a case makes encryption fail or a decrypt give other bytes.
    cat > "$bin/openssl" <<'STUB'
#!/usr/bin/env bash
if [ "$1" = enc ]; then
    case " $* " in
        *" -d "*) [ -z "${OPENSSL_DECRYPTS_WRONG:-}" ] || { echo 'other bytes'; exit 0; } ;;
        *) [ -z "${OPENSSL_ENCRYPT_FAILS:-}" ] || { echo 'openssl: write error' >&2; exit 1; } ;;
    esac
fi
exec "$REAL_OPENSSL" "$@"
STUB
    chmod +x "$bin/docker" "$bin/curl" "$bin/openssl"
}

# A stack dir with an .env as the runbook's wiring leaves it. The auth string uses Neon's pooled
# host; the translation string names its own port and a verify-full SSL mode.
new_stack() {
    STACK="$TMP_ROOT/$1"
    mkdir -p "$STACK/backups/metrics"
    make_stubs "$STACK/bin"
    {
        echo 'COMPOSE_PROJECT_NAME=lotro-prod'
        echo "ConnectionStrings__AuthDatabase=Host=ep-calm-1-pooler.eu-central-1.aws.neon.tech;Database=lotro_auth;Username=neondb_owner;Password=${DB_PASSWORD};Ssl Mode=Require;Timeout=20"
        echo "ConnectionStrings__TranslationDatabase=Host=ep-calm-1.eu-central-1.aws.neon.tech;Port=5433;Database=lotro_translation;Username=neondb_owner;Password=${DB_PASSWORD};SSL Mode=VerifyFull;Timeout=60"
        echo "BACKUP_ENCRYPTION_KEY=${ENCRYPTION_KEY}"
        echo 'BACKUP_R2_ACCOUNT_ID=0123456789abcdef0123456789abcdef'
        echo 'BACKUP_R2_BUCKET=lotro-backups-prod'
        echo 'BACKUP_R2_ACCESS_KEY_ID=R2AccessKeyId'
        echo "BACKUP_R2_SECRET_ACCESS_KEY=${R2_SECRET}"
    } > "$STACK/.env"
    chmod 600 "$STACK/.env"
    echo "$OLD_METRIC" > "$STACK/backups/metrics/lotro_backup.prom"
}

run_backup() {
    set +e
    LAST_OUTPUT="$(env PATH="$STACK/bin:$PATH" STUB_STATE="$STACK" STACK_DIR="$STACK" \
        BACKUP_S3_ENDPOINT='https://stub.invalid' "$@" bash "$BACKUP_SH" 2>&1)"
    LAST_STATUS=$?
    set -e
}

metric_untouched() {
    [ "$(cat "$STACK/backups/metrics/lotro_backup.prom")" = "$OLD_METRIC" ]
}

object_for() {
    find "$STACK/bucket/lotro-backups-prod/$1" -name "$1-*.dump.enc" 2>/dev/null | head -1
}

# ---------------------------------------------------------------------------------------------------
CASE="happy path"
new_stack happy
run_backup
[ "$LAST_STATUS" = 0 ] || fail "expected exit 0, got $LAST_STATUS"
auth_object="$(object_for lotro_auth)"
translation_object="$(object_for lotro_translation)"
[ -n "$auth_object" ] && [ -n "$translation_object" ] || fail "both databases must land in the bucket"
pass "both databases land in the bucket, each under its own prefix"

first_dump="$(grep -m1 -o 'PGDATABASE=[a-z_]*' "$STACK/env.log")"
[ "$first_dump" = 'PGDATABASE=lotro_auth' ] || fail "the auth database must be dumped first, got $first_dump"
pass "the auth database is dumped before the translation database"

if grep -q 'PGDMP' "$auth_object" || grep -q 'zażółć' "$translation_object"; then
    fail "an object in the bucket holds plain text"
fi
decrypted="$(BACKUP_ENCRYPTION_KEY="$ENCRYPTION_KEY" openssl enc -d -aes-256-cbc -md sha256 -pbkdf2 -iter 600000 \
    -pass env:BACKUP_ENCRYPTION_KEY -in "$translation_object")"
[ "$decrypted" = 'PGDMP-archive-of-lotro_translation-zażółć gęślą jaźń' ] || fail "the object must decrypt to the dump, got: $decrypted"
pass "the objects are ciphertext that the key decrypts back to the exact dump"

grep -q 'PGHOST=ep-calm-1.eu-central-1.aws.neon.tech PGPORT=5432 PGDATABASE=lotro_auth PGUSER=neondb_owner PGSSLMODE=require ' "$STACK/env.log" \
    || fail "the pooled host must become the direct host, with the default port and require"
grep -q 'PGHOST=ep-calm-1.eu-central-1.aws.neon.tech PGPORT=5433 PGDATABASE=lotro_translation PGUSER=neondb_owner PGSSLMODE=verify-full ' "$STACK/env.log" \
    || fail "the translation string's port and SSL mode must reach pg_dump"
grep -q 'PGDATABASE=lotro_auth .* PGSSLROOTCERT=<unset>' "$STACK/env.log" || fail "require must not set a root certificate"
grep -q 'PGDATABASE=lotro_translation .* PGSSLROOTCERT=system' "$STACK/env.log" || fail "verify-full must check against the system CA store"
grep -q 'password-set lotro_translation' "$STACK/env.log" || fail "the password must reach pg_dump"
pass "the connection strings become libpq variables, and the pooler is never used"

metric="$STACK/backups/metrics/lotro_backup.prom"
grep -Eq '^lotro_backup_last_success_timestamp_seconds [0-9]{10}$' "$metric" || fail "the success metric is missing"
grep -Eq '^lotro_backup_object_size_bytes\{database="lotro_auth"\} [0-9]+$' "$metric" || fail "the auth size metric is missing"
grep -Eq '^lotro_backup_object_size_bytes\{database="lotro_translation"\} [0-9]+$' "$metric" || fail "the translation size metric is missing"
[ "$(stat -c %a "$metric")" = 644 ] && [ "$(stat -c %a "$STACK/backups/metrics")" = 755 ] \
    || fail "the agent must be able to read the metric"
pass "the success metric and the object sizes are written, readable by the agent"

[ ! -e "$STACK/backups/work" ] || fail "the work folder must be gone after the run"
grep -q 'credentials-on-stdin' "$STACK/curl-config.log" || fail "curl must get the R2 key pair on stdin"
pass "no dump is left on disk, and curl gets its key pair on stdin"

if grep -F -e "$DB_PASSWORD" -e "$ENCRYPTION_KEY" -e "$R2_SECRET" "$STACK/calls.log" >/dev/null \
    || printf '%s' "$LAST_OUTPUT" | grep -F -e "$DB_PASSWORD" -e "$ENCRYPTION_KEY" -e "$R2_SECRET" >/dev/null; then
    fail "a secret reached argv or the output"
fi
pass "no password, key or R2 secret reaches argv or the output"

grep -qF 'rows-check lotro_auth: SELECT EXISTS (SELECT 1 FROM authsystem."Users")' "$STACK/env.log" \
    && grep -qF 'rows-check lotro_translation: SELECT EXISTS (SELECT 1 FROM translation."Translations")' "$STACK/env.log" \
    || fail "each database must be checked for rows in its own never-empty table"
pass "each database is checked for rows before it is dumped"

grep -Eq ' postgres:[^ ]+@sha256:[0-9a-f]{64} pg_dump ' "$STACK/calls.log" \
    || fail "the container that gets the prod password must run an image pinned by digest"
pass "pg_dump runs in an image pinned by digest"

# ---------------------------------------------------------------------------------------------------
# The runbook's restore steps run: eval "$(bash /opt/lotro/backup.sh --helpers)".
CASE="the runbook's helpers"
new_stack helpers
sed -i "s/^BACKUP_ENCRYPTION_KEY=.*/BACKUP_ENCRYPTION_KEY=\"${ENCRYPTION_KEY}\"  /" "$STACK/.env"
sed -i 's/$/\r/' "$STACK/.env"
set +e
helpers="$(env PATH="$STACK/bin:$PATH" STUB_STATE="$STACK" STACK_DIR="$STACK" bash "$BACKUP_SH" --helpers 2>&1)"
helpers_status=$?
set -e
LAST_OUTPUT="$helpers"
[ "$helpers_status" = 0 ] || fail "--helpers must exit 0, got $helpers_status"
[ ! -s "$STACK/calls.log" ] || fail "--helpers must run nothing"
printf '%s' "$helpers" | grep -F -e "$DB_PASSWORD" -e "$ENCRYPTION_KEY" -e "$R2_SECRET" >/dev/null \
    && fail "--helpers must print no value of the .env"
helper_key="$(eval "$helpers"; env_value BACKUP_ENCRYPTION_KEY)"
[ "$helper_key" = "$ENCRYPTION_KEY" ] || fail "the helpers' env_value must read a quoted CRLF key as backup.sh does"
helper_image="$(eval "$helpers"; printf '%s' "${PG_IMAGE:-}")"
[[ "$helper_image" =~ ^postgres:[^\ ]+@sha256:[0-9a-f]{64}$ ]] || fail "the helpers must give the pinned image, got '$helper_image'"
grep -qF " $helper_image pg_dump " "$TMP_ROOT/happy/calls.log" || fail "the helpers' image must be the one pg_dump runs in"
pass "--helpers gives the runbook backup.sh's own reader and pinned image, and prints no secret"

new_stack unknown-argument
set +e
LAST_OUTPUT="$(env PATH="$STACK/bin:$PATH" STUB_STATE="$STACK" STACK_DIR="$STACK" bash "$BACKUP_SH" --helper 2>&1)"
LAST_STATUS=$?
set -e
[ "$LAST_STATUS" = 2 ] || fail "a mistyped argument must exit 2, got $LAST_STATUS"
[ ! -s "$STACK/calls.log" ] || fail "a mistyped argument must not start a backup"
grep -q "unknown argument '--helper'" <<< "$LAST_OUTPUT" || fail "the output must name the argument"
pass "a mistyped argument stops before any dump instead of starting a real backup"

# ---------------------------------------------------------------------------------------------------
CASE="the R2 endpoint"
new_stack r2-endpoint
run_backup BACKUP_S3_ENDPOINT=
[ "$LAST_STATUS" = 0 ] || fail "expected exit 0, got $LAST_STATUS"
grep -Eq ' https://0123456789abcdef0123456789abcdef\.eu\.r2\.cloudflarestorage\.com/lotro-backups-prod/lotro_auth/lotro_auth-[0-9]{8}T[0-9]{6}Z\.dump\.enc$' "$STACK/calls.log" \
    || fail "the upload must go to the bucket on the account's EU-jurisdiction R2 host"
pass "without an endpoint override the upload goes to the account's EU R2 host"

# ---------------------------------------------------------------------------------------------------
CASE="values as compose reads them"
new_stack quoted
sed -i "s/^BACKUP_ENCRYPTION_KEY=.*/BACKUP_ENCRYPTION_KEY='${ENCRYPTION_KEY}'  /; s/^BACKUP_R2_BUCKET=.*/BACKUP_R2_BUCKET=\"lotro-backups-prod\"/" "$STACK/.env"
sed -i 's/$/\r/' "$STACK/.env"
grep -q "^BACKUP_ENCRYPTION_KEY='" "$STACK/.env" || fail "the case must start from a quoted key"
run_backup
[ "$LAST_STATUS" = 0 ] || fail "expected exit 0, got $LAST_STATUS"
translation_object="$(object_for lotro_translation)"
[ -n "$translation_object" ] || fail "the quoted bucket name must name the bucket without its quotes"
decrypted="$(BACKUP_ENCRYPTION_KEY="$ENCRYPTION_KEY" openssl enc -d -aes-256-cbc -md sha256 -pbkdf2 -iter 600000 \
    -pass env:BACKUP_ENCRYPTION_KEY -in "$translation_object" 2>/dev/null)" || true
[ "$decrypted" = 'PGDMP-archive-of-lotro_translation-zażółć gęślą jaźń' ] \
    || fail "a quoted key in a CRLF file must encrypt under the key without its quotes"
pass "quotes, trailing spaces and CRLF line ends are not part of a value, as in compose"

CASE="a connection string Npgsql accepts"
new_stack npgsql-forms
quoted_password='p;w"x'
sed -i '/^ConnectionStrings__TranslationDatabase=/d' "$STACK/.env"
echo 'ConnectionStrings__TranslationDatabase=Host = ep-calm-1.eu-central-1.aws.neon.tech ; Database = lotro_translation ;User Id=neondb_owner;Password="p;w""x";SSL Mode=Require' >> "$STACK/.env"
run_backup EXPECTED_PGPASSWORD="$quoted_password"
[ "$LAST_STATUS" = 0 ] || fail "expected exit 0, got $LAST_STATUS"
grep -q 'PGHOST=ep-calm-1.eu-central-1.aws.neon.tech PGPORT=5432 PGDATABASE=lotro_translation PGUSER=neondb_owner PGSSLMODE=require ' "$STACK/env.log" \
    || fail "spaces around a plain value must not reach libpq"
grep -q 'password-matches lotro_translation' "$STACK/env.log" \
    || fail "a quoted password must keep its ';' and turn a doubled quote into one"
[ -n "$(object_for lotro_translation)" ] || fail "the object key must hold no space"
pass "spaced plain values are trimmed, and a quoted value keeps its ';' and doubled quotes"

# ---------------------------------------------------------------------------------------------------
CASE="an empty database"
new_stack empty-db
run_backup EMPTY_DB=lotro_translation
[ "$LAST_STATUS" = 1 ] || fail "expected exit 1, got $LAST_STATUS"
[ -n "$(object_for lotro_auth)" ] || fail "the other database must still be backed up"
[ -z "$(object_for lotro_translation)" ] || fail "an empty database must not be uploaded"
grep -q 'PGDATABASE=lotro_translation' "$STACK/env.log" && fail "an empty database must not be dumped"
metric_untouched || fail "the metric must keep the last good run"
grep -qF 'lotro_translation: translation."Translations" is empty' <<< "$LAST_OUTPUT" \
    || fail "the output must name the table"
pass "an empty database is never dumped, and the metric keeps the last good run"

new_stack missing-table
run_backup MISSING_TABLE_DB=lotro_auth
[ "$LAST_STATUS" = 1 ] || fail "expected exit 1, got $LAST_STATUS"
[ -z "$(object_for lotro_auth)" ] || fail "a database without the table must not be uploaded"
metric_untouched || fail "the metric must keep the last good run"
grep -qF 'lotro_auth: cannot read authsystem."Users"' <<< "$LAST_OUTPUT" || fail "the output must name the table"
grep -q 'is empty' <<< "$LAST_OUTPUT" && fail "a failed query must not be reported as an empty table"
pass "a database whose never-empty table cannot be read fails the run, and says it could not read it"

# ---------------------------------------------------------------------------------------------------
CASE="verify-ca"
new_stack verify-ca
sed -i 's/SSL Mode=VerifyFull/SSL Mode=VerifyCA/' "$STACK/.env"
grep -q 'SSL Mode=VerifyCA' "$STACK/.env" || fail "the case must start from a VerifyCA string"
run_backup
[ "$LAST_STATUS" = 0 ] || fail "expected exit 0, got $LAST_STATUS"
grep -q 'PGDATABASE=lotro_translation .* PGSSLMODE=verify-full PGSSLROOTCERT=system' "$STACK/env.log" \
    || fail "VerifyCA must become verify-full against the system CA store, which libpq allows"
pass "a VerifyCA string gets verify-full, the only mode libpq allows with the system CA store"

CASE="the SSL mode libpq gets"
# The password goes over this connection: an unnamed or unknown mode must never reach libpq, whose
# default `prefer` may fall back to plain text. A mode libpq knows passes through as written.
for ssl_case in 'none:require' 'Bogus:require' 'Disable:disable' 'Prefer:prefer'; do
    ssl_written="${ssl_case%%:*}" ssl_expected="${ssl_case#*:}"
    new_stack "ssl-$ssl_written"
    if [ "$ssl_written" = none ]; then
        sed -i 's/;SSL Mode=VerifyFull//' "$STACK/.env"
        grep -qi 'ssl mode' <(grep '^ConnectionStrings__TranslationDatabase=' "$STACK/.env") \
            && fail "the case must start from a string without an SSL mode"
    else
        sed -i "s/SSL Mode=VerifyFull/SSL Mode=$ssl_written/" "$STACK/.env"
    fi
    run_backup
    [ "$LAST_STATUS" = 0 ] || fail "SSL mode '$ssl_written': expected exit 0, got $LAST_STATUS"
    grep -q "PGDATABASE=lotro_translation PGUSER=neondb_owner PGSSLMODE=$ssl_expected PGSSLROOTCERT=<unset>" "$STACK/env.log" \
        || fail "SSL mode '$ssl_written' must reach libpq as $ssl_expected, with no root certificate"
done
pass "no SSL mode or an unknown one becomes require; disable and prefer pass through as written"

CASE="a failed dump"
new_stack failed-dump
run_backup FAIL_DUMP_DB=lotro_auth
[ "$LAST_STATUS" = 1 ] || fail "expected exit 1, got $LAST_STATUS"
[ -n "$(object_for lotro_translation)" ] || fail "the other database must still be backed up"
[ -z "$(object_for lotro_auth)" ] || fail "nothing may be uploaded for the failed database"
metric_untouched || fail "a failed run must not update the success metric"
grep -q 'lotro_auth: pg_dump failed' <<< "$LAST_OUTPUT" || fail "the output must name the failed database"
pass "the next database is still dumped, and the metric keeps the last good run"

# ---------------------------------------------------------------------------------------------------
CASE="not an archive"
new_stack garbage
run_backup GARBAGE_DUMP_DB=lotro_translation
[ "$LAST_STATUS" = 1 ] || fail "expected exit 1, got $LAST_STATUS"
[ -z "$(object_for lotro_translation)" ] || fail "a dump that is not an archive must not be uploaded"
metric_untouched || fail "the metric must keep the last good run"
pass "a dump that pg_restore cannot read is never uploaded"

CASE="encryption"
new_stack encrypt-fails
run_backup OPENSSL_ENCRYPT_FAILS=1
[ "$LAST_STATUS" = 1 ] || fail "expected exit 1, got $LAST_STATUS"
[ ! -d "$STACK/bucket" ] || fail "nothing may be uploaded when encryption fails"
metric_untouched || fail "the metric must keep the last good run"
grep -q 'lotro_auth: encryption failed' <<< "$LAST_OUTPUT" || fail "the output must name the step"
pass "a failed encryption uploads nothing"

new_stack decrypt-differs
run_backup OPENSSL_DECRYPTS_WRONG=1
[ "$LAST_STATUS" = 1 ] || fail "expected exit 1, got $LAST_STATUS"
[ ! -d "$STACK/bucket" ] || fail "nothing may be uploaded when the ciphertext does not decrypt to the dump"
metric_untouched || fail "the metric must keep the last good run"
grep -q 'does not decrypt to the dump' <<< "$LAST_OUTPUT" || fail "the output must say why"
pass "ciphertext that does not decrypt to the exact dump is never uploaded"

# ---------------------------------------------------------------------------------------------------
CASE="a failed upload"
new_stack failed-upload
run_backup FAIL_UPLOAD_DB=lotro_translation
[ "$LAST_STATUS" = 1 ] || fail "expected exit 1, got $LAST_STATUS"
metric_untouched || fail "the metric must keep the last good run"
grep -q 'lotro_translation: the upload failed' <<< "$LAST_OUTPUT" || fail "the output must name the step"
pass "a refused upload fails the run"

CASE="a PUT that landed but lost its answer"
new_stack landed-answer-lost
run_backup STORE_THEN_FAIL_UPLOAD_DB=lotro_translation
[ "$LAST_STATUS" = 0 ] || fail "expected exit 0, got $LAST_STATUS"
grep -Eq '^lotro_backup_last_success_timestamp_seconds [0-9]{10}$' "$STACK/backups/metrics/lotro_backup.prom" \
    || fail "an object that landed and reads back right must count"
grep -q 'checking whether the object landed' <<< "$LAST_OUTPUT" || fail "the output must say why it went on"
pass "a PUT whose answer was lost counts when the read-back shows the object"

CASE="a wrong read-back"
new_stack wrong-etag
run_backup WRONG_ETAG=1
[ "$LAST_STATUS" = 1 ] || fail "expected exit 1, got $LAST_STATUS"
metric_untouched || fail "the metric must keep the last good run"
grep -q 'is not what was sent' <<< "$LAST_OUTPUT" || fail "the output must say the object differs"
pass "an object whose ETag is not the MD5 of what was sent fails the run"

CASE="a wrong size"
new_stack wrong-size
run_backup WRONG_SIZE=1
[ "$LAST_STATUS" = 1 ] || fail "expected exit 1, got $LAST_STATUS"
metric_untouched || fail "the metric must keep the last good run"
pass "an object of another size fails the run, even with the right ETag"

CASE="a failed read-back"
new_stack head-fails
run_backup FAIL_HEAD=1
[ "$LAST_STATUS" = 1 ] || fail "expected exit 1, got $LAST_STATUS"
metric_untouched || fail "the metric must keep the last good run"
grep -q 'cannot be read back' <<< "$LAST_OUTPUT" || fail "the output must say why"
pass "an upload that cannot be read back does not count"

CASE="a read-back that needed a retry"
new_stack head-retried
run_backup RETRIED_HEAD=1
[ "$LAST_STATUS" = 0 ] || fail "expected exit 0, got $LAST_STATUS"
grep -Eq '^lotro_backup_last_success_timestamp_seconds [0-9]{10}$' "$STACK/backups/metrics/lotro_backup.prom" \
    || fail "a good object read back on curl's retry must count"
pass "a HEAD that succeeds on curl's retry is judged by its last answer, not the failed one"

new_stack head-retried-wrong
run_backup RETRIED_HEAD=1 WRONG_SIZE=1
[ "$LAST_STATUS" = 1 ] || fail "expected exit 1, got $LAST_STATUS"
metric_untouched || fail "the metric must keep the last good run"
pass "after a retried HEAD, a wrong size in the last answer still fails the run"

CASE="an S3 store that answers over HTTP/1.1"
new_stack head-http1
run_backup HTTP1_HEAD=1
[ "$LAST_STATUS" = 0 ] || fail "expected exit 0, got $LAST_STATUS"
pass "mixed-case HTTP/1.1 header names (the drill's S3 stand-in) are read as well"

# ---------------------------------------------------------------------------------------------------
CASE="missing settings"
new_stack missing
sed -i '/^BACKUP_ENCRYPTION_KEY=/d; /^BACKUP_R2_ACCOUNT_ID=/d' "$STACK/.env"
set +e
LAST_OUTPUT="$(env PATH="$STACK/bin:$PATH" STUB_STATE="$STACK" STACK_DIR="$STACK" bash "$BACKUP_SH" 2>&1)"
LAST_STATUS=$?
set -e
[ "$LAST_STATUS" = 2 ] || fail "expected exit 2, got $LAST_STATUS"
grep -q 'BACKUP_ENCRYPTION_KEY BACKUP_R2_ACCOUNT_ID' <<< "$LAST_OUTPUT" || fail "the output must name both missing keys"
[ ! -s "$STACK/calls.log" ] || fail "nothing may be dumped without the settings"
metric_untouched || fail "the metric must keep the last good run"
pass "a missing key stops the run before any dump, and names the key"

CASE="a connection string without a password"
new_stack no-password
sed -i 's/;Password=[^;]*;SSL Mode/;SSL Mode/' "$STACK/.env"
run_backup
[ "$LAST_STATUS" = 1 ] || fail "expected exit 1, got $LAST_STATUS"
[ -n "$(object_for lotro_auth)" ] || fail "the other database must still be backed up"
[ -z "$(object_for lotro_translation)" ] || fail "nothing may be dumped from an incomplete string"
grep -q 'PGDATABASE=lotro_translation' "$STACK/env.log" && fail "pg_dump must not run for an incomplete string"
metric_untouched || fail "the metric must keep the last good run"
grep -q 'ConnectionStrings__TranslationDatabase needs Host, Database, Username and Password' <<< "$LAST_OUTPUT" \
    || fail "the output must name the key"
pass "a connection string without a password is refused, and the other database is still backed up"

CASE="an empty connection string"
new_stack no-auth-string
sed -i '/^ConnectionStrings__AuthDatabase=/d' "$STACK/.env"
run_backup
[ "$LAST_STATUS" = 1 ] || fail "expected exit 1, got $LAST_STATUS"
[ -n "$(object_for lotro_translation)" ] || fail "the other database must still be backed up"
metric_untouched || fail "the metric must keep the last good run"
grep -q 'has no value for ConnectionStrings__AuthDatabase' <<< "$LAST_OUTPUT" || fail "the output must name the key"
pass "a missing connection string fails the run and names the key"

CASE="a database key with no never-empty table"
new_stack unknown-key
echo "ConnectionStrings__OtherDatabase=Host=ep-calm-1.eu-central-1.aws.neon.tech;Database=lotro_other;Username=neondb_owner;Password=${DB_PASSWORD}" >> "$STACK/.env"
run_backup DATABASE_KEYS='ConnectionStrings__OtherDatabase ConnectionStrings__AuthDatabase'
[ "$LAST_STATUS" = 1 ] || fail "expected exit 1, got $LAST_STATUS"
[ -n "$(object_for lotro_auth)" ] || fail "the other database must still be backed up"
grep -q 'lotro_other' "$STACK/calls.log" "$STACK/env.log" && fail "nothing may run against a database with no row check"
metric_untouched || fail "the metric must keep the last good run"
grep -q 'ConnectionStrings__OtherDatabase names no table that must have rows' <<< "$LAST_OUTPUT" \
    || fail "the output must name the key"
pass "a database key with no never-empty table is refused before anything runs against it"

CASE="no .env"
new_stack no-env
rm -f "$STACK/.env"
run_backup
[ "$LAST_STATUS" = 2 ] || fail "expected exit 2, got $LAST_STATUS"
pass "a missing .env stops the run"

CASE="an unreadable .env"
if [ "$(id -u)" = 0 ]; then
    printf '- [%s] skipped: root reads any file\n' "$CASE"
else
    new_stack unreadable
    chmod 000 "$STACK/.env"
    run_backup
    chmod 600 "$STACK/.env"
    [ "$LAST_STATUS" = 2 ] || fail "expected exit 2, got $LAST_STATUS"
    [ ! -s "$STACK/calls.log" ] || fail "nothing may be dumped"
    grep -q 'is not readable by' <<< "$LAST_OUTPUT" || fail "the output must name the cause"
    pass "an .env the user cannot read stops the run and says so"
fi

CASE="a missing tool"
new_stack no-flock
mkdir -p "$STACK/sandbox"
for tool in sed head id cmp; do ln -s "$(command -v "$tool")" "$STACK/sandbox/$tool"; done
set +e
LAST_OUTPUT="$(env PATH="$STACK/bin:$STACK/sandbox" STUB_STATE="$STACK" STACK_DIR="$STACK" \
    BACKUP_S3_ENDPOINT='https://stub.invalid' "$BASH" "$BACKUP_SH" 2>&1)"
LAST_STATUS=$?
set -e
[ "$LAST_STATUS" = 2 ] || fail "expected exit 2, got $LAST_STATUS"
grep -q "'flock' is not installed" <<< "$LAST_OUTPUT" || fail "the output must name the tool"
[ ! -s "$STACK/calls.log" ] || fail "nothing may be dumped"
pass "a missing tool stops the run before any dump, and names the tool"

# ---------------------------------------------------------------------------------------------------
CASE="the lock"
new_stack locked
exec 8>"$STACK/backups/.lock"
flock -n 8
run_backup
exec 8>&-
[ "$LAST_STATUS" = 1 ] || fail "expected exit 1, got $LAST_STATUS"
[ ! -s "$STACK/calls.log" ] || fail "a second run must not dump anything"
grep -q 'another run holds' <<< "$LAST_OUTPUT" || fail "the output must say why"
pass "a run that finds the lock taken does nothing"

CASE="a killed run's leftovers"
new_stack leftovers
mkdir -p "$STACK/backups/work"
echo 'plain dump from a killed run' > "$STACK/backups/work/lotro_auth.dump"
run_backup
[ "$LAST_STATUS" = 0 ] || fail "expected exit 0, got $LAST_STATUS"
[ ! -e "$STACK/backups/work" ] || fail "the leftovers must be swept"
pass "a plain dump that a killed run left behind is removed"

printf '\nhetzner-backup: %d checks passed\n' "$cases"
