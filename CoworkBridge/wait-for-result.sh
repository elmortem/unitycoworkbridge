#!/usr/bin/env bash
set -euo pipefail

task="${1:?usage: wait-for-result.sh <TaskName> [timeout_seconds]}"
timeout="${2:-300}"
dir="$(cd "$(dirname "$0")" && pwd)"

elapsed=0
while [ ! -f "$dir/result_${task}.done" ]; do
	sleep 1
	elapsed=$((elapsed + 1))
	if [ "$elapsed" -ge "$timeout" ]; then
		echo "{\"status\":\"timeout\",\"error\":\"Bridge did not respond within ${timeout}s\"}"
		exit 1
	fi
done

cat "$dir/result_${task}.json"
