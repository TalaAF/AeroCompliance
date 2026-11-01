#!/usr/bin/env bash
set -Eeuo pipefail

# The seed client uses the internal API. Hold the public endpoint until bootstrap
# finishes, so ordinary uploads cannot be processed by its temporary mock worker.
until [[ -f /run/aerocompliance/public-ready ]]; do
    sleep 1
done
exec /usr/sbin/nginx -g 'daemon off;'
