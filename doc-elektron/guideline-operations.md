# Elektron Net: `elektron-net-btcpayplugin` Operations Guideline

- **Version:** 0.1 (draft)
- **Date:** October 11, 2026
- **Audience:** Merchants and server operators who want to accept ELEK through BTCPay Server
- **Reference implementation:** [`elektron-net`](https://github.com/kutlusoy/elektron-net) (`TECHNICAL_SETUP.md`, `Quick-Install.md`, `src/wallet/rpc/`, `src/kernel/chainparams.cpp`), treat as ground truth for anything this doc references
- **Consumer:** this plugin, see [`README.MD`](../README.MD)
- **See also:** [`elektron-net-stack`](https://github.com/kutlusoy/elektron-net-stack) (Docker node setup), [`guideline-wallet-integration.md`](https://github.com/kutlusoy/elektron-net/blob/main/doc-elektron/guideline-wallet-integration.md) (derivation paths, address safety), [`elektron-net-payments`](https://github.com/kutlusoy/elektron-net-payments) (`pay-server`, escrow)

Requirement levels follow common practice: **MUST** = mandatory for correct or safe operation, **SHOULD** = strongly recommended, **MAY** = optional.

This guideline describes how to set up the plugin from scratch, how to operate it, and how to switch it off again. Section 12 lists what was checked against source code and what has not yet been run on a real system. Nothing here has been tested end to end.

---

## 1. What You Are Building

```
  Customer wallet
        |  pays to a be1q... address
        v
  elektrond (pruned node)  <--- JSON-RPC --->  BTCPay Server + ELEK plugin
  watch-only wallet "btcpay"                    (invoices, checkout, webhooks)

  Signing wallet (holds the private keys, kept offline)
  spends the received funds later
```

| Part | Holds private keys? | Runs where |
|---|---|---|
| Signing wallet | **Yes** | Offline or on a machine you fully control, never on the BTCPay host |
| `elektrond` with watch-only wallet `btcpay` | No (public descriptor only) | Same host as BTCPay Server, or a host reachable over a private network |
| BTCPay Server with the ELEK plugin | No | Linux server (production) or a workstation (testing) |

The plugin refuses to run against a wallet that has private keys enabled.

## 2. Choose a Deployment Model

| Model | Layout | Use for |
|---|---|---|
| A. Single server (recommended) | BTCPay Server and `elektrond` as Docker containers on one Linux host | Production |
| B. Split | BTCPay Server on host X, `elektrond` on host Y, connected by a private network (WireGuard or SSH tunnel) | Existing BTCPay hosts, separate node operations |
| C. Workstation | Everything on one Windows, macOS or Linux machine | Evaluation and development only |

Platform support:

| Component | Linux | Windows | macOS |
|---|---|---|---|
| `elektrond` | Docker image built from source, or native build | Release installer or portable zip | Native build from source |
| BTCPay Server | Docker (`btcpayserver-docker`), recommended | Docker Desktop (testing only) | Docker Desktop (testing only) |
| Plugin build (.NET 10 SDK) | Yes | Yes | Yes |

The `elektron-net` releases (latest at the time of writing: v4.0.5) ship Windows binaries only. On Linux and macOS the node is built from source.

## 3. Fixed Facts

| Item | Value |
|---|---|
| Daemon, CLI | `elektrond`, `elektron-cli` |
| Default data directory | `~/.bitcoin` (Linux, macOS), `%APPDATA%\Bitcoin` (Windows) |
| Config file | `bitcoin.conf` by default, any name with `-conf=`; the Docker stack uses `elektron.conf` |
| Mainnet ports | P2P `8333`, RPC `8332` |
| Testnet3 / Regtest ports | P2P `18333` / `18444`, RPC `18332` / `18443` |
| Address format | Bech32 with prefix `be` (`be1q...`) |
| Legacy addresses | Identical to Bitcoin mainnet, MUST NOT be used for receiving (misdirected funds risk) |
| Unit | 1 ELEK = 100,000,000 lepton |
| Block time | 60 seconds |
| Pruning | Mandatory on every node, 197,280 blocks (about 137 days), no archival mode |
| Account path | `m/84'/1370'/0'` (SLIP-44 coin type 1370) |

## 4. Before You Start

- [ ] A Linux server with a public IPv4 address for model A (4 GB RAM, 2 vCPU and 50 GB disk are a sensible floor for node plus BTCPay; BTCPay's default Bitcoin chain needs far more disk)
- [ ] A DNS name for BTCPay Server (for example `pay.example.com`) pointing to the server
- [ ] A second machine or a clean, offline environment for the signing wallet
- [ ] .NET 10 SDK on one machine to build the plugin
- [ ] Ports: `80` and `443` for BTCPay, `8333` for the node (optional, helps the network), nothing else public

## 5. Part A: The Node

### 5.1 Linux with Docker (recommended for servers)

The `elektron-net-stack` repository contains a Dockerfile and entrypoint for the node. The image is built from the `elektron-net` source, there is no published image.

```bash
sudo mkdir -p /opt/elektron && cd /opt/elektron
git clone https://github.com/kutlusoy/elektron-net.git
git clone https://github.com/kutlusoy/elektron-net-stack.git
cp elektron-net-stack/elektron-net/Dockerfile elektron-net/Dockerfile
cp elektron-net-stack/elektron-net/docker-entrypoint.sh elektron-net/docker-entrypoint.sh
docker build -t elektrond:local ./elektron-net
```

Generate RPC credentials (prints an `rpcauth=` line and the plaintext password):

```bash
python3 elektron-net/share/rpcauth/rpcauth.py btcpay
```

Create `/opt/elektron/elektron.conf`:

```ini
server=1
listen=1
bind=0.0.0.0:8333
maxconnections=125
addresstype=bech32
disablewallet=0

rpcbind=0.0.0.0
rpcallowip=172.16.0.0/12
rpcauth=btcpay:<salt>$<hash>
```

Run the container (the RPC port is never published to the host):

```bash
docker network create elektron 2>/dev/null
docker run -d --name elektrond --restart unless-stopped \
  --network elektron -p 8333:8333 \
  -v /opt/elektron/data:/data \
  -v /opt/elektron/elektron.conf:/etc/elektron/elektron.conf:ro \
  elektrond:local \
  elektrond -conf=/etc/elektron/elektron.conf -datadir=/data -printtoconsole
```

The configuration file is mounted outside of `/data` on purpose: the entrypoint runs `chown -R` on `/data` at every start.

All later `elektron-cli` calls in this document use the form `ecli <args>`. Define it once:

```bash
alias ecli='docker exec elektrond elektron-cli'
```

If BTCPay Server runs in its own Docker network, attach the node to it later (section 7.2) so BTCPay can reach `http://elektrond:8332`.

### 5.2 Linux, native build

Follow `doc/build-unix.md` in `elektron-net` for dependencies, then:

```bash
git clone https://github.com/kutlusoy/elektron-net.git
cd elektron-net
mkdir build && cd build
cmake .. -DBUILD_GUI=OFF
cmake --build . -j$(nproc)
sudo cmake --install .
```

Create a service user and `/etc/elektron/elektron.conf` as above, with `rpcbind=127.0.0.1` and `rpcallowip=127.0.0.1`, then a systemd unit:

```ini
[Unit]
Description=Elektron Net daemon
After=network-online.target

[Service]
User=elektron
ExecStart=/usr/local/bin/elektrond -conf=/etc/elektron/elektron.conf -datadir=/var/lib/elektron -printtoconsole
Restart=on-failure
TimeoutStopSec=300

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now elektron
alias ecli='elektron-cli -conf=/etc/elektron/elektron.conf -datadir=/var/lib/elektron'
```

### 5.3 Windows

1. Download `elektron-net-windows-v4.0.5_setup.exe` (installer) or `..._portable.zip` from the [releases page](https://github.com/kutlusoy/elektron-net/releases) and check the SHA-256 digest shown there.
2. Create `%APPDATA%\Bitcoin\bitcoin.conf`:

```ini
server=1
listen=1
addresstype=bech32
disablewallet=0
rpcbind=127.0.0.1
rpcallowip=127.0.0.1
rpcuser=btcpay
rpcpassword=<long random password>
```

3. Start the daemon from a terminal: `elektrond.exe -printtoconsole`.
4. Use `elektron-cli.exe` for all commands (`ecli` below).

For a permanent installation run `elektrond.exe` as a service (for example with NSSM) or as a scheduled task at boot. If a release archive only contains the GUI, build from source with `Quick-Install.md` instead.

### 5.4 macOS

Build from source following `doc/build-osx.md`, use the Linux native layout (data directory `~/.bitcoin`, `bitcoin.conf`), and start `elektrond` with `launchd` or in a terminal for testing.

### 5.5 Wait for Synchronisation

```bash
ecli getblockchaininfo
```

The node is ready when `initialblockdownload` is `false`. Do not continue before that. The plugin reports the payment method as unavailable while the node is syncing.

## 6. Part B: Keys and the Watch-Only Wallet

The goal: BTCPay can create receiving addresses and see payments, but cannot spend anything.

### 6.1 Create the Signing Wallet (offline)

On a secure machine with its own `elektrond` (an offline or air-gapped host is best; the node does not need to be synced to export descriptors):

```bash
ecli createwallet merchant
ecli -rpcwallet=merchant listdescriptors
```

`listdescriptors` without the `true` flag prints public descriptors only. Pick the entry that is `"active": true` and `"internal": false` with a `wpkh(...)` descriptor. It looks like this:

```
wpkh([a1b2c3d4/84h/1370h/0h]xpub6.../0/*)#checksum
```

Checks:

- The path MUST show `84h/1370h/0h`. If it shows `0h` as coin type, the wallet predates the SLIP-44 change and you should create a new wallet.
- Use the `wpkh` (native SegWit) descriptor only. Do not import the legacy `pkh` descriptor, it produces addresses that are also valid on Bitcoin mainnet.
- Never copy a descriptor that contains `xprv`. Never run `listdescriptors true` on a machine that is connected to BTCPay.
- Back up the wallet offline right after creation (`backupwallet`, plus a written record of the descriptors from `listdescriptors true` on the offline machine only). See `howto-coinsweep-legacy-wallet.md` in `elektron-net` if you hold older wallets.

### 6.2 Create the Watch-Only Wallet on the Payment Node

```bash
ecli createwallet btcpay true true "" false true true
```

The arguments are: name, `disable_private_keys`, `blank`, passphrase, `avoid_reuse`, `descriptors`, `load_on_startup`. The last `true` makes the node load the wallet again after every restart. Without it, a node restart leaves BTCPay without a wallet.

Import the public descriptor. Replace the descriptor string with the one from 6.1:

```bash
ecli -rpcwallet=btcpay importdescriptors '[{"desc":"wpkh([a1b2c3d4/84h/1370h/0h]xpub6.../0/*)#checksum","active":true,"internal":false,"range":[0,999],"next_index":0,"timestamp":"now"}]'
```

`"timestamp":"now"` skips any rescan, which is correct for a fresh wallet and required on a pruned node.

Verify:

```bash
ecli -rpcwallet=btcpay getwalletinfo
```

`private_keys_enabled` MUST be `false`.

Compare the first addresses of both wallets. They MUST be identical:

```bash
ecli -rpcwallet=btcpay getnewaddress "" bech32
ecli deriveaddresses "wpkh([a1b2c3d4/84h/1370h/0h]xpub6.../0/*)#checksum" "[0,0]"
```

The first call consumes index 0. Compare it to the derived address for index 0, then continue with `next_index` 1 in your notes.

### 6.3 Address Range

Every invoice consumes one address. The range `[0,999]` is enough for 1,000 invoices. Extend it before it runs out by running `importdescriptors` again with a larger `range` for the same descriptor. The signing wallet MUST also look ahead far enough to find all used addresses: if more than its look-ahead (default 1,000) consecutive invoices are never paid, later payments are not found. Increase the keypool of the signing wallet (`-keypool=` or a larger descriptor range) when you expect many expired invoices.

## 7. Part C: BTCPay Server and the Plugin

### 7.1 Install BTCPay Server (Linux, Docker)

Use the official `btcpayserver-docker` deployment and its documentation as the source of truth. Typical steps:

```bash
sudo su -
mkdir BTCPayServer && cd BTCPayServer
git clone https://github.com/btcpayserver/btcpayserver-docker
cd btcpayserver-docker
export BTCPAY_HOST="pay.example.com"
export NBITCOIN_NETWORK="mainnet"
export BTCPAYGEN_CRYPTO1="btc"
export BTCPAYGEN_REVERSEPROXY="nginx"
export BTCPAYGEN_LIGHTNING="none"
. ./btcpay-setup.sh -i
```

The default setup includes a Bitcoin chain. Running BTCPay Server without any Bitcoin chain has not been verified for this plugin, see section 12. The plugin does not use the Bitcoin chain, it only needs BTCPay Server itself.

Open `https://pay.example.com`, register the first account (it becomes the server administrator) and create a store.

### 7.2 Connect the Node Container

If the node runs in a separate Docker network (section 5.1), attach it to the BTCPay network so the name `elektrond` resolves from BTCPay:

```bash
docker network ls
docker network connect <btcpay network name> elektrond
```

If BTCPay Server and the node run as one compose project, define the node there instead as a custom fragment, for example `docker-compose-generator/docker-fragments/opt-add-elek.custom.yml`, and add `opt-add-elek` to `BTCPAYGEN_ADDITIONAL_FRAGMENTS`. The exact fragment mechanism is documented by `btcpayserver-docker`, verify it against the current version.

### 7.3 Environment Variables

Set these for the BTCPay Server container (or process):

| Variable | Value |
|---|---|
| `BTCPAY_ELEK_DAEMON_URI` | `http://elektrond:8332` (Docker), `http://127.0.0.1:8332` (same host) |
| `BTCPAY_ELEK_DAEMON_USERNAME` | `btcpay` |
| `BTCPAY_ELEK_DAEMON_PASSWORD` | the plaintext password from `rpcauth.py` |
| `BTCPAY_ELEK_WALLET_NAME` | `btcpay` (default) |

For the `btcpayserver-docker` layout, use a custom fragment that adds the variables to the `btcpayserver` service:

```yaml
version: "3"
services:
  btcpayserver:
    environment:
      BTCPAY_ELEK_DAEMON_URI: http://elektrond:8332
      BTCPAY_ELEK_DAEMON_USERNAME: btcpay
      BTCPAY_ELEK_DAEMON_PASSWORD: <password>
      BTCPAY_ELEK_WALLET_NAME: btcpay
```

Keep the file readable by root only, it contains a password.

### 7.4 Build the Plugin Package

On a machine with the .NET 10 SDK:

```bash
git clone https://github.com/kutlusoy/elektron-net-btcpayplugin.git
cd elektron-net-btcpayplugin
git submodule add https://github.com/btcpayserver/btcpayserver submodules/btcpayserver
dotnet publish Plugins/Elektron/BTCPayServer.Plugins.Elektron.csproj -c Release -o out/plugin
dotnet run --project submodules/btcpayserver/BTCPayServer.PluginPacker -- out/plugin BTCPayServer.Plugins.Elektron out/packed
```

The packer arguments are: directory of the compiled plugin, plugin assembly name, output directory. The result is:

```
out/packed/BTCPayServer.Plugins.Elektron/0.1.0/BTCPayServer.Plugins.Elektron.btcpay
```

Check out the BTCPay Server submodule at the release tag that matches your running BTCPay version, since the plugin compiles against its API.

### 7.5 Install the Plugin

1. In BTCPay Server open **Server Settings > Plugins** (administrator only).
2. Upload the `.btcpay` file with the upload form.
3. Restart BTCPay Server when asked. In the Docker deployment: `btcpay-restart.sh` in the `btcpayserver-docker` directory, or restart from the server maintenance page.
4. The plugin appears in the installed plugin list. If it does not, check the BTCPay logs and the version dependency (BTCPay Server 2.3.7 or newer).

### 7.6 Enable ELEK for a Store

There is no store settings page in version 0.1. Enable the payment method through the Greenfield API. Create an API key with the permission `btcpay.store.canmodifystoresettings` (Account > Manage Account > API Keys), then:

```bash
curl -X PUT https://pay.example.com/api/v1/stores/<storeId>/payment-methods/ELEK-CHAIN \
  -H "Authorization: token <api key>" \
  -H "Content-Type: application/json" \
  -d '{"enabled": true, "config": {}}'
```

Optional config field: `InvoiceSettledConfirmationThreshold` (number of confirmations after which a payment counts as settled, overriding the invoice speed policy).

### 7.7 First Test

1. Create a small invoice. Invoices can only be priced in ELEK (there is no exchange rate source):

```bash
curl -X POST https://pay.example.com/api/v1/stores/<storeId>/invoices \
  -H "Authorization: token <api key>" \
  -H "Content-Type: application/json" \
  -d '{"amount": "0.01", "currency": "ELEK"}'
```

2. Open the returned `checkoutLink`. A `be1q...` address and a QR code (`elek:` link) are shown.
3. Pay from any Elektron wallet.
4. The invoice moves to **Processing** when the transaction is seen and to **Settled** after the required confirmations.
5. Confirm that the funds arrive in the signing wallet (it must be synced, section 9.3).

Use small amounts until you have completed this test on your own system.

### 7.8 Windows and macOS Test Setup

Install Docker Desktop, then follow the same `btcpayserver-docker` or the official BTCPay development guide. Run `elektrond` natively (section 5.3) and use `http://host.docker.internal:8332` as `BTCPAY_ELEK_DAEMON_URI`. Set `rpcbind=0.0.0.0` and a narrow `rpcallowip` for the Docker network in `bitcoin.conf`, and keep the firewall closed to the outside. This setup is for evaluation only.

## 8. Optional: Instant Notifications

The plugin polls the node every 15 seconds and reacts to new blocks, so no notification setup is required. On a native (non-Docker) node you MAY add callbacks to reduce latency:

```ini
walletnotify=curl -s "http://127.0.0.1:49392/elektrondaemoncallback/tx?hash=%s"
blocknotify=curl -s "http://127.0.0.1:49392/elektrondaemoncallback/block?hash=%s"
```

Adjust host and port to where BTCPay Server listens. The Docker node image has no `curl`, so rely on polling there.

## 9. Part D: Operations

### 9.1 Health Checks

| Check | Command | Healthy |
|---|---|---|
| Node synced | `ecli getblockchaininfo` | `initialblockdownload: false`, `blocks` close to the explorer height |
| Wallet loaded | `ecli listwallets` | contains `btcpay` |
| Wallet watch-only | `ecli -rpcwallet=btcpay getwalletinfo` | `private_keys_enabled: false` |
| Plugin active | BTCPay log | no repeated "Node or wallet not available" |
| Disk | `df -h` | free space for the node data directory |

### 9.2 Updates

- **Node:** watch the [releases](https://github.com/kutlusoy/elektron-net/releases). Some releases are mandatory before a given block height (for example v4.0.3 before height 150000). Stop the node cleanly, replace the binary or rebuild the image, start, and re-check section 9.1. The node MUST NOT stay offline for months, it can only catch up within the pruning window.
- **BTCPay Server:** update with the official procedure. After a BTCPay update, check the plugin list. A new BTCPay version can require a rebuilt plugin (section 7.4).
- **Plugin:** build the new package, upload it again, restart.

### 9.3 Spending the Funds

Funds received through BTCPay are controlled by the signing wallet, not by BTCPay Server. To spend them:

1. Start the signing wallet's node and let it sync.
2. Load the wallet (`ecli loadwallet merchant`). A wallet recovered on a pruned node finds funds through the UTXO set scan, historic transactions are not available.
3. Check the balance (`ecli -rpcwallet=merchant getbalance`) and send with `sendtoaddress` or a PSBT workflow.

Payments are only visible in the signing wallet after the wallet has looked ahead to the used address index (section 6.3).

### 9.4 Backups

| What | How | Why |
|---|---|---|
| Signing wallet seed / wallet file | Offline copies in two places | Only way to access funds |
| Descriptor text, range, next index | Notes next to the seed | Rebuild the watch-only wallet |
| BTCPay database and settings | `btcpay-backup.sh` of `btcpayserver-docker` | Stores, invoices, plugin config |
| Node data directory | Not required | Can be re-synchronised |

The watch-only wallet does not need a private-key backup, it can be rebuilt from the descriptor.

### 9.5 Security Rules

- The RPC port `8332` MUST NOT be reachable from the internet. Docker: do not publish it. Native: bind to `127.0.0.1` or a private network.
- Use `rpcauth` or a long random `rpcpassword`, never a default.
- Private keys MUST NOT be present on the BTCPay host or in the node wallet used by BTCPay.
- Show and accept only `be1q...` addresses. Do not import legacy descriptors.
- Keep BTCPay Server, the plugin and the node updated.

## 10. Part E: Switching Off and Moving

### 10.1 Stop Accepting ELEK (keep everything installed)

```bash
curl -X PUT https://pay.example.com/api/v1/stores/<storeId>/payment-methods/ELEK-CHAIN \
  -H "Authorization: token <api key>" -H "Content-Type: application/json" \
  -d '{"enabled": false}'
```

Existing invoices keep being tracked. Wait until open invoices are settled or expired.

### 10.2 Remove the Plugin

1. Export your invoices first (BTCPay: Invoices > Export).
2. **Server Settings > Plugins**, uninstall the ELEK plugin, restart BTCPay Server.
3. Remove the `BTCPAY_ELEK_*` variables or the custom fragment.

How BTCPay Server displays old ELEK invoices after the plugin is gone has not been verified. Keep the export.

### 10.3 Stop or Remove the Node

```bash
docker stop elektrond      # keep data
docker rm elektrond        # remove container, data stays in /opt/elektron/data
```

Funds are unaffected, they belong to the signing wallet.

### 10.4 Move to Another BTCPay Instance or Node

- **New node, same BTCPay:** change `BTCPAY_ELEK_DAEMON_URI`, create the wallet and import the descriptor there (section 6.2), restart BTCPay Server.
- **New BTCPay Server:** install the plugin, set the variables, import the descriptor.

In both cases set `next_index` in `importdescriptors` to the highest index you have already used plus a safety margin. A fresh wallet would otherwise start at index 0 and hand out addresses that were used before. Alternatively use a new account (`84h/1370h/1h`) with a new descriptor.

## 11. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Checkout has no ELEK option | Method not enabled for the store, or node not available | Section 7.6, section 9.1 |
| "Node or wallet not available" in logs | Node syncing, wallet not loaded, wallet has private keys, wrong credentials | `getblockchaininfo`, `listwallets`, `getwalletinfo`, check env variables |
| "BTCPAY_ELEK_DAEMON_URI is not configured" | Variable missing in the BTCPay container | Section 7.3, restart the container |
| After a node restart payments stop | Wallet was not set to load on startup | `ecli loadwallet btcpay true` |
| HTTP 401 from the node | `rpcauth` and password do not match | Regenerate with `rpcauth.py`, update both sides |
| Connection refused or timeout | Wrong host name, network not shared, `rpcallowip` too narrow | Section 7.2, widen `rpcallowip` to the Docker subnet |
| Invoice stays "Processing" | Not enough confirmations for the speed policy | Wait, or lower `InvoiceSettledConfirmationThreshold` |
| Payment not visible in the signing wallet | Look-ahead too small, wallet not synced | Section 6.3, section 9.3 |
| Plugin not listed after upload | BTCPay older than 2.3.7, wrong package, no restart | Check version, rebuild, restart |
| Addresses repeat after a migration | New wallet started at index 0 | Section 10.4 |

## 12. Verification Status

| Statement | Status |
|---|---|
| Node binaries, ports, config name, data directory | Taken from `TECHNICAL_SETUP.md`, `Quick-Install.md` and the stack Dockerfile |
| Docker image build for the node | Taken from `elektron-net-stack`, not run for this guide |
| Wallet RPCs (`createwallet`, `importdescriptors`, `listdescriptors`, `deriveaddresses`) | Present in `elektron-net` source, not run against `elektrond` |
| Plugin upload, restart, uninstall flow and packer arguments | Read from BTCPay Server source |
| Plugin code compiles and runs | **Not verified**, never built |
| Greenfield path `ELEK-CHAIN` and currency code `ELEK` | **Not verified** |
| BTCPay Server without a Bitcoin chain | **Not verified** |
| Custom fragment mechanism of `btcpayserver-docker` | **Not verified**, from memory, check the current docs |
| Windows release archive contents | **Not verified**, only asset names were read |

## 13. Checklist

- [ ] Node built or installed and fully synced
- [ ] RPC credentials generated, RPC port not public
- [ ] Signing wallet created offline, descriptor shows `84h/1370h/0h`
- [ ] Watch-only wallet `btcpay` created with load on startup, descriptor imported
- [ ] First addresses compared between both wallets
- [ ] BTCPay Server installed over HTTPS
- [ ] Plugin built, uploaded, BTCPay restarted
- [ ] Environment variables set, node reachable from BTCPay
- [ ] ELEK enabled for the store
- [ ] Test invoice paid and funds seen in the signing wallet
- [ ] Backups made and restore of the signing wallet tested

## 14. Open Questions

1. Should the project publish a prebuilt `elektrond` Docker image and Linux release binaries to remove the source build from this guide?
2. Should the plugin get a store settings page so that the Greenfield call in section 7.6 is not needed?
3. Which wallet software should the project recommend as the signing wallet for merchants?

## Links

- Website: https://elektron-net.org
- X (Twitter): @elektronnet and @kutlusoy
- Telegram: @elektronnet
