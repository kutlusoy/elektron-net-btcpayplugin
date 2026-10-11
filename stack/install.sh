#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"
SELF="$PWD/$(basename "${BASH_SOURCE[0]}")"

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
                SWEEP_ADDRESS | SWEEP_THRESHOLD | SWEEP_KEEP | SWEEP_MINCONF | SWEEP_INTERVAL | AUTO_UPDATE_STACK)
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
BTCPAY_VERSION="${BTCPAY_VERSION:-latest}"
ELEKTRON_NET_REF="${ELEKTRON_NET_REF:-latest}"
WALLET_NAME="${WALLET_NAME:-btcpay}"
WALLET_MODE="${WALLET_MODE:-watchonly}"
WATCHONLY_DESCRIPTOR="${WATCHONLY_DESCRIPTOR:-}"
SWEEP_ADDRESS="${SWEEP_ADDRESS:-}"
SWEEP_THRESHOLD="${SWEEP_THRESHOLD:-5}"
SWEEP_KEEP="${SWEEP_KEEP:-0}"
SWEEP_MINCONF="${SWEEP_MINCONF:-6}"
SWEEP_INTERVAL="${SWEEP_INTERVAL:-300}"
AUTO_UPDATE_STACK="${AUTO_UPDATE_STACK:-true}"
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

# Newest stable release tag of a repository, for example v4.0.5 (pre-releases are ignored)
latest_tag() {
    local tag
    tag="$(git ls-remote --tags --refs --sort=-v:refname "https://github.com/$1.git" 'v*' | sed 's|.*refs/tags/||' | grep -E '^v[0-9]+(\.[0-9]+)*$' | head -n 1 || true)"
    [[ -n $tag ]] || die "could not determine the latest release of $1, set it in $CONF"
    echo "$tag"
}

# The stack files live in this repository, so the script updates itself first and restarts once
if [[ $AUTO_UPDATE_STACK == true && -z ${ELEKTRON_STACK_UPDATED:-} ]] && git rev-parse --git-dir >/dev/null 2>&1; then
    if [[ -z $(git status --porcelain --untracked-files=no) ]]; then
        before="$(git rev-parse HEAD)"
        if git pull --ff-only --quiet; then
            if [[ $before != "$(git rev-parse HEAD)" ]]; then
                log "Stack files updated, restarting the installer"
                ELEKTRON_STACK_UPDATED=1 exec bash "$SELF" "$@"
            fi
        else
            log "Could not fast-forward the stack files, continuing with the local copy"
        fi
    else
        log "Local changes in the stack files, skipping the self-update"
    fi
fi

NODE_REF="$ELEKTRON_NET_REF"
[[ $NODE_REF != latest ]] || NODE_REF="$(latest_tag kutlusoy/elektron-net)"

BTCPAY_TARGET="$BTCPAY_VERSION"
if [[ $BTCPAY_TARGET == latest ]]; then
    BTCPAY_TARGET="$(latest_tag btcpayserver/btcpayserver)"
    BTCPAY_TARGET="${BTCPAY_TARGET#v}"
    docker manifest inspect "btcpayserver/btcpayserver:$BTCPAY_TARGET" >/dev/null 2>&1 \
        || die "no Docker image btcpayserver/btcpayserver:$BTCPAY_TARGET yet, pin BTCPAY_VERSION to the previous release in $CONF"
fi

if [[ ! -d elektron-src/.git ]]; then
    log "Cloning elektron-net $NODE_REF"
    git clone --depth 1 --branch "$NODE_REF" https://github.com/kutlusoy/elektron-net.git elektron-src
else
    node_current="$(git -C elektron-src describe --tags --exact-match 2>/dev/null || git -C elektron-src rev-parse --short HEAD)"
    if [[ $node_current == "$NODE_REF" ]]; then
        log "elektron-net is up to date ($NODE_REF)"
    else
        [[ -z $(git -C elektron-src status --porcelain --untracked-files=no) ]] || die "elektron-src has local changes, remove them or delete elektron-src/"
        log "Updating elektron-net $node_current -> $NODE_REF"
        git -C elektron-src fetch --quiet --depth 1 origin "refs/tags/$NODE_REF:refs/tags/$NODE_REF"
        git -C elektron-src checkout --quiet "$NODE_REF"
    fi
