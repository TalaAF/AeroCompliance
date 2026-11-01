FROM --platform=$BUILDPLATFORM golang:1.24-bookworm AS build

ARG TARGETARCH
ARG MINIO_RELEASE=RELEASE.2025-09-07T16-13-09Z

ENV CGO_ENABLED=0 \
    GOOS=linux

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

FROM debian:bookworm-slim
ARG TARGETARCH

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /out/minio /usr/local/bin/minio
RUN minio_machine="$(od -An -tu2 -j18 -N2 /usr/local/bin/minio | tr -d '[:space:]')" \
    && case "${TARGETARCH}:${minio_machine}" in \
         amd64:62|arm64:183) ;; \
         *) echo "MinIO ELF architecture ${minio_machine} does not match ${TARGETARCH}" >&2; exit 1 ;; \
       esac

EXPOSE 9000 9001
ENTRYPOINT ["/usr/local/bin/minio"]
