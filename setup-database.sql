-- Create user if not exists
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_user WHERE usename = 'iamuser') THEN
        CREATE USER iamuser WITH PASSWORD 'iampassword';
    END IF;
END
$$;

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
