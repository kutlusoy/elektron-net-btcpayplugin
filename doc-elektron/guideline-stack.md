# Elektron Net: `elektron-net-btcpayplugin` Payment Stack Guideline

- **Version:** 0.1 (draft)
- **Date:** October 11, 2026
- **Audience:** Operators who want a complete ELEK payment server (node, BTCPay Server, plugin) installed with one script
- **Reference implementation:** [`elektron-net`](https://github.com/kutlusoy/elektron-net) (`src/wallet/rpc/`, `share/rpcauth/rpcauth.py`), node image recipe taken from [`elektron-net-stack`](https://github.com/kutlusoy/elektron-net-stack), treat as ground truth for anything this doc references
- **Consumer:** the `stack/` directory of this repository
- **See also:** [`guideline-operations.md`](guideline-operations.md) (manual setup, wallet details, switching off), [`README.MD`](../README.MD)

Requirement levels follow common practice: **MUST** = mandatory for correct or safe operation, **SHOULD** = strongly recommended, **MAY** = optional.

This guideline describes the all-in-one stack in `stack/`. Section 12 lists what was simulated and what has not yet been run on a real server. Nothing here has been tested end to end.

---

## 1. What the Stack Contains

```
  Internet --> Caddy (HTTPS) --> BTCPay Server + ELEK plugin --> elektrond (pruned node)
                                         |                          ^
                                         v                          |
                                     PostgreSQL            sweeper (hot wallet mode only)
```

| Service | Image | Role |
|---|---|---|
| `elektrond` | Built from the `elektron-net` source | Full node with the payment wallet, RPC only on the internal network |
| `postgres` | `postgres:16-alpine` | BTCPay Server database |
| `btcpayserver` | `btcpayserver/btcpayserver` plus the ELEK plugin | Invoices, checkout, store UI. Runs without any Bitcoin chain (`BTCPAY_NODEFAULTCHAIN=true`), so no Bitcoin node and no NBXplorer |
| `caddy` | `caddy:2-alpine` | HTTPS with automatic certificates |
| `sweeper` | Same image as `elektrond` | Hot wallet mode only: moves funds above a threshold to a cold address |

The plugin is built inside the BTCPay image build, against the same BTCPay Server release that runs. On startup, the image entrypoint queues the plugin installation (BTCPay installs queued plugins at startup).

## 2. Requirements

- A Linux server with a public IPv4 address, 4 GB RAM, 2 vCPU and 50 GB disk as a floor
- Docker Engine with the compose plugin, `git`, `openssl`, `python3`
- A DNS name for `BTCPAY_HOST` pointing to the server, and ports `80`, `443` and `8333` open (the node uses `8333`, the RPC port stays internal)
- The node image is built from source. Plan for a long first build.

## 3. Choose a Wallet Mode

| | `watchonly` (default) | `hot` |
|---|---|---|
| Private keys | Offline, never on the server | Encrypted wallet on the server |
| Needed before install | Public descriptor from your offline signing wallet | A cold storage address (`be1...`) |
| Spending | From the signing wallet | Automatic sweep to the cold address, the rest from the node wallet with `elektron-cli` |
| If the server is compromised | Attacker sees addresses only | Attacker can take the balance currently on the server |
| Plugin setting | `BTCPAY_ELEK_ALLOW_HOT_WALLET=false` | `BTCPAY_ELEK_ALLOW_HOT_WALLET=true` |

Use `watchonly` for anything beyond small amounts. In `hot` mode the sweeper needs the wallet passphrase, which is stored in `stack/secrets/wallet_passphrase` on the same host. The protection is therefore a small balance and a fixed sweep address, not the encryption. The plugin itself never signs or spends in either mode, only the sweeper does.

## 4. Install in Watch-Only Mode

1. On an offline machine create the signing wallet and export the public descriptor (see section 6.1 of the operations guideline). It looks like `wpkh([a1b2c3d4/84h/1370h/0h]xpub6.../0/*)`.
2. On the server:

```bash
git clone https://github.com/kutlusoy/elektron-net-btcpayplugin.git
cd elektron-net-btcpayplugin/stack
cp stack.conf.example stack.conf
```

3. Edit `stack.conf`:

```
BTCPAY_HOST=pay.example.org
WALLET_MODE=watchonly
WATCHONLY_DESCRIPTOR=wpkh([a1b2c3d4/84h/1370h/0h]xpub6.../0/*)
```

4. Run the installer as root or as a user in the `docker` group:

```bash
bash install.sh
```

5. Compare the first derived address printed by the installer with the first address of your signing wallet. They MUST be identical.

## 5. Install in Hot Wallet Mode

Create a cold storage address on a wallet you control offline, then in `stack.conf`:

```
BTCPAY_HOST=pay.example.org
WALLET_MODE=hot
SWEEP_ADDRESS=be1q...
SWEEP_THRESHOLD=5
```

Run `bash install.sh`. The installer creates an encrypted wallet, stores the generated passphrase in `secrets/wallet_passphrase`, takes a first wallet backup inside the node volume and starts the sweeper. Afterwards copy the backup and the passphrase file off the server:

```bash
docker compose cp elektrond:/data/backup/btcpay-initial.dat .
```

The sweeper checks every `SWEEP_INTERVAL` seconds. When the confirmed balance (at least `SWEEP_MINCONF` confirmations) exceeds `SWEEP_KEEP + SWEEP_THRESHOLD`, it unlocks the wallet for 60 seconds, sends the excess to `SWEEP_ADDRESS` with the fee deducted from the amount, and locks the wallet again. It refuses to start if the sweep address is invalid.

## 6. What the Installer Does

1. Parses `stack.conf` (it is read, not executed, and unknown keys are rejected)
2. Checks the wallet settings: public `wpkh` descriptors with the path `84h/1370h` only, no private keys, Bech32 sweep address only
3. Resolves the newest stable releases of `elektron-net` and BTCPay Server (`latest` is the default for both, the elektron-net release current at the time of writing is v4.0.5), then clones or updates `elektron-src/` to that tag. Before this it fast-forwards this repository itself (`AUTO_UPDATE_STACK`) and restarts once if the stack files changed
4. Generates the RPC password and the database password once and keeps them in `.env` on re-runs
5. Writes `elektron.conf` (node, with seed nodes, `addnode` entries, `fallbackfee` and a hashed RPC credential; inside the container the data directory is `/data`, not `~/.elektron`), `cli.conf` (client credentials) and `.env`, all readable by root only
6. Builds the images, starts the node and the database and waits for the RPC
7. Creates the wallet (watch-only with the imported descriptor, or encrypted hot wallet), or loads it if it already exists
8. Starts the whole stack and writes `CREDENTIALS.txt`: one file with everything worth securing (versions, URL, node RPC and database passwords, wallet name and mode, the public descriptor and first address in watch-only mode, the wallet passphrase, the latest wallet backup path and the sweep rule in hot mode, the files to keep and the backup commands). Copy it off the server and delete it there if you prefer

The script can be run again at any time and doubles as the update. Every run checks for new releases, rebuilds the images only when something changed and recreates only the changed containers. It reuses the existing secrets from `.env` and `secrets/`, the existing wallet and the existing `elektron.conf` (only a missing `fallbackfee` line is added, followed by a node restart), and rewrites `CREDENTIALS.txt` from them. In hot mode every run also writes a dated wallet backup inside the node volume. If the build of a new release fails, the installer stops before it touches the running stack and restores the previous `.env`. A new release can still fail at runtime, for example when BTCPay Server changes an API the plugin uses, so check the store page afterwards. Pin `BTCPAY_VERSION` or `ELEKTRON_NET_REF` in `stack.conf` to go back to the previous release.

## 7. First Steps After the Install

1. Open `https://BTCPAY_HOST` and register the administrator account immediately. The first account that registers becomes the server administrator.
2. Create a store. Open **Wallets > Elektron Net** in the store and enable ELEK.
3. Wait until the node has synchronised (`docker compose exec elektrond elektron-cli -conf=/etc/elektron/cli.conf getblockchaininfo` shows `initialblockdownload: false`). The store page shows the node status as well.
4. Create a small test invoice, see section 7.7 of the operations guideline.

## 8. Operations

| Task | Command (in `stack/`) |
|---|---|
| Status | `docker compose ps` |
| Logs | `docker compose logs -f elektrond` (or `btcpayserver`, `sweeper`) |
| Restart | `docker compose restart btcpayserver` |
| Node CLI | `docker compose exec elektrond elektron-cli -conf=/etc/elektron/cli.conf <command>` |
| Wallet CLI | add `-rpcwallet=btcpay` |
| Update everything | `bash install.sh` (node, BTCPay Server, plugin and stack files, with `latest` settings) |
| Measure the sync | `ELEKTRON_CLI="docker compose exec -T elektrond elektron-cli -conf=/etc/elektron/cli.conf" LOG_FILE=<path to debug.log> bash tools/measure-sync.sh` (a new node bootstraps from a UTXO snapshot automatically) |
| Go back to a release | Set `ELEKTRON_NET_REF` or `BTCPAY_VERSION` to that release in `stack.conf`, run `bash install.sh` |
| Change sweep settings | Edit `stack.conf`, run `bash install.sh` |

Check the [node releases](https://github.com/kutlusoy/elektron-net/releases) regularly, some are mandatory before a given block height.

Backups: the signing wallet (watch-only mode) or the wallet backup and passphrase (hot mode) are the only things that protect funds. The BTCPay database lives in the `postgres-data` volume, back it up with `docker compose exec postgres pg_dump -U btcpay btcpayserver > backup.sql`. The node data can be re-synchronised.

## 9. Switching Off

- Stop accepting ELEK: disable it in the store (**Wallets > Elektron Net**).
- Stop the stack and keep all data: `docker compose down`
- Remove everything including all data: `docker compose down -v`. This deletes the node data, the database and in hot mode the wallet inside the node volume. Make sure the wallet backup and the passphrase are stored elsewhere first.

## 10. Security Notes

- The node RPC port is not published. Only the internal network (`172.29.88.0/24`) can reach it.
- `.env`, `cli.conf`, `elektron.conf`, `secrets/` and `CREDENTIALS.txt` contain credentials. They are created with mode `600` and are excluded from Git.
- The sweep address is fixed in the configuration of the sweeper. The wallet passphrase is mounted into the sweeper only, not into the BTCPay container.
- While the sweeper has the wallet unlocked (up to 60 seconds, only when a sweep happens), anyone who holds the node RPC credentials could spend. BTCPay Server holds those credentials, so keep the balance low and the sweep threshold small.
- Anyone with access to the Docker socket on the host can read all secrets. Treat the server accordingly.
- Register the BTCPay administrator right after the install, before anyone else can reach the page.

## 11. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Installer stops with a message | Invalid `stack.conf` | Fix the setting named in the message |
| `node RPC did not come up` | Build error or crash | `docker compose logs elektrond` |
| Image build fails for BTCPay | The `BTCPAY_VERSION` tag does not exist, or the plugin does not compile against it | Check the tag in the BTCPay repository, use a version the plugin supports |
| HTTPS does not work | DNS not pointing to the server, ports closed | Check DNS, ports `80` and `443`, `docker compose logs caddy` |
| Plugin missing in BTCPay | The plugin did not install on startup | `docker compose logs btcpayserver`, then Server Settings > Plugins |
| ELEK not available in the store | Node still syncing, wallet missing | Store page **Wallets > Elektron Net** shows the reason |
| Sweeper logs a failed sweep | Wallet locked state, balance too small, RPC unreachable | `docker compose logs sweeper`, test `sendtoaddress` by hand |

## 12. Verification Status

| Statement | Status |
|---|---|
| Installer flow, generated files and file modes, config validation and rejections | **Simulated** with a stand-in for `docker`, no real containers |
| Update flow: latest tag resolution (pre-releases ignored), node update to a new tag, BTCPay version change, restore after a failed build, missing image check, self-update with restart | **Simulated** with local Git repositories and a stand-in for `docker`, not against GitHub or Docker Hub |
| Sweeper amount arithmetic, threshold logic, passphrase on stdin, wallet locking on failure | **Simulated** with a stand-in for `elektron-cli`, and **verified** once against a real `elektrond` v4.0.5 in regtest: encrypted wallet created with `-stdin -named createwallet`, 8 ELEK received, 7 ELEK swept with the fee deducted, wallet locked again, 1 ELEK left |
| Compose file validity and profiles | Checked with `docker compose config` |
| Docker image builds (node, BTCPay with plugin), `docker compose up` | **Not verified**, no registry access where this was written |
| The BTCPay tag `v<BTCPAY_VERSION>` and the Docker Hub image tag exist | **Not verified** |
| Plugin installation through the queued `install` command | Read from BTCPay Server source, not run |
| `rpcauth.py` output format, `elektron-cli -stdin -named` handling, `createwallet` with a passphrase | **Verified** against a real `elektrond` v4.0.5 |
| Fee estimation for `sendtoaddress` | **Verified** on the real node: without a fallback fee a send fails on a fresh node. The installer sets `fallbackfee=0.0001` and adds it to an existing `elektron.conf` on update |
| Live node start, real sync, snapshot activation, time from first header to synced | **Not verified**, the environment had no peer access. `tools/measure-sync.sh` is provided for this, it ran against a real node in regtest but not against a real sync |
| Plugin code compiles and runs | **Not verified**, never built |

## 13. Checklist

- [ ] Run the installer on a clean server in watch-only mode
- [ ] Verify the BTCPay image tag and that the plugin builds against it
- [ ] Verify wallet creation, descriptor import and the printed first address on the real node
- [ ] Run hot mode with a small balance and a test sweep to a cold address
- [ ] Test an update of the node and of BTCPay Server with existing data
- [ ] Publish prebuilt images to remove the long source build

## 14. Open Questions

1. Should the project publish prebuilt `elektrond` and BTCPay plugin images?
2. Which default sweep threshold and confirmation depth fit the chain's value and its 60 second block time?
3. Should the sweeper support several cold addresses or an xpub-derived address list?

## Links

- Website: https://elektron-net.org
- X (Twitter): @elektronnet and @kutlusoy
- Telegram: @elektronnet
