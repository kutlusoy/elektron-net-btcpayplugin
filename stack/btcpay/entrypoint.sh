#!/usr/bin/env bash
set -euo pipefail

PLUGIN_NAME="BTCPayServer.Plugins.Elektron"
PLUGIN_DIR="${BTCPAY_PLUGINDIR:-/datadir/plugins}"
SOURCE_DIR="${ELEKTRON_PLUGIN_SOURCE:-/opt/elektron-plugin}"
MARKER="${PLUGIN_DIR}/.${PLUGIN_NAME}.checksum"

mkdir -p "${PLUGIN_DIR}"

# BTCPay installs a plugin from a queued "install" command on startup and deletes the package afterwards
checksum="$(cat "${SOURCE_DIR}/CHECKSUM")"
if [[ "$(cat "${MARKER}" 2>/dev/null || true)" != "${checksum}" ]]; then
    cp "${SOURCE_DIR}/${PLUGIN_NAME}.btcpay" "${PLUGIN_DIR}/${PLUGIN_NAME}.btcpay"
    echo "install:${PLUGIN_NAME}" >> "${PLUGIN_DIR}/commands"
    echo "${checksum}" > "${MARKER}"
fi

exec /app/docker-entrypoint.sh "$@"
