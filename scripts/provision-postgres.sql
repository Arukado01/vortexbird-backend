\set ON_ERROR_STOP on
\set ECHO none
-- Run once against the maintenance database as the database administrator.
-- Secrets enter through the process environment, never through -v or command-line arguments.
\getenv database_name VORTEXBIRD_DATABASE_NAME
\getenv migrator_role VORTEXBIRD_MIGRATOR_ROLE
\getenv api_role VORTEXBIRD_API_ROLE
\getenv worker_role VORTEXBIRD_WORKER_ROLE
\getenv migrator_password VORTEXBIRD_MIGRATOR_PASSWORD
\getenv api_password VORTEXBIRD_API_PASSWORD
\getenv worker_password VORTEXBIRD_WORKER_PASSWORD

SELECT current_user AS provisioner,
       :'database_name' ~ '^[a-z_][a-z0-9_]{0,62}$'
       AND :'database_name' NOT IN ('postgres', 'template0', 'template1')
       AND :'migrator_role' ~ '^[a-z_][a-z0-9_]{0,62}$'
       AND :'api_role' ~ '^[a-z_][a-z0-9_]{0,62}$'
       AND :'worker_role' ~ '^[a-z_][a-z0-9_]{0,62}$'
       AND :'migrator_role' NOT LIKE 'pg\_%' AND :'api_role' NOT LIKE 'pg\_%' AND :'worker_role' NOT LIKE 'pg\_%'
       AND :'migrator_role' <> 'postgres' AND :'api_role' <> 'postgres' AND :'worker_role' <> 'postgres'
       AND :'migrator_role' <> :'api_role' AND :'migrator_role' <> :'worker_role' AND :'api_role' <> :'worker_role'
       AND length(:'migrator_password') >= 32 AND length(:'api_password') >= 32 AND length(:'worker_password') >= 32
       AND NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'database_name')
       AND NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname IN (:'migrator_role', :'api_role', :'worker_role')) AS safe_new_database
\gset
\if :safe_new_database
\else
  \echo 'Refusing provisioning: configuration is invalid or database/roles already exist. No existing object is modified.'
  \quit 2
\endif

BEGIN;
SET LOCAL password_encryption = 'scram-sha-256';
CREATE ROLE :"migrator_role" LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD :'migrator_password';
CREATE ROLE :"api_role" LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD :'api_password';
CREATE ROLE :"worker_role" LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD :'worker_password';
-- PostgreSQL 17 requires SET ROLE permission to create a database owned by another role.
GRANT :"migrator_role" TO :"provisioner" WITH SET TRUE, INHERIT FALSE;
COMMIT;
CREATE DATABASE :"database_name" OWNER :"migrator_role";
\connect :database_name
SET ROLE :"migrator_role";
REVOKE ALL PRIVILEGES ON DATABASE :"database_name" FROM PUBLIC;
REVOKE ALL PRIVILEGES ON SCHEMA public FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL PRIVILEGES ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL PRIVILEGES ON SEQUENCES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL PRIVILEGES ON FUNCTIONS FROM PUBLIC;
GRANT CONNECT ON DATABASE :"database_name" TO :"api_role", :"worker_role";
GRANT USAGE ON SCHEMA public TO :"api_role", :"worker_role";
RESET ROLE;
REVOKE :"migrator_role" FROM :"provisioner";
\echo 'Dedicated database and roles created. Apply the migration job before starting API or worker.'
