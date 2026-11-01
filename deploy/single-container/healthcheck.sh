#!/usr/bin/env bash
set -Eeuo pipefail

pg_isready -h 127.0.0.1 -p 5432 -U aerocompliance -d aerocompliance >/dev/null
bash -c 'exec 3<>/dev/tcp/127.0.0.1/5672'
curl --fail --silent http://127.0.0.1:9000/minio/health/live >/dev/null
curl --fail --silent http://127.0.0.1:8081/health >/dev/null
curl --fail --silent http://127.0.0.1:8082/ready >/dev/null
curl --fail --silent http://127.0.0.1:8080/health >/dev/null

# A fresh demo volume is ready for use only after its synthetic scenario is complete.
config_dir="${AEROCOMPLIANCE_DATA_ROOT:-/var/lib/aerocompliance}/config"
if grep -qx 'DEMO_SCENARIO_ENABLED=1' "${config_dir}/runtime.env"; then
    test -f "${config_dir}/demo-scenario-v1.done"
fi
