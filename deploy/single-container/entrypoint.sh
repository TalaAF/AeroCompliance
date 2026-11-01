#!/usr/bin/env bash
set -Eeuo pipefail

data_root="${AEROCOMPLIANCE_DATA_ROOT:-/var/lib/aerocompliance}"
config_dir="${data_root}/config"
config_file="${config_dir}/runtime.env"

install -d -m 0700 "${config_dir}"
install -d -m 0700 -o postgres -g postgres "${data_root}/postgres"
install -d -o rabbitmq -g rabbitmq "${data_root}/rabbitmq" "${data_root}/rabbitmq/mnesia" "${data_root}/rabbitmq/log"
install -d -o aerocompliance -g aerocompliance \
    "${data_root}/minio" "${data_root}/models" "${data_root}/app" \
    "${data_root}/app/dotnet" "${data_root}/app/ultralytics" \
    "${data_root}/app/matplotlib" "${data_root}/app/config"
install -d -o rabbitmq -g rabbitmq /run/rabbitmq
install -d -o postgres -g postgres /run/postgresql
install -d -o aerocompliance -g aerocompliance /run/aerocompliance
install -d -o www-data -g www-data \
    /var/lib/nginx/body /var/lib/nginx/proxy /var/lib/nginx/fastcgi \
    /var/lib/nginx/uwsgi /var/lib/nginx/scgi

if [[ ! -s "${config_file}" ]]; then
    umask 077
    temp_config="${config_file}.tmp"
    {
        printf 'POSTGRES_PASSWORD=%s\n' "$(openssl rand -hex 24)"
        printf 'RABBITMQ_PASSWORD=%s\n' "$(openssl rand -hex 24)"
        printf 'MINIO_SECRET_KEY=%s\n' "$(openssl rand -hex 24)"
        printf 'JWT_SIGNING_KEY=%s\n' "$(openssl rand -hex 48)"
        printf 'DEMO_PASSWORD=Demo-%s\n' "$(openssl rand -hex 10)"
        printf 'DEMO_SCENARIO_ENABLED=1\n'
    } > "${temp_config}"
    mv "${temp_config}" "${config_file}"
    chmod 0600 "${config_file}"
    first_start=true
else
    first_start=false
fi

set -a
# shellcheck disable=SC1090
source "${config_file}"
set +a

export PGDATA="${data_root}/postgres"
export HOME="${data_root}/app"
export DOTNET_CLI_HOME="${data_root}/app/dotnet"
export XDG_CONFIG_HOME="${data_root}/app/config"
export YOLO_CONFIG_DIR="${data_root}/app/ultralytics"
export MPLCONFIGDIR="${data_root}/app/matplotlib"
export POSTGRES_DB=aerocompliance
export POSTGRES_USER=aerocompliance
export POSTGRES_PASSWORD
export POSTGRES_INITDB_ARGS="--auth-host=scram-sha-256"

export RABBITMQ_DEFAULT_USER=aerocompliance
export RABBITMQ_DEFAULT_PASS="${RABBITMQ_PASSWORD}"
export RABBITMQ_NODENAME=rabbit@localhost
export RABBITMQ_MNESIA_BASE="${data_root}/rabbitmq/mnesia"
export RABBITMQ_LOG_BASE="${data_root}/rabbitmq/log"
export RABBITMQ_PID_FILE=/run/rabbitmq/rabbitmq.pid
export RABBITMQ_LOGS=-
export RABBITMQ_SASL_LOGS=-

export MINIO_ROOT_USER=aerocompliance
export MINIO_ROOT_PASSWORD="${MINIO_SECRET_KEY}"

