-- Shmesh POS: one-time PostgreSQL setup.
-- Run as a superuser (e.g. "postgres") in psql or pgAdmin, AFTER replacing the password below.
--
--   psql -U postgres -f scripts/create-database.sql
--
-- The application then creates and migrates all tables itself on first start.

CREATE ROLE shmesh_pos WITH LOGIN PASSWORD 'CHANGE_ME';

-- Development machines only: lets the integration tests create and drop throwaway databases.
-- Do not grant this on a production server.
-- ALTER ROLE shmesh_pos CREATEDB;

CREATE DATABASE shmesh_pos OWNER shmesh_pos ENCODING 'UTF8';
