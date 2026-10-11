# Elektron Net: `elektron-net-btcpayplugin` Todo

- **Version:** 0.1 (draft)
- **Date:** October 11, 2026
- **Audience:** Maintainers of the BTCPay Server plugin
- **Reference implementation:** [`elektron-net`](https://github.com/kutlusoy/elektron-net) (chain parameters, wallet RPC), treat as ground truth for anything this doc references
- **See also:** [`guideline-operations.md`](guideline-operations.md), [`guideline-stack.md`](guideline-stack.md), [`README.MD`](../README.MD)

Open work items, newest first. Remove an item when it is done and verified.

---

## 1. ELEK Rate Source for Fiat Priced Invoices

**Problem.** The plugin has no exchange rate source. Invoices can only be priced directly in ELEK. A merchant who already accepts BTC and Monero on an existing BTCPay Server instance and prices invoices in a fiat currency (EUR, USD) does not get ELEK offered at checkout, because BTCPay cannot convert the fiat amount into ELEK.

**Goal.** A merchant with a mixed setup can enable ELEK next to BTC and XMR and keep pricing in fiat.

**Tasks.**

- [ ] Read how BTCPay Server resolves rates for a payment method (rate rules, rate providers, the currency pair `ELEK_<fiat>`) and what happens for a currency without any provider
- [ ] Decide the source: a fixed rate set by the merchant through the store rate rules, an external price feed for ELEK, or both
- [ ] If the store rate rules can express a fixed pair, document the rule and verify it on a running instance
- [ ] If a rate provider is needed, implement it in the plugin and register it, with a clear failure state when no price is available (ELEK MUST NOT be offered with a stale or missing rate)
- [ ] Show the used rate on the checkout and on the invoice page
- [ ] Add unit tests for the conversion and for the missing-rate case
- [ ] Describe the setup for existing multi-currency instances in the operations guideline
- [ ] Verify with an instance that already runs BTC and XMR: enable ELEK for a store, create an EUR invoice, pay it in ELEK

**Open questions.**

1. Is there a public price feed for ELEK, or does the project publish one?
2. Should the plugin refuse fiat invoices until a rate is configured, or fall back to a fixed merchant rate?

## Links

- Website: https://elektron-net.org
- X (Twitter): @elektronnet and @kutlusoy
- Telegram: @elektronnet
