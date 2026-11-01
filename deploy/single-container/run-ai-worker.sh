#!/usr/bin/env bash
set -Eeuo pipefail

/opt/aerocompliance/bin/wait-for-stack api
cd /opt/aerocompliance/ai

seed_signal=/run/aerocompliance/demo-seed-complete
ready_file=/run/aerocompliance/public-ready
worker_pid=

stop_worker() {
    if [[ -n "${worker_pid}" ]] && kill -0 "${worker_pid}" 2>/dev/null; then
        kill -TERM "${worker_pid}"
        wait "${worker_pid}" || true
    fi
}
trap stop_worker EXIT TERM INT

wait_for_worker() {
    local deadline=$((SECONDS + 180))
    until curl --fail --silent http://127.0.0.1:8082/ready >/dev/null; do
        if ! kill -0 "${worker_pid}" 2>/dev/null; then
            wait "${worker_pid}"
            return 1
        fi
        if (( SECONDS >= deadline )); then
            echo 'Timed out waiting for the AI worker.' >&2
            return 1
        fi
        sleep 2
    done
}

if [[ "${DEMO_SCENARIO_PENDING:-0}" == 1 ]]; then
    echo 'AI BOOTSTRAP: generating synthetic fixtures with a temporary MOCK worker.'
    AI_MODE=mock AI_MODEL_NAME=aerocompliance-mock python -m aerocompliance_ai.main &
    worker_pid=$!
    wait_for_worker
    until [[ -f "${seed_signal}" ]]; do
        if ! kill -0 "${worker_pid}" 2>/dev/null; then
            wait "${worker_pid}"
            exit 1
        fi
        sleep 2
    done
    if [[ "${AI_MODE}" == mock ]]; then
        echo 'AI MODE ACTIVE: MOCK. Synthetic results remain labelled as mock.'
        touch "${ready_file}"
        wait "${worker_pid}"
        exit $?
    fi
    stop_worker
    worker_pid=
    echo 'AI BOOTSTRAP: synthetic fixtures complete; switching to YOLO.'
fi

python -m aerocompliance_ai.main &
worker_pid=$!
wait_for_worker
if [[ "${AI_MODE}" == yolo ]]; then
    echo "AI MODE ACTIVE: YOLO. Checkpoint: ${AI_MODEL_PATH}."
else
    echo 'AI MODE ACTIVE: MOCK. Synthetic results remain labelled as mock.'
fi
touch "${ready_file}"
wait "${worker_pid}"
