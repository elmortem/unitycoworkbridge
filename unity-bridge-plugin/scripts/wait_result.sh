#!/bin/bash

TASK_ID=$1
TIMEOUT=${2:-60}
DONE_FILE="Assets/Editor/CoworkBridge/result_${TASK_ID}.done"
RESULT_FILE="Assets/Editor/CoworkBridge/result_${TASK_ID}.json"

elapsed=0
while [ ! -f "$DONE_FILE" ]; do
    sleep 1
    elapsed=$((elapsed + 1))
    if [ $elapsed -ge $TIMEOUT ]; then
        echo '{"status":"timeout","error":"Bridge did not respond within timeout"}'
        exit 1
    fi
done

cat "$RESULT_FILE"
