#!/usr/bin/env bash
# Measures how long a node needs from the first downloaded header until the initial sync is finished.
#
#   ELEKTRON_CLI   command that runs elektron-cli, may contain arguments (default: elektron-cli)
#   LOG_FILE       optional path to debug.log, adds the timestamps of the [snapshot] phases
#   INTERVAL       seconds between two polls (default: 10), also the resolution of the result
#   OUT            CSV file for the progress samples
#   MAX_WAIT       give up after this many seconds, 0 means never (default: 0)
set -euo pipefail

read -r -a CLI <<< "${ELEKTRON_CLI:-elektron-cli}"
INTERVAL="${INTERVAL:-10}"
MAX_WAIT="${MAX_WAIT:-0}"
LOG_FILE="${LOG_FILE:-}"
OUT="${OUT:-measure-sync-$(date -u +%Y%m%dT%H%M%SZ).csv}"

iso() { date -u -d "@$1" +%FT%TZ; }

duration() {
    local s=$1
    printf '%02d:%02d:%02d' $((s / 3600)) $((s % 3600 / 60)) $((s % 60))
}

json_field() { sed -n "s/.*\"$1\": *\([^,}]*\).*/\1/p" <<< "$2" | head -n 1 | tr -d ' "'; }

# Epoch of the first log line matching a pattern, empty if there is none
log_time() {
    local line stamp
    [[ -n $LOG_FILE && -r $LOG_FILE ]] || return 0
    line=$(grep -m 1 -F "$1" "$LOG_FILE" || true)
    [[ -n $line ]] || return 0
    stamp=${line%% *}
    date -u -d "$stamp" +%s 2>/dev/null || true
}

phase() {
    local label=$1 epoch=$2
    if [[ -n $epoch ]]; then
        printf '  %-28s %s\n' "$label" "$(iso "$epoch")"
    else
        printf '  %-28s %s\n' "$label" "not found in the log"
    fi
}

echo "epoch,time,blocks,headers,verificationprogress,initialblockdownload" > "$OUT"
echo "Polling every ${INTERVAL}s, samples go to $OUT"

started=$(date -u +%s)
t_headers=""
h_first=""

while true; do
    now=$(date -u +%s)
    if ((MAX_WAIT > 0 && now - started > MAX_WAIT)); then
        echo "Gave up after ${MAX_WAIT}s without a finished sync" >&2
        exit 2
    fi

    if info=$("${CLI[@]}" getblockchaininfo 2>/dev/null); then
        blocks=$(json_field blocks "$info")
        headers=$(json_field headers "$info")
        progress=$(json_field verificationprogress "$info")
        ibd=$(json_field initialblockdownload "$info")
        echo "$now,$(iso "$now"),$blocks,$headers,$progress,$ibd" >> "$OUT"

        if [[ -z $t_headers && ${headers:-0} -gt 0 ]]; then
            t_headers=$now
            h_first=$headers
            echo "$(iso "$now") first headers received ($headers)"
        fi

        if [[ -n $t_headers && $ibd == false && ${blocks:-0} -gt 0 ]]; then
            t_synced=$now
            break
        fi
        echo "$(iso "$now") blocks=$blocks headers=$headers progress=$progress"
    else
        echo "$(iso "$now") node not reachable yet"
    fi
    sleep "$INTERVAL"
done

t_snap_start=$(log_time "[snapshot] Initialized download")
t_snap_done=$(log_time "[snapshot] Download complete")
t_snap_active=$(log_time "[snapshot] Successfully activated")

echo
echo "Result"
phase "first headers received" "$t_headers"
phase "snapshot download started" "$t_snap_start"
phase "snapshot download complete" "$t_snap_done"
phase "snapshot activated" "$t_snap_active"
phase "initial sync finished" "$t_synced"
echo
echo "  headers at the first sample  $h_first"
echo "  height at the end            $blocks"
echo "  first headers -> synced      $(duration $((t_synced - t_headers))) ($((t_synced - t_headers)) s)"
if [[ -n $t_snap_start && -n $t_snap_done ]]; then
    echo "  snapshot download            $(duration $((t_snap_done - t_snap_start)))"
fi
if [[ -n $t_snap_active ]]; then
    echo "  first headers -> snapshot active  $(duration $((t_snap_active - t_headers)))"
    echo "  snapshot active -> synced    $(duration $((t_synced - t_snap_active)))"
fi
echo "  resolution of the figures    ${INTERVAL}s"
