# Elektron Net: `elektron-net-btcpayplugin` Payment Stack Guideline

- **Version:** 0.1 (draft)
- **Date:** October 11, 2026
- **Audience:** Operators who want a complete ELEK payment server (node, BTCPay Server, plugin) installed with one script
- **Reference implementation:** [`elektron-net`](https://github.com/kutlusoy/elektron-net) (`src/wallet/rpc/`, `share/rpcauth/rpcauth.py`), node image recipe taken from [`elektron-net-stack`](https://github.com/kutlusoy/elektron-net-stack), treat as ground truth for anything this doc references
- **Consumer:** the `stack/` directory of this repository
- **See also:** [`guideline-operations.md`](guideline-operations.md) (manual setup, wallet details, switching off), [`README.MD`](../README.MD)

Requirement levels follow common practice: **MUST** = mandatory for correct or safe operation, **SHOULD** = strongly recommended, **MAY** = optional.

This guideline describes the all-in-one stack in `stack/`. Section 14 lists what was simulated and what has not yet been run on a real server. Nothing here has been tested end to end.

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
- A DNS name for `BTCPAY_HOST` pointing to the server, and ports `80`, `443` and `8333` open (the node uses `8333`, the RPC port stays internal). Without a domain use the local mode (section 6), on Windows see section 7
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

### 4.1 Get the Watch-Only Descriptor

The installer needs the public descriptor of a wallet whose private keys stay off the payment server. It looks like `wpkh([a1b2c3d4/84h/1370h/0h]xpub6.../0/*)#checksum`.

1. On a machine with `elektrond` and `elektron-cli` (an offline computer is best, the node does not need to be synced), run in `stack/`:

```bash
bash tools/export-descriptor.sh
```

   The script creates the wallet `merchant` if it does not exist (`WALLET=name` picks another name), prints the line `WATCHONLY_DESCRIPTOR=...` and the first address of the wallet, and refuses anything that contains a private key or a wrong derivation path. If `elektron-cli` needs arguments or runs in Docker, set `ELEKTRON_CLI`, for example `ELEKTRON_CLI="elektron-cli -datadir=D:\elektron"`.
2. By hand, the same result: `elektron-cli createwallet merchant`, then `elektron-cli -rpcwallet=merchant listdescriptors`, and take the entry with `"active": true`, `"internal": false` that starts with `wpkh(`. The path MUST contain `84h/1370h/0h`, the entry MUST NOT contain `xprv`.
3. Back up the wallet right away (`elektron-cli -rpcwallet=merchant backupwallet <file>`) and keep the file off the payment server. It is the only way to spend the funds. The descriptor itself is public and safe to copy to the server.
4. Do not create the signing wallet on the payment node of this stack, that would put the private keys on the payment server.

### 4.2 Install

1. Put the descriptor line into `stack.conf` (next step).
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

Before you run the installer, check that `stack.conf` is saved with your own values. With the defaults (`BTCPAY_HOST=pay.example.com`) the installer stops at once. Set `BTCPAY_HOST` to your domain, or `LOCAL_MODE=true` for a test without a domain (section 6), then the matching `WALLET_MODE` with its descriptor or sweep address. A file saved on Windows with Notepad (CRLF line endings) is accepted.

4. Run the installer as root or as a user in the `docker` group:

```bash
bash install.sh
```

5. Compare the first derived address printed by the installer with the first address printed by `tools/export-descriptor.sh`. They MUST be identical.

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

## 6. Local Mode Without a Domain

For a test on your own computer, or a private setup with no domain and no certificate, set in `stack.conf`:

```
LOCAL_MODE=true
WALLET_MODE=hot
```

What changes in local mode:

- `BTCPAY_HOST` is not needed and Caddy is not started. BTCPay Server answers over plain HTTP at `http://localhost:49392`, published on `127.0.0.1` only.
- The node publishes its P2P port on `127.0.0.1` only. It still syncs, outgoing connections are enough, nothing on your computer is reachable from the network.
- `WALLET_MODE=hot` works without `SWEEP_ADDRESS` and without a descriptor. The installer creates an encrypted wallet, no sweeper is started and the funds stay in the wallet. Use this for tests with small amounts only. A domain setup (section 4 and 5) still requires either a descriptor or a sweep address.
- `WALLET_MODE=watchonly` works as well, with a descriptor from section 4.1.

Limits: no HTTPS, so a customer on another device cannot reach the checkout page, and a webhook from the internet cannot reach BTCPay Server. To open it to other devices later, set `BTCPAY_HOST`, `LOCAL_MODE=false` and a sweep address or a descriptor, then run `bash install.sh` again. The data and the wallet stay.

## 7. Windows with Docker Desktop

Run the stack inside WSL2, not in PowerShell or Git Bash. The installer needs `bash`, `git`, `openssl` and `python3`, and the node build runs in Linux containers anyway.

1. Install Docker Desktop with the WSL2 backend and a WSL2 distribution such as Ubuntu. In Docker Desktop open Settings, Resources, WSL integration and switch on your distribution.
2. Give the build enough memory. The node build needs about 4 GB, so make sure WSL2 gets at least 6 GB (`memory=6GB` in `%UserProfile%\.wslconfig`, then `wsl --shutdown`).
3. Open the Ubuntu shell and work in the Linux file system (`cd ~`), not under `/mnt/c`. File modes and speed are wrong there.
4. Install the tools if they are missing: `sudo apt update && sudo apt install -y git openssl python3`.
5. Get the stack and start the local mode:

```bash
git clone https://github.com/kutlusoy/elektron-net-btcpayplugin.git
cd elektron-net-btcpayplugin/stack
cp stack.conf.example stack.conf
nano stack.conf        # set LOCAL_MODE=true and WALLET_MODE=hot
bash install.sh
```

   If you start the installer from PowerShell with `bash install.sh`, Windows starts the WSL shell. Lines such as `wsl: Failed to translate 'Z:\...'` are harmless warnings about Windows PATH entries that WSL cannot map. To switch them off, add `appendWindowsPath=false` under `[interop]` in `/etc/wsl.conf` and run `wsl --shutdown`. Edit `stack.conf` first (`notepad stack.conf` when the repository lies on the Windows side), the defaults stop the installer.

6. Open `http://localhost:49392` in the Windows browser. Docker Desktop passes the port that the container publishes on `127.0.0.1` through to Windows, so the page is reachable from your Windows PC and from nothing else. Register the administrator account, create a store and enable **Wallets > Elektron Net**. The first build takes a while, and the node has to finish syncing before ELEK can be accepted.
7. To measure the sync time: `ELEKTRON_CLI="docker compose exec -T elektrond elektron-cli -conf=/etc/elektron/cli.conf" LOG_FILE=<path to debug.log> bash tools/measure-sync.sh`.
8. Remove everything again with `docker compose down -v` (this deletes the node data, the database and the wallet of the test).

If the repository was cloned on the Windows side, the file `.gitattributes` keeps the shell scripts and configs on LF line endings. A failure like `bash\r: No such file or directory` means a script has CRLF line endings, clone again inside WSL2.

## 8. What the Installer Does

1. Parses `stack.conf` (it is read, not executed, and unknown keys are rejected)
2. Checks the settings: in local mode no domain is needed, otherwise `BTCPAY_HOST` is required. In watch-only mode only public `wpkh` descriptors with the path `84h/1370h`, no private keys. In hot mode a Bech32 sweep address (optional in local mode)
3. Resolves the newest stable releases of `elektron-net` and BTCPay Server (`latest` is the default for both, the elektron-net release current at the time of writing is v4.0.5), then clones or updates `elektron-src/` to that tag. Before this it fast-forwards this repository itself (`AUTO_UPDATE_STACK`) and restarts once if the stack files changed
4. Generates the RPC password and the database password once and keeps them in `.env` on re-runs
5. Writes `elektron.conf` (node, with seed nodes, `addnode` entries, `fallbackfee` and a hashed RPC credential; inside the container the data directory is `/data`, not `~/.elektron`), `cli.conf` (client credentials) and `.env`, all readable by root only
6. Builds the images, starts the node and the database and waits for the RPC
7. Creates the wallet (watch-only with the imported descriptor, or encrypted hot wallet), or loads it if it already exists
8. Starts the whole stack and writes `CREDENTIALS.txt`: one file with everything worth securing (versions, URL, node RPC and database passwords, wallet name and mode, the public descriptor and first address in watch-only mode, the wallet passphrase, the latest wallet backup path and the sweep rule in hot mode, the files to keep and the backup commands). Copy it off the server and delete it there if you prefer

The script can be run again at any time and doubles as the update. Every run checks for new releases, rebuilds the images only when something changed and recreates only the changed containers. It reuses the existing secrets from `.env` and `secrets/`, the existing wallet and the existing `elektron.conf` (only a missing `fallbackfee` line is added and an old `rpcbind=0.0.0.0` is changed to the internal address, followed by a node restart), and rewrites `CREDENTIALS.txt` from them. In hot mode every run also writes a dated wallet backup inside the node volume. If the build of a new release fails, the installer stops before it touches the running stack and restores the previous `.env`. A new release can still fail at runtime, for example when BTCPay Server changes an API the plugin uses, so check the store page afterwards. Pin `BTCPAY_VERSION` or `ELEKTRON_NET_REF` in `stack.conf` to go back to the previous release.

## 9. First Steps After the Install

1. Open `https://BTCPAY_HOST` (in local mode `http://localhost:49392`) and register the administrator account immediately. The first account that registers becomes the server administrator.
2. Create a store. Open **Wallets > Elektron Net** in the store and enable ELEK.
3. Wait until the node has synchronised (`docker compose exec elektrond elektron-cli -conf=/etc/elektron/cli.conf getblockchaininfo` shows `initialblockdownload: false`). The store page shows the node status as well.
4. Create a small test invoice, see section 7.7 of the operations guideline.

## 10. Operations

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

## 11. Switching Off

- Stop accepting ELEK: disable it in the store (**Wallets > Elektron Net**).
- Stop the stack and keep all data: `docker compose down`
- Remove everything including all data: `docker compose down -v`. This deletes the node data, the database and in hot mode the wallet inside the node volume. Make sure the wallet backup and the passphrase are stored elsewhere first.

## 12. Security Notes

- The node RPC port is not published, and the node binds it to its internal address `172.29.88.251` only (`rpcbind`), not to `0.0.0.0`. Only the internal network (`172.29.88.0/24`) can reach it.
- PostgreSQL is not published either and listens on its internal address `172.29.88.250` only (`listen_addresses`), not on `0.0.0.0`. Ports `5432` and `8332` are never open on the host, only `80`, `443` and `8333` are (in local mode only `127.0.0.1:49392` and `127.0.0.1:8333`).
- `.env`, `cli.conf`, `secrets/` and `CREDENTIALS.txt` contain credentials. They are created with mode `600` and are excluded from Git. `elektron.conf` has mode `644` because the node runs as an unprivileged user inside the container and has to read it. It holds only the hashed RPC credential, not the password.
- The sweep address is fixed in the configuration of the sweeper. The wallet passphrase is mounted into the sweeper only, not into the BTCPay container.
- While the sweeper has the wallet unlocked (up to 60 seconds, only when a sweep happens), anyone who holds the node RPC credentials could spend. BTCPay Server holds those credentials, so keep the balance low and the sweep threshold small.
- Anyone with access to the Docker socket on the host can read all secrets. Treat the server accordingly.
- Register the BTCPay administrator right after the install, before anyone else can reach the page.

## 13. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Installer stops with a message | Invalid `stack.conf` | Fix the setting named in the message |
| `node RPC did not come up` | Build error or crash | `docker compose logs elektrond` |
| Image build fails for BTCPay | The `BTCPAY_VERSION` tag does not exist, or the plugin does not compile against it | Check the tag in the BTCPay repository, use a version the plugin supports |
| HTTPS does not work | DNS not pointing to the server, ports closed | Check DNS, ports `80` and `443`, `docker compose logs caddy` |
| Plugin missing in BTCPay | The plugin did not install on startup | `docker compose logs btcpayserver`, then Server Settings > Plugins |
| ELEK not available in the store | Node still syncing, wallet missing | Store page **Wallets > Elektron Net** shows the reason |
| Sweeper logs a failed sweep | Wallet locked state, balance too small, RPC unreachable | `docker compose logs sweeper`, test `sendtoaddress` by hand |
| Node container restarts with `Data directory "/data" contains a "bitcoin.conf" file which is ignored` | The node writes its own `bitcoin.conf` into the data directory and refuses a second configuration file | The image starts the node with `-allowignoredconf=1`. Rebuild with `bash install.sh` if your image is older |
| Node container restarts with `could not be opened` for the configuration | `elektron.conf` is not readable for the node user | `chmod 644 elektron.conf` (the installer does this on every run) |
| Local mode: `http://localhost:49392` does not open | The stack is still starting, or the port is taken by another program | `docker compose ps`, `docker compose logs btcpayserver`, free the port |

## 14. Verification Status

| Statement | Status |
|---|---|
| Installer flow, generated files and file modes, config validation and rejections | **Simulated** with a stand-in for `docker`, no real containers |
| Local mode (no domain, loopback ports, no Caddy, hot wallet without sweeper, rejections for a missing domain or sweep address) | **Simulated** with a stand-in for `docker`, the compose file parsed as YAML, not run with Docker |
| `tools/export-descriptor.sh` | **Verified** against a real `elektrond` v4.0.5 on the main chain, offline: new and existing wallet, path `84h/1370h/0h`, `be1` first address, refusal on another chain |
| Windows with Docker Desktop and WSL2 | **Not verified**, the steps are written from the Docker Desktop and WSL2 documentation and have not been run. That Docker Desktop forwards a port published on `127.0.0.1` to the Windows browser is also not verified here |
| Update flow: latest tag resolution (pre-releases ignored), node update to a new tag, BTCPay version change, restore after a failed build, missing image check, self-update with restart | **Simulated** with local Git repositories and a stand-in for `docker`, not against GitHub or Docker Hub |
| Sweeper amount arithmetic, threshold logic, passphrase on stdin, wallet locking on failure | **Simulated** with a stand-in for `elektron-cli`, and **verified** once against a real `elektrond` v4.0.5 in regtest: encrypted wallet created with `-stdin -named createwallet`, 8 ELEK received, 7 ELEK swept with the fee deducted, wallet locked again, 1 ELEK left |
| Compose file validity and profiles | Checked with `docker compose config` |
| Fixed container addresses, `listen_addresses` of PostgreSQL and the node `rpcbind` to an internal address | Compose file parsed as YAML, `rpcbind` to a specific address checked on a real node (only that address answers, `elektron-cli` with `rpcconnect` works). The PostgreSQL setting and the fixed addresses were **not run** with Docker |
| Runtime libraries of the node image | **Verified** with `readelf` on a build of `elektrond` v4.0.5 with the Dockerfile switches and shared libraries: it needs `libevent_core`, `libevent_extra`, `libevent_pthreads`, `libzmq.so.5`, `libsqlite3.so.0`, `libstdc++`, `libgcc_s`, `libm` and `libc`, `elektron-cli` needs `libevent_core` and `libevent_extra`. The packages in the image cover them. The check used libevent 2.2 from Git, Debian ships 2.1 with the same library names |
| Docker image builds (node, BTCPay with plugin), `docker compose up` | First real build on Windows with Docker Desktop: the node configure step failed on Cap'n Proto, the plugin build failed on missing `using` lines, the runtime image missed `libevent` core and extra, the node refused a second configuration file. All four are fixed. A complete successful run is not confirmed yet |
| The BTCPay tag `v<BTCPAY_VERSION>` and the Docker Hub image tag exist | **Not verified** |
| Plugin installation through the queued `install` command | Read from BTCPay Server source, not run |
| `rpcauth.py` output format, `elektron-cli -stdin -named` handling, `createwallet` with a passphrase | **Verified** against a real `elektrond` v4.0.5 |
| Fee estimation for `sendtoaddress` | **Verified** on the real node: without a fallback fee a send fails on a fresh node. The installer sets `fallbackfee=0.0001` and adds it to an existing `elektron.conf` on update |
| Live node start, real sync, snapshot activation, time from first header to synced | **Not verified**, the environment had no peer access. `tools/measure-sync.sh` is provided for this, it ran against a real node in regtest but not against a real sync |
| Plugin code compiles and runs | **Not verified** as a running plugin. The C# code compiled in the first real build after three missing `using` lines were added, the views needed one more |

## 15. Checklist

- [ ] Run the installer on a clean server in watch-only mode
- [ ] Verify the BTCPay image tag and that the plugin builds against it
- [ ] Verify wallet creation, descriptor import and the printed first address on the real node
- [ ] Run hot mode with a small balance and a test sweep to a cold address
- [ ] Test an update of the node and of BTCPay Server with existing data
- [ ] Run the local mode on a Windows PC with Docker Desktop
- [ ] Publish prebuilt images to remove the long source build

## 16. Open Questions

1. Should the project publish prebuilt `elektrond` and BTCPay plugin images?
2. Which default sweep threshold and confirmation depth fit the chain's value and its 60 second block time?
3. Should the sweeper support several cold addresses or an xpub-derived address list?

## Links

- Website: https://elektron-net.org
- X (Twitter): @elektronnet and @kutlusoy
- Telegram: @elektronnet
