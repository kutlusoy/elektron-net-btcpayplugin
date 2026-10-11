# Elektron Net: `elektron-net-btcpayplugin` BTCPay Server Plugin

- **Version:** 0.1 (draft)
- **Date:** October 11, 2026
- **Audience:** Plugin developers, merchants running BTCPay Server who want to accept ELEK
- **Reference implementation:** [`elektron-net`](https://github.com/kutlusoy/elektron-net) (`src/wallet/rpc/`, `src/wallet/init.cpp`, `src/kernel/chainparams.cpp`), treat as ground truth for anything this doc references
- **Pattern source:** [`btcpayserver-monero-plugin`](https://github.com/btcpay-monero/btcpayserver-monero-plugin) (MIT), structure of a BTCPay Server plugin that does not use NBXplorer
- **See also:** [`doc-elektron/guideline-operations.md`](doc-elektron/guideline-operations.md) (setup, operation, switching off), [`elektron-net-payments`](https://github.com/kutlusoy/elektron-net-payments) (`pay-server`, escrow), [`elektron-net-mempool`](https://github.com/kutlusoy/elektron-net-mempool) (block explorer)

---

## 1. Purpose and scope

This plugin lets an unmodified [BTCPay Server](https://github.com/btcpayserver/btcpayserver) release accept Elektron Net (ELEK) on-chain payments. It is a regular BTCPay Server plugin: no BTCPay Server fork, no NBXplorer, no NBitcoin patch.

In scope for the first versions:

- On-chain receive-only payments (one fresh `be1q...` address per invoice)
- Payment detection through the RPC interface of `elektrond`
- Checkout integration (QR code, `elek:` payment link, confirmation counter)
- Store settings page, invoice payment details and node status in the BTCPay Server UI

Out of scope:

- Sending, payouts, pull payments and the built-in BTCPay wallet UI (the merchant manages funds in their own wallet)
- Escrow (2-of-2 with CLTV timeouts), which is handled by `pay-server` in `elektron-net-payments`
- Lightning

For installation and day-to-day operation on Linux, Windows and macOS, read the [operations guideline](doc-elektron/guideline-operations.md).

## 2. Architecture

BTCPay Server's Bitcoin handler depends on NBXplorer. This plugin follows the same route as the Monero plugin instead: it registers its own network and payment method handler, and reports payments to BTCPay Server through `PaymentService`.

| Component | File | Role |
|---|---|---|
| Network | `ElektronBtcPayNetwork.cs` | `BTCPayNetworkBase` for `ELEK` (8 decimals, `elek` URI scheme) |
| Registration | `ElektronPlugin.cs` | Plugin entry point, DI wiring, UI extensions, explorer link |
| Logo | `elektron.svg` | Embedded resource, copy of `src/qt/res/src/bitcoin-elektron.svg` from `elektron-net` |
| RPC client | `Services/ElektronRpcClient.cs` | JSON-RPC against `elektrond` and its wallet endpoint |
| Node state | `Services/ElektronRpcProvider.cs` | Availability, sync state, watch-only check, automatic wallet loading |
| Block polling | `Services/ElektronSyncService.cs` | Detects new blocks, raises events |
| Payment detection | `Services/ElektronListener.cs` | Maps wallet transactions to invoices and payments |
| Node callbacks | `Controllers/ElektronDaemonCallbackController.cs` | Optional `walletnotify` and `blocknotify` endpoints |
| Invoice handler | `Payments/ElektronPaymentMethodHandler.cs` | Reserves an address per invoice |
| Checkout | `Payments/ElektronCheckoutModelExtension.cs`, `Payments/ElektronPaymentLinkExtension.cs` | QR code, payment link, confirmations |
| Store settings | `Controllers/UIElektronStoreController.cs`, `Views/Elektron/GetStoreElektronPaymentMethod.cshtml` | Enable ELEK per store, settlement threshold |
| Store navigation | `Views/Elektron/StoreWalletsNavElektronExtension.cshtml` | Entry under the store's wallets menu |
| Invoice details | `Views/Elektron/ViewElektronPaymentData.cshtml` | Payment table on the invoice page |
| Node status | `Services/ElektronSyncSummaryProvider.cs`, `Views/Elektron/ElektronSyncSummary.cshtml` | Entry in BTCPay's sync status |

Payment IDs have the form `<txid>-<vout>`, the same format BTCPay Server uses for Bitcoin outpoints, so its default transaction link provider works unchanged. On mainnet, transaction links point to `https://mempool.elektron-net.org/tx/<txid>`.

The plugin never holds private keys. It requires a watch-only wallet on the node and refuses to run against a wallet with private keys enabled. Receiving addresses are requested explicitly as Bech32 (`be1q...`), because legacy Base58 addresses are identical to Bitcoin mainnet addresses.

## 3. Node requirements

`elektrond` MUST be built with wallet support and reachable over RPC. Elektron Net enforces mandatory pruning, which is compatible with this design: the plugin only needs payments to freshly derived addresses and never rescans old history.

The merchant keeps the private keys in a separate signing wallet. The node used by BTCPay Server holds only the public descriptor, imported without a rescan:

```bash
elektron-cli createwallet btcpay true true "" false true true
elektron-cli -rpcwallet=btcpay importdescriptors '[{"desc":"wpkh([<fingerprint>/84h/1370h/0h]<account xpub>/0/*)#<checksum>","active":true,"internal":false,"range":[0,999],"timestamp":"now"}]'
```

The complete procedure, including how to obtain the descriptor from the signing wallet, is in the [operations guideline](doc-elektron/guideline-operations.md).

If the node restarts and the wallet is not loaded, the plugin asks the node to load it.

Optional node settings for instant notifications (the plugin also polls, so these only reduce latency):

```
walletnotify=curl -s "http://<btcpay host>/elektrondaemoncallback/tx?hash=%s"
blocknotify=curl -s "http://<btcpay host>/elektrondaemoncallback/block?hash=%s"
```

## 4. Configuration

| Environment variable | Description | Example |
|---|---|---|
| `BTCPAY_ELEK_DAEMON_URI` | **Required.** RPC URI of `elektrond` | `http://127.0.0.1:8332` |
| `BTCPAY_ELEK_DAEMON_USERNAME` | Optional. RPC user | `btcpay` |
| `BTCPAY_ELEK_DAEMON_PASSWORD` | Optional. RPC password | `secret` |
| `BTCPAY_ELEK_WALLET_NAME` | Optional. Watch-only wallet name (default `btcpay`) | `btcpay` |

The wallet is shared by all stores of the BTCPay Server instance, as in the Monero plugin. Use this plugin only on instances that are not shared between unrelated merchants. For that reason the store settings page requires server administrator permission in addition to store settings permission.

Per store, open **Wallets > Elektron Net** in the store navigation to enable ELEK and choose when an invoice counts as settled. The payment method id is `ELEK-CHAIN`.

## 5. Project layout

```
elektron-net-btcpayplugin/
    Plugins/Elektron/                     Plugin sources
    BTCPayServer.Plugins.UnitTests/       Unit tests
    doc-elektron/                         Guidelines
    btcpay-elektron-plugin.slnx           Solution
    .github/workflows/dotnet.yml          Build, test, plugin package
    submodules/                           BTCPay Server sources (git submodule, not yet added)
```

Build requirements: .NET 10 SDK, BTCPay Server as a submodule at `submodules/btcpayserver`:

```bash
git submodule add https://github.com/btcpayserver/btcpayserver submodules/btcpayserver
dotnet build btcpay-elektron-plugin.slnx
dotnet test --project BTCPayServer.Plugins.UnitTests
```

The code has not been compiled yet. Its use of BTCPay Server APIs was checked by reading the BTCPay Server source at commit `ae3abbb0` (September 30, 2026), the revision pinned by the Monero plugin at the time of writing.

## 6. Differences from the Monero plugin

| Monero plugin | This plugin |
|---|---|
| `monero-wallet-rpc` creates subaddresses and reports transfers | `elektrond` wallet RPC (`getnewaddress`, `listreceivedbyaddress`, `gettransaction`) |
| Wallet created from the BTCPay UI (view key) | Wallet created on the node from a public descriptor, see the operations guideline |
| `MoneroLoadUpService` opens the wallet at startup | The RPC provider calls `loadwallet` when the node reports the wallet as not loaded |
| Account index, unlock time, fee estimate | Not applicable, no network fee is added to invoices |
| Integration tests against a `monerod` regtest setup | Not yet written |

## 7. Limitations

1. No ELEK exchange rate source exists, so no default rate rules are set. Invoices can only be priced in ELEK until a rate source exists.
2. Receive-only. Refunds and payouts are done manually from the merchant's own wallet.
3. One shared wallet per BTCPay Server instance.
4. Reorg handling is limited to updating confirmation counts of known payments. A payment whose transaction is replaced stays in its last state.

## 8. Checklist

- [ ] Add the BTCPay Server submodule (`.gitmodules` and the gitlink are created by `git submodule add`)
- [ ] Compile against the BTCPay Server revision and fix API mismatches
- [ ] Run the unit tests
- [ ] Verify that the logo renders in the checkout (the SVG draws the letter with a `text` element and relies on an installed Arial or fallback font)
- [ ] Run the wallet setup from the operations guideline against `elektrond` (pruned node)
- [ ] Verify `getnewaddress`, `listreceivedbyaddress` and `gettransaction` output on a watch-only wallet
- [ ] Integration tests with `elektrond` in regtest mode, modeled on the Monero plugin's test setup
- [ ] Adopt `.editorconfig` and `.globalconfig` from the Monero plugin after the first successful build, plus a format check workflow
- [ ] Map Elektron testnet and regtest chain names, and the explorer link for them
- [ ] Handle conflicted and replaced transactions
- [ ] Release packaging and publication

## 9. Open Questions

1. Should each store get its own wallet to remove the shared-wallet restriction?
2. Which confirmation counts fit a 60 second block target for the speed policies?

## License

MIT, see [LICENSE](LICENSE). Parts of the plugin structure derive from the MIT-licensed `btcpayserver-monero-plugin`; its notice is preserved.

## Links

- Website: https://elektron-net.org
- X (Twitter): @elektronnet and @kutlusoy
- Telegram: @elektronnet
