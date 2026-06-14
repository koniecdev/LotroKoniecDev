#!/bin/sh
# Postgres docker-entrypoint-initdb.d script.
# The official postgres image creates POSTGRES_DB (lotro_translation) on first boot;
# this script adds the second database that AuthSystem needs.
# Runs only when the data volume is empty (first container boot).
set -e

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
    CREATE DATABASE "lotro_auth";
EOSQL
