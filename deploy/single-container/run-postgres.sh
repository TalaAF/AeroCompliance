#!/usr/bin/env bash
set -Eeuo pipefail

pgdata="${PGDATA:?PGDATA is required}"
pgbin=/usr/lib/postgresql/17/bin
marker="${pgdata}/.aerocompliance-initialized"

if [[ -f "${pgdata}/PG_VERSION" && ! -f "${marker}" ]]; then
    echo "Removing an incomplete first-start PostgreSQL initialization." >&2
    find "${pgdata}" -mindepth 1 -delete
fi

if [[ ! -f "${marker}" ]]; then
    echo "Initializing PostgreSQL/PostGIS demo database in ${pgdata}."
    "${pgbin}/initdb" \
        --pgdata="${pgdata}" \
        --username=postgres \
        --auth-local=trust \
        --auth-host=scram-sha-256 \
        --encoding=UTF8 \
        --no-locale

    "${pgbin}/pg_ctl" -D "${pgdata}" \
        -o "-c listen_addresses=127.0.0.1 -c port=5432" -w start

    cleanup() {
        "${pgbin}/pg_ctl" -D "${pgdata}" -m fast -w stop >/dev/null 2>&1 || true
    }
    trap cleanup EXIT

    psql --username=postgres --dbname=postgres --set=ON_ERROR_STOP=1 \
        --set=db_password="${POSTGRES_PASSWORD}" <<'SQL'
CREATE USER aerocompliance WITH PASSWORD :'db_password';
CREATE DATABASE aerocompliance OWNER aerocompliance;
\connect aerocompliance
CREATE EXTENSION IF NOT EXISTS postgis;
SQL

    cleanup
    trap - EXIT
    touch "${marker}"
    echo "PostgreSQL/PostGIS demo database initialized."
fi

exec "${pgbin}/postgres" -D "${pgdata}" -c listen_addresses=127.0.0.1 -c port=5432
