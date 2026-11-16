-- BASI Development Database Initialization
-- This runs on first container creation only

-- Create extensions
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- Verify setup
SELECT version();
