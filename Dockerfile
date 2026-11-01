# syntax=docker/dockerfile:1.7

# Compile both .NET services for the requested Linux architecture on the build host.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
ARG TARGETARCH
WORKDIR /src
COPY src/ ./src/
RUN dotnet restore src/AeroCompliance.ApiService/AeroCompliance.ApiService.csproj -a $TARGETARCH \
    && dotnet restore src/AeroCompliance.ProcessingService/AeroCompliance.ProcessingService.csproj -a $TARGETARCH
RUN dotnet publish src/AeroCompliance.ApiService/AeroCompliance.ApiService.csproj \
        --configuration Release --no-restore --output /out/api /p:UseAppHost=false -a $TARGETARCH \
    && dotnet publish src/AeroCompliance.ProcessingService/AeroCompliance.ProcessingService.csproj \
        --configuration Release --no-restore --output /out/processing /p:UseAppHost=false -a $TARGETARCH

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/aspnet:10.0 AS dotnet-runtime

# Vite produces architecture-independent browser assets.
FROM --platform=$BUILDPLATFORM node:22-alpine AS frontend-build
WORKDIR /frontend
COPY src/AeroCompliance.Frontend/package.json src/AeroCompliance.Frontend/package-lock.json ./
RUN npm ci
COPY src/AeroCompliance.Frontend/ ./
RUN npm run build

