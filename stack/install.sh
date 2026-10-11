#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

log() { printf '==> %s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

CONF="${STACK_CONF:-stack.conf}"
[[ -f $CONF ]] || die "$CONF not found, copy stack.conf.example to $CONF and edit it"

# The file is parsed, not executed, so values like descriptors need no shell quoting
load_conf() {
    local line key value
    while IFS= read -r line || [[ -n $line ]]; do
        [[ $line =~ ^[[:space:]]*(#|$) ]] && continue
        [[ $line =~ ^([A-Z_]+)=(.*)$ ]] || die "invalid line in $CONF: $line"
        key=${BASH_REMATCH[1]}
        value=${BASH_REMATCH[2]}
        value=${value#[\"\']}
        value=${value%[\"\']}
        case $key in
            BTCPAY_HOST | BTCPAY_VERSION | ELEKTRON_NET_REF | WALLET_NAME | WALLET_MODE | WATCHONLY_DESCRIPTOR | \
                SWEEP_ADDRESS | SWEEP_THRESHOLD | SWEEP_KEEP | SWEEP_MINCONF | SWEEP_INTERVAL)
                printf -v "$key" '%s' "$value"
                ;;
            *)
                die "unknown setting $key in $CONF"
                ;;
        esac
    done < "$CONF"
}
load_conf

BTCPAY_HOST="${BTCPAY_HOST:-}"
BTCPAY_VERSION="${BTCPAY_VERSION:-2.3.7}"
ELEKTRON_NET_REF="${ELEKTRON_NET_REF:-v4.0.5}"
WALLET_NAME="${WALLET_NAME:-btcpay}"
WALLET_MODE="${WALLET_MODE:-watchonly}"
WATCHONLY_DESCRIPTOR="${WATCHONLY_DESCRIPTOR:-}"
SWEEP_ADDRESS="${SWEEP_ADDRESS:-}"
SWEEP_THRESHOLD="${SWEEP_THRESHOLD:-5}"
SWEEP_KEEP="${SWEEP_KEEP:-0}"
SWEEP_MINCONF="${SWEEP_MINCONF:-6}"
SWEEP_INTERVAL="${SWEEP_INTERVAL:-300}"
RPC_USER="btcpay"

[[ -n $BTCPAY_HOST && $BTCPAY_HOST != *example.com ]] || die "set BTCPAY_HOST in $CONF"

case $WALLET_MODE in
    watchonly)
        [[ -n $WATCHONLY_DESCRIPTOR ]] || die "WATCHONLY_DESCRIPTOR is required in watchonly mode"
        [[ $WATCHONLY_DESCRIPTOR != *prv* ]] || die "the descriptor contains a private key, use the public descriptor only"
        [[ $WATCHONLY_DESCRIPTOR == wpkh\(* ]] || die "only wpkh(...) descriptors are supported"
        [[ $WATCHONLY_DESCRIPTOR == *84h/1370h/* ]] || die "the descriptor path must contain 84h/1370h"
        ;;
    hot)
        [[ -n $SWEEP_ADDRESS ]] || die "SWEEP_ADDRESS is required in hot mode"
        [[ $SWEEP_ADDRESS == be1* ]] || die "SWEEP_ADDRESS must be a Bech32 address starting with be1"
        ;;
    *)
        die "WALLET_MODE must be watchonly or hot"
        ;;
esac

for tool in docker git openssl python3; do
    command -v "$tool" >/dev/null || die "$tool is required"
done
docker compose version >/dev/null 2>&1 || die "the docker compose plugin is required"

umask 077

if [[ ! -d elektron-src/.git ]]; then
    log "Cloning elektron-net $ELEKTRON_NET_REF"
    git clone --depth 1 --branch "$ELEKTRON_NET_REF" https://github.com/kutlusoy/elektron-net.git elektron-src
fi

env_value() { [[ -f .env ]] && sed -n "s/^$1=//p" .env | head -n 1 || true; }

RPC_PASSWORD="$(env_value RPC_PASSWORD)"
RPC_PASSWORD="${RPC_PASSWORD:-$(openssl rand -hex 24)}"
POSTGRES_PASSWORD="$(env_value POSTGRES_PASSWORD)"
POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-$(openssl rand -hex 24)}"

mkdir -p secrets
if [[ $WALLET_MODE == hot && ! -f secrets/wallet_passphrase ]]; then
    openssl rand -hex 32 > secrets/wallet_passphrase
fi

{
    echo "BTCPAY_HOST=$BTCPAY_HOST"
    echo "BTCPAY_VERSION=$BTCPAY_VERSION"
    echo "WALLET_NAME=$WALLET_NAME"
    echo "RPC_USER=$RPC_USER"
    echo "RPC_PASSWORD=$RPC_PASSWORD"
    echo "POSTGRES_PASSWORD=$POSTGRES_PASSWORD"
    if [[ $WALLET_MODE == hot ]]; then
        echo "ELEK_ALLOW_HOT_WALLET=true"
        echo "COMPOSE_PROFILES=hot"
        echo "SWEEP_ADDRESS=$SWEEP_ADDRESS"
        echo "SWEEP_THRESHOLD=$SWEEP_THRESHOLD"
        echo "SWEEP_KEEP=$SWEEP_KEEP"
        echo "SWEEP_MINCONF=$SWEEP_MINCONF"
        echo "SWEEP_INTERVAL=$SWEEP_INTERVAL"
    else
        echo "ELEK_ALLOW_HOT_WALLET=false"
    fi
} > .env

cat > cli.conf <<EOF
rpcuser=$RPC_USER
rpcpassword=$RPC_PASSWORD
rpcconnect=elektrond
rpcport=8332
EOF

if [[ ! -f elektron.conf ]]; then
    rpcauth_line="$(python3 elektron-src/share/rpcauth/rpcauth.py "$RPC_USER" "$RPC_PASSWORD" | grep '^rpcauth=')"
    cat > elektron.conf <<EOF
server=1
listen=1
bind=0.0.0.0:8333
maxconnections=125
addresstype=bech32
disablewallet=0

rpcbind=0.0.0.0
rpcallowip=172.29.88.0/24
$rpcauth_line
EOF
fi

ecli() { docker compose exec -T elektrond elektron-cli -conf=/etc/elektron/cli.conf "$@"; }
wcli() { ecli -rpcwallet="$WALLET_NAME" "$@"; }

log "Building images (the node build takes a while)"
docker compose build

log "Starting the node and the database"
docker compose up -d elektrond postgres

log "Waiting for the node RPC"
for _ in $(seq 1 60); do
    ecli getblockchaininfo >/dev/null 2>&1 && break
    sleep 5
done
ecli getblockchaininfo >/dev/null 2>&1 || die "the node RPC did not come up, check: docker compose logs elektrond"

if [[ $WALLET_MODE == hot ]]; then
    [[ $(ecli validateaddress "$SWEEP_ADDRESS") == *'"isvalid": true'* ]] || die "SWEEP_ADDRESS is not a valid address"
fi

wallet_dir="$(ecli listwalletdir)"
if [[ $wallet_dir == *"\"name\": \"$WALLET_NAME\""* ]]; then
    log "Wallet $WALLET_NAME already exists, loading it"
    ecli loadwallet "$WALLET_NAME" >/dev/null 2>&1 || true
elif [[ $WALLET_MODE == watchonly ]]; then
    log "Creating the watch-only wallet $WALLET_NAME"
    descriptor="$(ecli getdescriptorinfo "$WATCHONLY_DESCRIPTOR" | sed -n 's/.*"descriptor": *"\([^"]*\)".*/\1/p')"
    [[ -n $descriptor ]] || die "the node rejected the descriptor"
    ecli createwallet "$WALLET_NAME" true true "" false true true >/dev/null
    result="$(wcli importdescriptors "[{\"desc\":\"$descriptor\",\"active\":true,\"internal\":false,\"range\":[0,999],\"next_index\":0,\"timestamp\":\"now\"}]")"
    [[ $result == *'"success": true'* ]] || die "importing the descriptor failed: $result"
    log "First derived address, compare it with your signing wallet: $(ecli deriveaddresses "$descriptor" "[0,0]" | tr -d '[:space:]')"
else
    log "Creating the encrypted hot wallet $WALLET_NAME"
    printf 'passphrase=%s\n' "$(<secrets/wallet_passphrase)" | ecli -stdin -named createwallet \
        wallet_name="$WALLET_NAME" disable_private_keys=false blank=false avoid_reuse=false descriptors=true load_on_startup=true >/dev/null
    docker compose exec -T -u elektron elektrond mkdir -p /data/backup
    wcli backupwallet "/data/backup/$WALLET_NAME-initial.dat" >/dev/null
fi

log "Starting the stack"
docker compose up -d

{
    echo "BTCPay Server:    https://$BTCPAY_HOST"
    echo "Wallet mode:      $WALLET_MODE"
    echo "Node RPC user:    $RPC_USER"
    echo "Node RPC password: $RPC_PASSWORD"
    if [[ $WALLET_MODE == hot ]]; then
        echo "Wallet passphrase file: $PWD/secrets/wallet_passphrase"
        echo "Wallet backup (inside the node volume): /data/backup/$WALLET_NAME-initial.dat"
        echo "Sweep address:    $SWEEP_ADDRESS"
    fi
} > CREDENTIALS.txt

cat <<EOF

Done. Credentials are in $PWD/CREDENTIALS.txt (readable by root only).

Next steps:
  1. Open https://$BTCPAY_HOST and register the administrator account right away,
     the first account that registers becomes the server administrator.
  2. Create a store, then open Wallets > Elektron Net in the store and enable it.
  3. The node needs to finish synchronising before payments are accepted,
     check with: docker compose exec elektrond elektron-cli -conf=/etc/elektron/cli.conf getblockchaininfo
EOF
if [[ $WALLET_MODE == hot ]]; then
    cat <<EOF
  4. Copy the wallet backup and the passphrase file to a safe place off this server:
     docker compose cp elektrond:/data/backup/$WALLET_NAME-initial.dat .
EOF
fi
