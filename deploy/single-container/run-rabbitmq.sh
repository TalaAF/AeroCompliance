#!/usr/bin/env bash
set -Eeuo pipefail
export HOME="${AEROCOMPLIANCE_DATA_ROOT:-/var/lib/aerocompliance}/rabbitmq"
exec /usr/lib/rabbitmq/bin/rabbitmq-server
