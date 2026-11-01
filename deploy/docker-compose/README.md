# AeroCompliance Docker Compose

Requirements: Docker Engine or Docker Desktop using Linux AMD64 containers, with Docker Compose v2.

1. Download `compose.yaml` and `.env.example` into the same directory.
2. Copy `.env.example` to `.env` and replace every `change-me` value.
3. Run:

   ```sh
   docker compose pull
   docker compose up -d
   ```

4. Open http://localhost:8080 and sign in as `admin@aerocompliance.test` with `DEMO_PASSWORD` from
   `.env`.

Data persists in named PostgreSQL, RabbitMQ and MinIO volumes. Stop without deleting data using
`docker compose down`.

## Processing upgrade

The updated AI worker needs `ConnectionStrings__aerocompliancedb` and the additive
`MultiInstanceProcessing` migration before consuming jobs. On an existing deployment,
back up PostgreSQL, apply the migration with the repository's EF migration procedure,
then start updated API and worker images. Production API startup does not apply migrations
automatically. Keep the named volumes; do not use `docker compose down -v`.

Failed outbox messages stay in the database after five publish attempts. See
`docs/architecture/multi-instance-processing-2026-11-01.md` for inspection and selective
retry SQL.

## Trusted frontend proxy

The frontend nginx container reaches the API on the private `backend` Docker network. The API
trusts forwarded client addresses **only** from the frontend's exact backend-network IP. The
defaults in `.env.example` reserve `172.30.250.254` for nginx inside `172.30.250.0/24`; nginx
replaces any client-supplied `X-Forwarded-For` header with the address of the peer it actually
received. Other containers and direct API clients cannot select their audit IP or evade the
IP-based login limit by sending forwarded headers.

Before starting a new deployment, confirm the subnet does not overlap a Docker, VPN or site
network. If it does, change **both** `AEROCOMPLIANCE_BACKEND_SUBNET` and
`AEROCOMPLIANCE_FRONTEND_BACKEND_IP` in `.env`; the frontend IP must be inside the chosen subnet
and must not be the gateway or another container's address. Check the rendered settings with
`docker compose config` before `docker compose up -d`. On an existing Compose deployment, changing
the backend subnet requires a maintenance window to recreate that Docker network; the named data
volumes are independent and must be retained. This repository change does not recreate it.

The public bind defaults to `127.0.0.1`. If another reverse proxy sits in front of nginx, nginx
records that immediate peer's IP by default. Configure a separate trusted ingress policy there
before relying on the original public client IP; do not pass through arbitrary inbound
`X-Forwarded-For` values.

The default `AEROCOMPLIANCE_AI_MODE=mock` is deterministic demonstration output, not model-accuracy
evidence. For real YOLO inference, create the directory configured by `AEROCOMPLIANCE_MODEL_DIR`, put
the approved weights at `best.pt`, set `AEROCOMPLIANCE_AI_MODE=yolo`, and run `docker compose up -d`
again. The model is mounted read-only and is not included in the image.

These images target `linux/amd64`. They were built and checked under AMD64 emulation on an ARM64
Docker host; the complete workflow has not been verified on a native AMD64 machine.
