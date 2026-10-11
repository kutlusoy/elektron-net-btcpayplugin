# Elektron Net: `elektron-net-btcpayplugin` BTCPay Server Plugin

- **Version:** 0.1 (draft)
- **Date:** October 11, 2026
- **Audience:** Plugin developers, merchants running BTCPay Server who want to accept ELEK
- **Reference implementation:** [`elektron-net`](https://github.com/kutlusoy/elektron-net) (`src/wallet/rpc/`, `src/wallet/init.cpp`, `src/kernel/chainparams.cpp`), treat as ground truth for anything this doc references
- **Pattern source:** [`btcpayserver-monero-plugin`](https://github.com/btcpay-monero/btcpayserver-monero-plugin) (MIT), structure of a BTCPay Server plugin that does not use NBXplorer
- **See also:** [`elektron-net-payments`](https://github.com/kutlusoy/elektron-net-payments) (`pay-server`, escrow), [`elektron-net-mempool`](https://github.com/kutlusoy/elektron-net-mempool) (block explorer)

---

## 1. Purpose and scope

This plugin lets an unmodified [BTCPay Server](https://github.com/btcpayserver/btcpayserver) release accept Elektron Net (ELEK) on-chain payments. It is a regular BTCPay Server plugin: no BTCPay Server fork, no NBXplorer, no NBitcoin patch.

In scope for the first versions:

- On-chain receive-only payments (one fresh address per invoice)
- Payment detection through the RPC interface of `elektrond`
- Checkout integration (QR code, `elek:` payment link, confirmation counter)

Out of scope:

- Sending, payouts, pull payments and the built-in BTCPay wallet UI (the merchant manages funds in their own wallet)
- Escrow (2-of-2 with CLTV timeouts), which is handled by `pay-server` in `elektron-net-payments`
- Lightning

## 2. Architecture

BTCPay Server's Bitcoin handler depends on NBXplorer. This plugin follows the same route as the Monero plugin instead: it registers its own network and payment method handler, and reports payments to BTCPay Server through `PaymentService`.

| Component | File | Role |
|---|---|---|
| Network | `ElektronBtcPayNetwork.cs` | `BTCPayNetworkBase` for `ELEK` (8 decimals, `elek` URI scheme) |
| Registration | `ElektronPlugin.cs` | Plugin entry point, DI wiring, explorer link |
| RPC client | `Services/ElektronRpcClient.cs` | JSON-RPC against `elektrond` and its wallet endpoint |
| Node state | `Services/ElektronRpcProvider.cs` | Availability, sync state, watch-only check |
| Block polling | `Services/ElektronSyncService.cs` | Detects new blocks, raises events |
| Payment detection | `Services/ElektronListener.cs` | Maps wallet transactions to invoices and payments |
| Node callbacks | `Controllers/ElektronDaemonCallbackController.cs` | Optional `walletnotify` and `blocknotify` endpoints |
| Invoice handler | `Payments/ElektronPaymentMethodHandler.cs` | Reserves an address per invoice |
| Checkout | `Payments/ElektronCheckoutModelExtension.cs`, `Payments/ElektronPaymentLinkExtension.cs` | QR code, payment link, confirmations |

Payment IDs have the form `<txid>:<vout>`.

The plugin never holds private keys. It requires a watch-only wallet on the node and refuses to run against a wallet with private keys enabled.

## 3. Node requirements

`elektrond` MUST be built with wallet support and reachable over RPC. Elektron Net enforces mandatory pruning, which is compatible with this design: the plugin only needs payments to freshly derived addresses and never rescans old history.

Expected one-time setup (commands are based on the standard descriptor wallet RPCs present in `elektron-net`, not yet verified end to end):

```bash
elektron-cli createwallet btcpay true true "" false true
elektron-cli -rpcwallet=btcpay getdescriptorinfo "wpkh(<account xpub>/0/*)"
elektron-cli -rpcwallet=btcpay importdescriptors '[{"desc":"wpkh(<account xpub>/0/*)#<checksum>","active":true,"range":[0,999],"timestamp":"now"}]'
```

Optional node settings for instant notifications (the plugin also polls, so these only reduce latency):

```
walletnotify=curl -s "http://<btcpay host>/elektrondaemoncallback/tx?hash=%s"
blocknotify=curl -s "http://<btcpay host>/elektrondaemoncallback/block?hash=%s"
```

## 4. Configuration

| Environment variable | Description | Example |
|---|---|---|
| `BTCPAY_ELEK_DAEMON_URI` | **Required.** RPC URI of `elektrond` | `http://127.0.0.1:8332` |
| `BTCPAY_ELEK_DAEMON_USERNAME` | Optional. RPC user | `rpcuser` |
| `BTCPAY_ELEK_DAEMON_PASSWORD` | Optional. RPC password | `secret` |
| `BTCPAY_ELEK_WALLET_NAME` | Optional. Watch-only wallet name (default `btcpay`) | `btcpay` |

The wallet is shared by all stores of the BTCPay Server instance, as in the Monero plugin. Use this plugin only on instances that are not shared between unrelated merchants.

The per-store payment method config currently has one field, `InvoiceSettledConfirmationThreshold`. A store settings page is not part of this draft; enabling the method is expected to go through the Greenfield payment methods endpoint (`ELEK-CHAIN`).

## 5. Project layout

```
elektron-net-btcpayplugin/
    Plugins/Elektron/       Plugin sources
    submodules/             BTCPay Server sources (git submodule, not yet added)
```

Build requirements: .NET 10 SDK, BTCPay Server as a submodule at `submodules/btcpayserver`:

```bash
git submodule add https://github.com/btcpayserver/btcpayserver submodules/btcpayserver
dotnet build Plugins/Elektron/BTCPayServer.Plugins.Elektron.csproj
```

The skeleton has not been compiled yet.

## 6. Limitations

1. No ELEK exchange rate source exists, so no default rate rules are set. Invoices can only be priced in ELEK until a rate source exists.
2. Receive-only. Refunds and payouts are done manually from the merchant's own wallet.
3. One shared wallet per BTCPay Server instance.
4. Reorg handling is limited to updating confirmation counts of known payments.

## 7. Checklist

- [ ] Add the BTCPay Server submodule and a solution file
- [ ] Compile against BTCPay Server 2.3.7 or newer and fix API mismatches
- [ ] Verify that a network without `DefaultRateRules` registers cleanly
- [ ] Add the ELEK logo as an embedded resource and set `CryptoImagePath`
- [ ] Verify wallet creation and descriptor import against `elektrond` (pruned node)
- [ ] Verify `getnewaddress` and `gettransaction` output on a watch-only wallet
- [ ] Add a store settings page or document the Greenfield call
- [ ] Map Elektron testnet and regtest chain names
- [ ] Handle conflicted and replaced transactions
- [ ] Unit tests for confirmation policy and payment mapping
- [ ] CI workflow and release packaging

## 8. Open Questions

1. Should each store get its own wallet to remove the shared-wallet restriction?
2. Which public block explorer URL should be used for transaction links once `elektron-net-mempool` is deployed?
3. Which confirmation counts fit a 60 second block target for the speed policies?

## License

MIT, see [LICENSE](LICENSE). Parts of the plugin structure derive from the MIT-licensed `btcpayserver-monero-plugin`; its notice is preserved.

## Links

- Website: https://elektron-net.org
- X (Twitter): @elektronnet and @kutlusoy
- Telegram: @elektronnet