export ASPNETCORE_ENVIRONMENT=Demo
export DOTNET_ENVIRONMENT=Demo
export ASPNETCORE_URLS=http://127.0.0.1:8081
export ConnectionStrings__aerocompliancedb="Host=127.0.0.1;Port=5432;Database=aerocompliance;Username=aerocompliance;Password=${POSTGRES_PASSWORD};Include Error Detail=false"
export ConnectionStrings__messaging="amqp://aerocompliance:${RABBITMQ_PASSWORD}@127.0.0.1:5672"
export MinIO__Endpoint=http://127.0.0.1:9000
export MinIO__AccessKey=aerocompliance
export MinIO__SecretKey="${MINIO_SECRET_KEY}"
export MinIO__Bucket=aerocompliance-images
export Jwt__Issuer=AeroCompliance
export Jwt__Audience=AeroCompliance.Clients
export Jwt__SigningKey="${JWT_SIGNING_KEY}"
export Jwt__AccessTokenMinutes=30
export Jwt__RefreshTokenDays=7
export Database__ApplyMigrationsOnStartup=true
if [[ "${DEMO_SCENARIO_ENABLED:-0}" == 1 && ! -f "${config_dir}/demo-scenario-v1.done" ]]; then
    export DemoSeed__Enabled=true
    export DEMO_SCENARIO_PENDING=1
else
    export DemoSeed__Enabled=false
    export DEMO_SCENARIO_PENDING=0
fi
export DemoSeed__Password="${DEMO_PASSWORD}"
export DemoMode__Enabled=true
export HealthChecks__Expose=true
export HttpsRedirection__Enabled=false

export AI_MODE="${AEROCOMPLIANCE_AI_MODE-yolo}"
export AI_MODEL_PATH=/opt/aerocompliance/models/best.pt
export AI_CONFIDENCE_THRESHOLD=0.25
if [[ "${AI_MODE}" == "yolo" ]]; then
    default_model_name=aerocompliance-yolo26m-seg
else
    default_model_name=aerocompliance-mock
fi
export AI_MODEL_NAME="${AEROCOMPLIANCE_AI_MODEL_NAME:-${default_model_name}}"
export AI_MODEL_VERSION="${AEROCOMPLIANCE_AI_MODEL_VERSION:-1.0}"
export RABBITMQ_URL="${ConnectionStrings__messaging}"
export MINIO_ENDPOINT=http://127.0.0.1:9000
export MINIO_ACCESS_KEY=aerocompliance
export MINIO_SECRET_KEY
export MINIO_SECURE=false
export MINIO_BUCKET=aerocompliance-images
export HEALTH_PORT=8082
export PYTHONPATH=/opt/aerocompliance/ai

if [[ "${AI_MODE}" != "mock" && "${AI_MODE}" != "yolo" ]]; then
    echo "ERROR: AEROCOMPLIANCE_AI_MODE must be 'mock' or 'yolo'." >&2
    exit 1
fi
if [[ "${AI_MODE}" == "yolo" && ! -f "${AI_MODEL_PATH}" ]]; then
    echo "ERROR: Bundled YOLO checkpoint is missing: ${AI_MODEL_PATH}." >&2
    exit 1
fi
rm -f /run/aerocompliance/public-ready
rm -f /run/aerocompliance/demo-seed-complete

chown -R postgres:postgres "${data_root}/postgres"
chmod 0700 "${data_root}/postgres"
chown -R rabbitmq:rabbitmq "${data_root}/rabbitmq"
chown -R aerocompliance:aerocompliance "${data_root}/minio" "${data_root}/models" "${data_root}/app"

echo "============================================================"
echo " AeroCompliance self-contained demo"
echo " URL: http://localhost:8080"
echo " Demo login: admin@aerocompliance.test"
echo " Demo password: ${DEMO_PASSWORD}"
if [[ "${AI_MODE}" == "mock" ]]; then
    echo " AI MODE CONFIGURED: MOCK - deterministic demo output; not real-model accuracy evidence."
else
    echo " AI MODE CONFIGURED: YOLO - bundled checkpoint: ${AI_MODEL_PATH}"
fi
if [[ "${first_start}" == true ]]; then
    echo " Generated first-start settings at ${config_file}"
else
    echo " Reusing persisted settings from ${config_file}"
fi
echo " Wait until Docker reports the container healthy before signing in."
echo "============================================================"

exec /usr/bin/supervisord -c /etc/supervisor/supervisord.conf
