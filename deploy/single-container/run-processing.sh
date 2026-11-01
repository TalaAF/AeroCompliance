#!/usr/bin/env bash
set -Eeuo pipefail
/opt/aerocompliance/bin/wait-for-stack api
cd /opt/aerocompliance/processing
exec dotnet AeroCompliance.ProcessingService.dll
