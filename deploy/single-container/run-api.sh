#!/usr/bin/env bash
set -Eeuo pipefail
/opt/aerocompliance/bin/wait-for-stack infrastructure
cd /opt/aerocompliance/api
exec dotnet AeroCompliance.ApiService.dll