# Install native Python wheels on the target platform. Match the final Debian release
# so the copied Python interpreter and its shared libraries remain compatible.
FROM --platform=$TARGETPLATFORM python:3.12-slim-trixie AS ai-worker
ARG AI_REQUIREMENTS=requirements-yolo.txt
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends \
        libexpat1 libxcb1 libgl1 libglib2.0-0 \
    && rm -rf /var/lib/apt/lists/*
COPY src/AeroCompliance.AiWorker/requirements.txt \
     src/AeroCompliance.AiWorker/requirements-geospatial.txt \
     src/AeroCompliance.AiWorker/requirements-yolo.txt ./
RUN if [ "${AI_REQUIREMENTS}" = "requirements-yolo.txt" ]; then \
        pip install --no-cache-dir --index-url https://download.pytorch.org/whl/cpu \
            "torch>=2.4,<3" "torchvision>=0.19,<1"; \
    fi \
    && pip install --no-cache-dir -r "${AI_REQUIREMENTS}"
COPY src/AeroCompliance.AiWorker/aerocompliance_ai ./aerocompliance_ai

# Compile MinIO for the requested target, even when this stage runs on the
# Buildx builder's architecture. Select Go's exact native or cross-build path.
FROM --platform=$BUILDPLATFORM golang:1.24-bookworm AS minio-build
ARG TARGETARCH
ARG MINIO_RELEASE=RELEASE.2025-09-07T16-13-09Z
ENV CGO_ENABLED=0 GOOS=linux
RUN GOARCH="${TARGETARCH}" go install -tags kqueue -trimpath \
    --ldflags "-s -w \
      -X github.com/minio/minio/cmd.Version=2025-09-07T16:13:09Z \
      -X github.com/minio/minio/cmd.CopyrightYear=2025 \
      -X github.com/minio/minio/cmd.ReleaseTag=${MINIO_RELEASE} \
      -X github.com/minio/minio/cmd.CommitID=07c3a429bfed \
      -X github.com/minio/minio/cmd.ShortCommitID=07c3a429bfed" \
    "github.com/minio/minio@${MINIO_RELEASE}" \
    && mkdir -p /out \
    && if [ "${TARGETARCH}" = "$(go env GOHOSTARCH)" ]; then \
        minio_binary=/go/bin/minio; \
       else \
        minio_binary="/go/bin/linux_${TARGETARCH}/minio"; \
       fi \
    && install -m 0755 "${minio_binary}" /out/minio

FROM --platform=$TARGETPLATFORM debian:trixie-slim
ARG TARGETARCH

LABEL org.opencontainers.image.title="AeroCompliance self-contained demo" \
      org.opencontainers.image.description="Single-container local demo with API, processing, AI worker, frontend, PostGIS, RabbitMQ and MinIO" \
      org.opencontainers.image.source="https://hub.docker.com/r/tala211/aerocompliance"

ENV DEBIAN_FRONTEND=noninteractive \
    AEROCOMPLIANCE_DATA_ROOT=/var/lib/aerocompliance \
    DOTNET_EnableDiagnostics=0 \
    PYTHONUNBUFFERED=1

RUN printf '#!/bin/sh\nexit 101\n' > /usr/sbin/policy-rc.d \
    && chmod +x /usr/sbin/policy-rc.d \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        bash ca-certificates curl libexpat1 libfontconfig1 libgcc-s1 libgl1 \
        libglib2.0-0 libgssapi-krb5-2 libicu76 libssl3t64 libstdc++6 libxcb1 \
        nginx openssl postgresql-17 postgresql-17-postgis-3 rabbitmq-server supervisor tzdata zlib1g \
    && rm -f /usr/sbin/policy-rc.d \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --system --home-dir /var/lib/aerocompliance/app --shell /usr/sbin/nologin aerocompliance \
    && rm -rf /var/lib/postgresql/* /var/lib/rabbitmq /var/lib/nginx /var/log/nginx /var/log/postgresql

COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet
RUN ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet

COPY --from=dotnet-build /out/api /opt/aerocompliance/api
COPY --from=dotnet-build /out/processing /opt/aerocompliance/processing
COPY --from=ai-worker /usr/local /usr/local
COPY --from=ai-worker /app/aerocompliance_ai /opt/aerocompliance/ai/aerocompliance_ai
COPY --from=frontend-build /frontend/dist /opt/aerocompliance/frontend
COPY --from=minio-build /out/minio /usr/local/bin/minio
COPY src/AeroCompliance.AiWorker/models/best.pt /opt/aerocompliance/models/best.pt
RUN echo '577ec6f619b5e22a462b0ac62d5a55722cded832ea07fbaf70f01d93786479f8  /opt/aerocompliance/models/best.pt' | sha256sum -c - \
    && chmod 0644 /opt/aerocompliance/models/best.pt
RUN minio_machine="$(od -An -tu2 -j18 -N2 /usr/local/bin/minio | tr -d '[:space:]')" \
    && case "${TARGETARCH}:${minio_machine}" in \
         amd64:62|arm64:183) ;; \
         *) echo "MinIO ELF architecture ${minio_machine} does not match ${TARGETARCH}" >&2; exit 1 ;; \
       esac

COPY deploy/single-container/supervisord.conf /etc/supervisor/supervisord.conf
COPY deploy/single-container/nginx.conf /etc/nginx/nginx.conf
COPY deploy/single-container/entrypoint.sh /usr/local/bin/aerocompliance-entrypoint
COPY deploy/single-container/run-api.sh /opt/aerocompliance/bin/run-api
COPY deploy/single-container/run-processing.sh /opt/aerocompliance/bin/run-processing
COPY deploy/single-container/run-ai-worker.sh /opt/aerocompliance/bin/run-ai-worker
COPY deploy/single-container/run-nginx.sh /opt/aerocompliance/bin/run-nginx
COPY deploy/single-container/run-postgres.sh /opt/aerocompliance/bin/run-postgres
COPY deploy/single-container/run-rabbitmq.sh /opt/aerocompliance/bin/run-rabbitmq
COPY deploy/single-container/wait-for-stack.sh /opt/aerocompliance/bin/wait-for-stack
COPY deploy/single-container/healthcheck.sh /opt/aerocompliance/bin/healthcheck
COPY deploy/single-container/demo-seed.py /opt/aerocompliance/demo-seed.py
COPY deploy/single-container/demo-assets/ /opt/aerocompliance/demo-assets/

RUN chmod 0755 \
        /usr/local/bin/aerocompliance-entrypoint \
        /opt/aerocompliance/bin/run-api \
        /opt/aerocompliance/bin/run-processing \
        /opt/aerocompliance/bin/run-ai-worker \
        /opt/aerocompliance/bin/run-nginx \
        /opt/aerocompliance/bin/run-postgres \
        /opt/aerocompliance/bin/run-rabbitmq \
        /opt/aerocompliance/bin/wait-for-stack \
        /opt/aerocompliance/bin/healthcheck \
    && install -d -o aerocompliance -g aerocompliance /var/lib/aerocompliance

EXPOSE 8080
VOLUME ["/var/lib/aerocompliance"]
STOPSIGNAL SIGTERM
HEALTHCHECK --interval=30s --timeout=10s --start-period=120s --retries=5 \
    CMD ["/opt/aerocompliance/bin/healthcheck"]
ENTRYPOINT ["/usr/local/bin/aerocompliance-entrypoint"]
