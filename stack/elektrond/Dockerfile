FROM debian:bookworm-slim AS build

RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential cmake pkgconf python3 \
        libevent-dev libboost-dev libsqlite3-dev libzmq3-dev \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /build
COPY . .

RUN cmake -B build \
        -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_GUI=OFF \
        -DWITH_ZMQ=ON \
    && cmake --build build -j"$(nproc)" \
    && cmake --install build

FROM debian:bookworm-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
        libevent-2.1-7 libevent-pthreads-2.1-7 \
        libsqlite3-0 libzmq5 \
        ca-certificates gosu \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd -r elektron && useradd -r -g elektron -d /data -s /usr/sbin/nologin elektron \
    && mkdir -p /data && chown elektron:elektron /data

COPY --from=build /usr/local/bin/elektron* /usr/local/bin/
COPY --from=node --chmod=0755 docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh

VOLUME ["/data"]

# The RPC port is never published to the host
EXPOSE 8333 8332

ENTRYPOINT ["docker-entrypoint.sh"]
CMD ["elektrond", "-conf=/etc/elektron/elektron.conf", "-datadir=/data", "-printtoconsole"]
