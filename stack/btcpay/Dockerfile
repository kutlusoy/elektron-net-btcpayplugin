ARG BTCPAY_VERSION=2.3.7

FROM mcr.microsoft.com/dotnet/sdk:10.0.400-noble AS plugin-builder
ARG BTCPAY_VERSION
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
RUN git clone --depth 1 --branch "v${BTCPAY_VERSION}" https://github.com/btcpayserver/btcpayserver submodules/btcpayserver

COPY global.json global.json
COPY Plugins Plugins

RUN dotnet publish Plugins/Elektron/BTCPayServer.Plugins.Elektron.csproj -c Release -o /out/plugin \
    && dotnet run --project submodules/btcpayserver/BTCPayServer.PluginPacker -c Release -- \
        /out/plugin BTCPayServer.Plugins.Elektron /out/packed \
    && mkdir /out/final \
    && cp /out/packed/BTCPayServer.Plugins.Elektron/*/BTCPayServer.Plugins.Elektron.btcpay /out/final/ \
    && sha256sum /out/final/BTCPayServer.Plugins.Elektron.btcpay | cut -d' ' -f1 > /out/final/CHECKSUM

FROM btcpayserver/btcpayserver:${BTCPAY_VERSION}

COPY --from=plugin-builder /out/final/ /opt/elektron-plugin/
COPY --chmod=0755 stack/btcpay/entrypoint.sh /usr/local/bin/elektron-entrypoint.sh

ENTRYPOINT ["/usr/local/bin/elektron-entrypoint.sh"]
