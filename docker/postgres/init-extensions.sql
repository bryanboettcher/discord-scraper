-- timescaledb-ha image includes both extensions; ensure they're active on first boot.
CREATE EXTENSION IF NOT EXISTS timescaledb;
CREATE EXTENSION IF NOT EXISTS vector;
