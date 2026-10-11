#!/usr/bin/env bash
set -euo pipefail

: "${SWEEP_ADDRESS:?SWEEP_ADDRESS is required}"
WALLET_NAME="${WALLET_NAME:-btcpay}"
SWEEP_THRESHOLD="${SWEEP_THRESHOLD:-5}"
SWEEP_KEEP="${SWEEP_KEEP:-0}"
SWEEP_MINCONF="${SWEEP_MINCONF:-6}"
SWEEP_INTERVAL="${SWEEP_INTERVAL:-300}"
SWEEP_ONCE="${SWEEP_ONCE:-0}"
PASSPHRASE_FILE="${PASSPHRASE_FILE:-/run/secrets/wallet_passphrase}"
CLI_CONF="${CLI_CONF:-/etc/elektron/cli.conf}"

log() { printf '%s %s\n' "$(date -u +%FT%TZ)" "$*"; }

cli() { elektron-cli -conf="$CLI_CONF" -rpcwallet="$WALLET_NAME" "$@"; }

# Amounts are handled as integer lepton (1 ELEK = 100000000) to avoid floating point errors
to_lepton() {
    local value=$1 whole frac=""
    whole=${value%%.*}
    [[ $value == *.* ]] && frac=${value#*.}
    frac=$(printf '%s00000000' "$frac")
    frac=${frac:0:8}
    echo $((10#${whole:-0} * 100000000 + 10#$frac))
}

to_elek() { printf '%d.%08d' $(($1 / 100000000)) $(($1 % 100000000)); }

lock_wallet() { cli walletlock >/dev/null 2>&1 || true; }

sweep_once() {
    local balance excess amount txid
    balance=$(cli getbalance "*" "$SWEEP_MINCONF")
    excess=$(($(to_lepton "$balance") - $(to_lepton "$SWEEP_KEEP")))

    if ((excess < $(to_lepton "$SWEEP_THRESHOLD"))); then
        log "balance $balance is below the sweep threshold, nothing to do"
        return 0
    fi

    amount=$(to_elek "$excess")
    # The passphrase is passed on stdin so it never appears in a process list
    printf 'passphrase=%s\n' "$(<"$PASSPHRASE_FILE")" | cli -stdin -named walletpassphrase timeout=60 >/dev/null
    if ! txid=$(cli -named sendtoaddress address="$SWEEP_ADDRESS" amount="$amount" subtractfeefromamount=true); then
        lock_wallet
        log "sweep of $amount ELEK failed"
        return 1
    fi
    lock_wallet
    log "swept $amount ELEK to $SWEEP_ADDRESS in $txid"
}

until cli getwalletinfo >/dev/null 2>&1; do
    log "waiting for the node and the wallet $WALLET_NAME"
    sleep 10
done

if [[ $(cli validateaddress "$SWEEP_ADDRESS") != *'"isvalid": true'* ]]; then
    log "SWEEP_ADDRESS is not a valid address, refusing to start"
    exit 1
fi

log "sweeping confirmed balance above $SWEEP_KEEP + $SWEEP_THRESHOLD ELEK to $SWEEP_ADDRESS every ${SWEEP_INTERVAL}s"
while true; do
    sweep_once || true
    [[ $SWEEP_ONCE == 1 ]] && break
    sleep "$SWEEP_INTERVAL"
done
