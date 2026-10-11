#!/bin/sh
set -e

chown -R elektron:elektron /data

exec gosu elektron "$@"
