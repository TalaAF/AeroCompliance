#!/usr/bin/env bash
set -Eeuo pipefail

mode="${1:?usage: wait-for-stack <infrastructure|api>}"
deadline=$((SECONDS + 240))

wait_for() {
    local description="$1"
    shift
    until "$@" >/dev/null 2>&1; do
        if (( SECONDS >= deadline )); then
            echo "Timed out waiting for ${description}." >&2
            return 1
        fi
        sleep 2
    done
}

wait_for "PostgreSQL" env PGPASSWORD="${POSTGRES_PASSWORD}" psql -h 127.0.0.1 -p 5432 -U aerocompliance -d aerocompliance -Atqc "select 1"
wait_for "RabbitMQ" bash -c 'exec 3<>/dev/tcp/127.0.0.1/5672'
wait_for "MinIO" curl --fail --silent http://127.0.0.1:9000/minio/health/live

if [[ "${mode}" == "api" ]]; then
    wait_for "AeroCompliance API" curl --fail --silent http://127.0.0.1:8081/health
elif [[ "${mode}" != "infrastructure" ]]; then
    echo "Unknown wait mode: ${mode}" >&2
    exit 2
fi
