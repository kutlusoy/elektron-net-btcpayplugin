#!/usr/bin/env bash
# Prints the public watch-only descriptor of a signing wallet as a ready-to-paste stack.conf line.
# Run it where the signing wallet lives, never on the payment server.
#
#   ELEKTRON_CLI   command that runs elektron-cli, may contain arguments (default: elektron-cli)
#   WALLET         wallet name, created when it does not exist (default: merchant)
set -euo pipefail

read -r -a CLI <<< "${ELEKTRON_CLI:-elektron-cli}"
WALLET="${WALLET:-merchant}"

die() { printf 'error: %s\n' "$*" >&2; exit 1; }

info=$("${CLI[@]}" getblockchaininfo 2>&1) || die "cannot reach the node: $info"
[[ $info == *'"chain": "main"'* ]] || die "the node is not on the main chain, the descriptor would not work for the stack"

wallets=$("${CLI[@]}" listwalletdir)
if [[ $wallets == *"\"name\": \"$WALLET\""* ]]; then
    "${CLI[@]}" loadwallet "$WALLET" >/dev/null 2>&1 || true
else
    "${CLI[@]}" createwallet "$WALLET" >/dev/null
    created=1
fi

descriptor=$("${CLI[@]}" -rpcwallet="$WALLET" listdescriptors | awk '
    /"desc":/ { d = $0; sub(/.*"desc": *"/, "", d); sub(/".*/, "", d); active = 0 }
    /"active": *true/ { active = 1 }
    /"internal": *false/ && active && d ~ /^wpkh\(/ { print d; exit }')

[[ -n $descriptor ]] || die "no active external wpkh descriptor found in wallet $WALLET"
[[ $descriptor != *prv* ]] || die "the descriptor contains a private key, refusing to print it"
[[ $descriptor == *"/84h/1370h/"* ]] || die "unexpected derivation path, the stack needs 84h/1370h"

first_address=$("${CLI[@]}" deriveaddresses "$descriptor" "[0,0]" | grep -o 'be1[a-z0-9]*' | head -n 1)
[[ -n $first_address ]] || die "could not derive the first address"

echo "Paste this line into stack.conf (WALLET_MODE=watchonly):"
echo
echo "WATCHONLY_DESCRIPTOR=$descriptor"
echo
echo "First address of the wallet, the installer must print the same one: $first_address"
echo
[[ -z ${created:-} ]] || echo "The wallet $WALLET was just created."
echo "Back it up now and keep it off the payment server: elektron-cli -rpcwallet=$WALLET backupwallet <file>"
echo "Anyone holding the wallet file can spend the funds, the descriptor above is public and safe to copy."
