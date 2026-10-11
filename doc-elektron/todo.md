# Elektron Net: `elektron-net-btcpayplugin` Todo

- **Version:** 0.4 (draft)
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
- [ ] Run the installer in local mode on a Windows PC with Docker Desktop
- [ ] Sync a node from the official seeds and record the time from the first header to the finished sync with `stack/tools/measure-sync.sh`, including the `[snapshot]` phases
- [ ] Pay a real invoice end to end (ELEK priced invoice, payment, confirmations, settled state, funds in the signing wallet)
- [ ] Verify the Greenfield API call for `ELEK-CHAIN` and the currency code `ELEK` on invoices
- [ ] Test the update of the node and of BTCPay Server with existing data

## 2. Getting the Watch-Only Descriptor (helper and guide done, checks left)

`stack/tools/export-descriptor.sh` and section 4.1 of the stack guideline exist. What is still open:

- [ ] Check whether the Elektron desktop wallet can export a public descriptor, and if so document the menu path, otherwise say that it cannot
- [ ] Check whether a hardware wallet can be used as signing wallet for the chain (derivation path `84h/1370h/0h`), and document the result or the limit
- [ ] Run the full path once on a real server: signing wallet on a separate machine, descriptor into `stack.conf`, installer, first address comparison
- [ ] Check that `tools/export-descriptor.sh` works with the Windows release of `elektron-cli.exe` from Git Bash or WSL

## 3. Local Mode Without a Domain (implemented, checks left)

`LOCAL_MODE=true` in `stack.conf`, sections 6 and 7 of the stack guideline. What is still open:

- [ ] Run it on a real Windows PC with Docker Desktop and WSL2 (the Windows steps are not verified yet, including that the port published on `127.0.0.1` opens in the Windows browser) and fix what the real run shows
- [ ] Document how to reach the local BTCPay Server from another device on the same network, and how to put a tunnel or reverse proxy in front of it later
- [ ] Decide whether the local mode needs a safeguard against switching to a public domain with a hot wallet that has no sweeper (the installer already requires a sweep address then)

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
