-- Usage: psql -v iam_password="$IAM_POSTGRES_PASSWORD" -f setup-database.sql
-- The password is supplied by the caller and is never stored in this file.
\if :{?iam_password}
\else
  \echo 'Set the iam_password psql variable (psql -v iam_password=...)'
  \quit
\endif

-- Create user if not exists
SELECT format('CREATE USER iamuser WITH PASSWORD %L', :'iam_password')
WHERE NOT EXISTS (SELECT FROM pg_catalog.pg_user WHERE usename = 'iamuser')\gexec

-- Create database if not exists
SELECT 'CREATE DATABASE iamdb OWNER iamuser'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'iamdb')\gexec

-- Grant privileges
GRANT ALL PRIVILEGES ON DATABASE iamdb TO iamuser;

-- Connect to the new database and grant schema privileges
\c iamdb
GRANT ALL ON SCHEMA public TO iamuser;
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO iamuser;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO iamuser;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO iamuser;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO iamuser;
