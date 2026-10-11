# Elektron Net: `elektron-net-btcpayplugin` Todo

- **Version:** 0.3 (draft)
- **Date:** October 11, 2026
- **Audience:** Maintainers of the BTCPay Server plugin
- **Reference implementation:** [`elektron-net`](https://github.com/kutlusoy/elektron-net) (chain parameters, wallet RPC), treat as ground truth for anything this doc references
- **See also:** [`guideline-operations.md`](guideline-operations.md), [`guideline-stack.md`](guideline-stack.md), [`README.MD`](../README.MD)

Open work items, ordered by priority. Remove an item when it is done and verified.

---

## 1. Live Verification (blocks a first release)

Nothing below could be run where the code was written (no peer access, no .NET SDK, no container runtime).

- [ ] Build the plugin and run the unit tests (`dotnet build`, `dotnet test`), fix whatever the compiler finds
- [ ] Build the `.btcpay` package with the PluginPacker and install it on a BTCPay Server instance
- [ ] Check that the BTCPay Docker image tag and the Git tag `v<BTCPAY_VERSION>` exist for the version the stack builds against
- [ ] Build the stack images (node and BTCPay with plugin) and run `docker compose up`, including the fixed container addresses and the PostgreSQL `listen_addresses` setting
- [ ] Run the installer on a clean server in watch-only mode and in hot mode, then run it again as an update
- [ ] Sync a node from the official seeds and record the time from the first header to the finished sync with `stack/tools/measure-sync.sh`, including the `[snapshot]` phases
- [ ] Pay a real invoice end to end (ELEK priced invoice, payment, confirmations, settled state, funds in the signing wallet)
- [ ] Verify the Greenfield API call for `ELEK-CHAIN` and the currency code `ELEK` on invoices
- [ ] Test the update of the node and of BTCPay Server with existing data

## 2. Getting the Watch-Only Descriptor

**Problem.** The installer needs `WATCHONLY_DESCRIPTOR` in `stack.conf`, but the stack guideline only points to section 6.1 of the operations guideline. A new operator has no single place that says where the descriptor comes from, on which machine to create it, and how to check it.

**Goal.** An operator who has never seen a descriptor gets from nothing to a verified `WATCHONLY_DESCRIPTOR` line in a few minutes, without any private key touching the payment server.

**Tasks.**

- [ ] Add a short "Step 0: create the signing wallet and get the descriptor" section to the stack guideline, before the install steps, with the exact commands (`createwallet`, `listdescriptors`, pick the `wpkh` entry with `"internal": false`) and what a correct result looks like (`84h/1370h/0h`, `xpub`, no `xprv`)
- [ ] Write a helper `stack/tools/export-descriptor.sh` for the offline machine: it creates or opens a wallet, prints only the public external `wpkh` descriptor with checksum and the first derived address, refuses anything that contains a private key or a wrong path, and prints the `stack.conf` line ready to paste
- [ ] Check whether the Elektron desktop wallet can export a public descriptor, and if so document the menu path, otherwise say that it cannot
- [ ] Check whether a hardware wallet can be used as signing wallet for the chain (derivation path `84h/1370h/0h`), and document the result or the limit
- [ ] Explain how to back up the signing wallet and why the seed MUST stay off the payment server
- [ ] Test the full path on a real node: signing wallet, descriptor, installer, first address comparison

## 3. Local Mode Without a Domain

**Problem.** The installer requires a public `BTCPAY_HOST`, and Caddy requests a certificate for it. Someone who wants to run the payment server on their own computer for testing or for a private setup has no domain, and the stack expects HTTPS and a forwarded protocol of `https`.

**Goal.** `bash install.sh` works on a laptop or home server with no domain, no certificate and no open ports.

**Tasks.**

- [ ] Add a local mode, for example `BTCPAY_HOST=localhost` or a setting `LOCAL_MODE=true`, that skips the domain check
- [ ] In local mode serve BTCPay Server over plain HTTP, set `BTCPAY_XFORWARDEDPROTO` to `http` and do not start Caddy (or give Caddy an `http://` site)
- [ ] Publish the BTCPay Server port on `127.0.0.1` only (for example `127.0.0.1:49392`), never on all interfaces, and print the address to open in the browser
- [ ] Do not require the node's P2P port to be reachable from outside, outgoing connections are enough, and document that the node still syncs
- [ ] Document the limits: no HTTPS, a wallet app or customer on another device cannot reach the checkout page without a tunnel or a reverse proxy, a webhook from the internet cannot reach it
- [ ] Document how to reach the page from another machine on the same network, and how to put a tunnel in front of it later
- [ ] Test the local mode with the installer simulation and later on a real machine

## 4. Guard Against Reused Receiving Addresses

**Problem.** The payment method handler takes the address from `getnewaddress` and registers it as a tracked destination without any check. If the wallet hands out an address a second time, BTCPay Server rejects the invoice (the Monero plugin reports the same case as HTTP 500 with `PK_AddressInvoices`, [issue 57](https://github.com/btcpay-monero/btcpayserver-monero-plugin/issues/57)). A wallet hands out addresses again after it is restored from an older backup or recreated with the same descriptor (the installer imports with `next_index` 0). Worse, an old transaction to a reused address could be shown on a new invoice.

**Tasks.**

- [ ] In `ElektronPaymentMethodHandler`, ask for the next address again when the new one already has transactions (`listreceivedbyaddress`) or is already tracked, with a small retry limit and a clear error afterwards
- [ ] Make the installer take `next_index` from the node when it recreates a wallet, or warn loudly when the wallet is new but invoices already exist
- [ ] Add unit tests with a fake node that returns a used address first
- [ ] Keep the migration advice in section 10.4 of the operations guideline in line with the behaviour

## 5. ELEK Rate Source for Fiat Priced Invoices

**Problem.** The plugin has no exchange rate source. Invoices can only be priced directly in ELEK. A merchant who already accepts BTC and Monero on an existing BTCPay Server instance and prices invoices in a fiat currency (EUR, USD) does not get ELEK offered at checkout, because BTCPay cannot convert the fiat amount into ELEK.

**Goal.** A merchant with a mixed setup can enable ELEK next to BTC and XMR and keep pricing in fiat.

**Tasks.**

- [ ] Read how BTCPay Server resolves rates for a payment method (rate rules, rate providers, the currency pair `ELEK_<fiat>`) and what happens for a currency without any provider
- [ ] Decide the source: a fixed rate set by the merchant through the store rate rules, an external price feed for ELEK, or both
- [ ] If the store rate rules can express a fixed pair, document the rule and verify it on a running instance
- [ ] If a rate provider is needed, implement it in the plugin and register it, with a clear failure state when no price is available (ELEK MUST NOT be offered with a stale or missing rate)
- [ ] Show the used rate on the checkout and on the invoice page
- [ ] Add unit tests for the conversion and for the missing-rate case, and a mock of the rate provider for integration tests (the Monero plugin tracks the same need in [issue 46](https://github.com/btcpay-monero/btcpayserver-monero-plugin/issues/46))
- [ ] Describe the setup for existing multi-currency instances in the operations guideline
- [ ] Verify with an instance that already runs BTC and XMR: enable ELEK for a store, create an EUR invoice, pay it in ELEK

**Open questions.**

1. Is there a public price feed for ELEK, or does the project publish one?
2. Should the plugin refuse fiat invoices until a rate is configured, or fall back to a fixed merchant rate?

## 6. CI and Dependency Hardening

The Monero plugin added these in autumn 2026, the workflow in this repository has none of them.

- [ ] Enable CodeQL code scanning for C# (exclude the BTCPay Server submodule or checkout used for the build)
- [ ] Enable Dependabot for NuGet packages and GitHub Actions
- [ ] Add a scheduled workflow that builds the plugin against the newest stable BTCPay Server tag, so a breaking BTCPay release shows up before an operator updates with `BTCPAY_VERSION=latest`
- [ ] Add a check that fails when a file contains the character used as a long dash or a reference to the tools used to write it, as required by the repository rules

## 7. Snapshot Bootstrap Trust

The node accepts the snapshot hash announced by the first peer in the current code. Until the node tightens this, the guideline tells operators to bootstrap from trusted peers only.

- [ ] Follow the fix in `elektron-net` and update section 5.5 of the operations guideline when it ships
- [ ] Decide whether the stack should pin `connect=` or `addnode` entries for the first sync only

## Links

- Website: https://elektron-net.org
- X (Twitter): @elektronnet and @kutlusoy
- Telegram: @elektronnet