fi

env_value() { [[ -f .env ]] && sed -n "s/^$1=//p" .env | head -n 1 || true; }

RPC_PASSWORD="$(env_value RPC_PASSWORD)"
RPC_PASSWORD="${RPC_PASSWORD:-$(openssl rand -hex 24)}"
POSTGRES_PASSWORD="$(env_value POSTGRES_PASSWORD)"
POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-$(openssl rand -hex 24)}"

BTCPAY_PREVIOUS="$(env_value BTCPAY_VERSION)"
if [[ -n $BTCPAY_PREVIOUS && $BTCPAY_PREVIOUS != "$BTCPAY_TARGET" ]]; then
    log "Updating BTCPay Server $BTCPAY_PREVIOUS -> $BTCPAY_TARGET"
else
    log "BTCPay Server $BTCPAY_TARGET"
fi
[[ ! -f .env ]] || cp .env .env.bak

mkdir -p secrets
if [[ $WALLET_MODE == hot && ! -f secrets/wallet_passphrase ]]; then
    openssl rand -hex 32 > secrets/wallet_passphrase
fi

{
    echo "BTCPAY_HOST=$BTCPAY_HOST"
    echo "BTCPAY_VERSION=$BTCPAY_TARGET"
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
fallbackfee=0.0001

seednode=seeder.eleknet.org
seednode=seed0.eleknet.org
addnode=node1.elektron-net.org:8333
addnode=node2.elektron-net.org:8333

rpcbind=172.29.88.251
rpcallowip=172.29.88.0/24
$rpcauth_line
EOF
fi

conf_changed=0
if grep -q '^rpcbind=0.0.0.0$' elektron.conf; then
    sed -i 's/^rpcbind=0.0.0.0$/rpcbind=172.29.88.251/' elektron.conf
    conf_changed=1
fi
if ! grep -q '^fallbackfee=' elektron.conf; then
    printf '\nfallbackfee=0.0001\n' >> elektron.conf
    conf_changed=1
fi

ecli() { docker compose exec -T elektrond elektron-cli -conf=/etc/elektron/cli.conf "$@"; }
wcli() { ecli -rpcwallet="$WALLET_NAME" "$@"; }

log "Building images (the node build takes a while)"
if ! docker compose build; then
    [[ ! -f .env.bak ]] || mv .env.bak .env
    die "the build failed, the running stack was not touched. A new release may not work with this plugin yet, pin ELEKTRON_NET_REF or BTCPAY_VERSION to the previous release in $CONF"
fi
rm -f .env.bak

log "Starting the node and the database"
docker compose up -d elektrond postgres
[[ $conf_changed == 0 ]] || docker compose restart elektrond

log "Waiting for the node RPC"
for _ in $(seq 1 60); do
    ecli getblockchaininfo >/dev/null 2>&1 && break
    sleep 5
done
ecli getblockchaininfo >/dev/null 2>&1 || die "the node RPC did not come up, check: docker compose logs elektrond"

if [[ $WALLET_MODE == hot ]]; then
    [[ $(ecli validateaddress "$SWEEP_ADDRESS") == *'"isvalid": true'* ]] || die "SWEEP_ADDRESS is not a valid address"
fi

descriptor=""
first_address=""
if [[ $WALLET_MODE == watchonly ]]; then
    descriptor="$(ecli getdescriptorinfo "$WATCHONLY_DESCRIPTOR" | sed -n 's/.*"descriptor": *"\([^"]*\)".*/\1/p')"
    [[ -n $descriptor ]] || die "the node rejected the descriptor"
    first_address="$(ecli deriveaddresses "$descriptor" "[0,0]" | grep -o 'be1[a-z0-9]*' | head -n 1)"
    [[ -n $first_address ]] || die "could not derive the first address"
fi

wallet_dir="$(ecli listwalletdir)"
wallet_backup=""
if [[ $wallet_dir == *"\"name\": \"$WALLET_NAME\""* ]]; then
    log "Wallet $WALLET_NAME already exists, loading it"
    ecli loadwallet "$WALLET_NAME" >/dev/null 2>&1 || true
    if [[ $WALLET_MODE == hot ]]; then
        wallet_backup="/data/backup/$WALLET_NAME-$(date -u +%Y%m%dT%H%M%SZ).dat"
        docker compose exec -T -u elektron elektrond mkdir -p /data/backup
        wcli backupwallet "$wallet_backup" >/dev/null
        log "Wallet backup written to $wallet_backup (inside the node volume)"
    fi
elif [[ $WALLET_MODE == watchonly ]]; then
    log "Creating the watch-only wallet $WALLET_NAME"
    ecli createwallet "$WALLET_NAME" true true "" false true true >/dev/null
    result="$(wcli importdescriptors "[{\"desc\":\"$descriptor\",\"active\":true,\"internal\":false,\"range\":[0,999],\"next_index\":0,\"timestamp\":\"now\"}]")"
    [[ $result == *'"success": true'* ]] || die "importing the descriptor failed: $result"
    log "First derived address, compare it with your signing wallet: $first_address"
else
    log "Creating the encrypted hot wallet $WALLET_NAME"
    printf 'passphrase=%s\n' "$(<secrets/wallet_passphrase)" | ecli -stdin -named createwallet \
        wallet_name="$WALLET_NAME" disable_private_keys=false blank=false avoid_reuse=false descriptors=true load_on_startup=true >/dev/null
    docker compose exec -T -u elektron elektrond mkdir -p /data/backup
    wallet_backup="/data/backup/$WALLET_NAME-initial.dat"
    wcli backupwallet "$wallet_backup" >/dev/null
fi

log "Starting the stack"
docker compose up -d

{
    cat <<EOF
ELEKTRON NET PAYMENT STACK, DATA TO SECURE
Written by install.sh on $(date -u +%FT%TZ), rewritten on every run from the existing .env, secrets/ and elektron.conf.
Keep a copy off this server, in a password manager or on an encrypted drive. Whoever reads this file can use the node RPC.

VERSIONS
  elektron-net:     $NODE_REF
  BTCPay Server:    $BTCPAY_TARGET (plugin built against it)
  Stack files:      $(git rev-parse --short HEAD 2>/dev/null || echo unknown)

ACCESS
  BTCPay Server:    https://$BTCPAY_HOST
  Node RPC user:    $RPC_USER
  Node RPC password: $RPC_PASSWORD
  Database password: $POSTGRES_PASSWORD
  Node RPC is internal only (172.29.88.0/24), it is not published on the host

WALLET
  Name:             $WALLET_NAME
  Mode:             $WALLET_MODE
EOF
    if [[ $WALLET_MODE == hot ]]; then
        cat <<EOF
  Wallet passphrase: $(<secrets/wallet_passphrase)
  Latest wallet backup (inside the node volume): $wallet_backup
  Sweep address:    $SWEEP_ADDRESS
  Sweep rule:       above $SWEEP_KEEP + $SWEEP_THRESHOLD ELEK with $SWEEP_MINCONF confirmations, every ${SWEEP_INTERVAL}s
  Copy a backup out: docker compose cp elektrond:$wallet_backup .
  The wallet file and the passphrase are the only things that protect these funds. Store both off this server.
EOF
    else
        cat <<EOF
  Public descriptor: $descriptor
  First address (index 0): $first_address
  The private keys are NOT on this server. Back up the signing wallet and its seed offline, that is the only way to spend.
EOF
    fi
    cat <<EOF

FILES TO KEEP (in $PWD)
  stack.conf, .env, cli.conf, elektron.conf, secrets/ (modes 600, excluded from Git)

BACKUP COMMANDS
  BTCPay database:  docker compose exec -T postgres pg_dump -U btcpay btcpayserver > btcpay-db.sql
  Node data:        not required, it can be re-synchronised

UPDATE
  Run bash install.sh again, existing secrets, wallet and elektron.conf are kept.
EOF
} > CREDENTIALS.txt

cat <<EOF

Done. All data to secure is in $PWD/CREDENTIALS.txt (readable by root only), copy it off this server.

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
     docker compose cp elektrond:$wallet_backup .
EOF
fi
