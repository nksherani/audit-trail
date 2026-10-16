-- Run once in the application database using a separate deployment principal.
-- Assign a dedicated non-owner application login to AuditAppWriter. Never use db_owner in production.
IF DATABASE_PRINCIPAL_ID(N'AuditAppWriter') IS NULL CREATE ROLE [AuditAppWriter];
IF DATABASE_PRINCIPAL_ID(N'AuditHistoryReader') IS NULL CREATE ROLE [AuditHistoryReader];
IF DATABASE_PRINCIPAL_ID(N'AuditRetentionAdmin') IS NULL CREATE ROLE [AuditRetentionAdmin];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.Categories TO [AuditAppWriter];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.Suppliers TO [AuditAppWriter];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.Products TO [AuditAppWriter];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.ImportCheckpoint TO [AuditAppWriter];
GRANT SELECT, INSERT ON OBJECT::dbo.AuditOperations TO [AuditAppWriter];
GRANT INSERT ON OBJECT::dbo.AuditTrail TO [AuditAppWriter];
DENY UPDATE, DELETE ON OBJECT::dbo.AuditTrail TO [AuditAppWriter];
DENY UPDATE, DELETE ON OBJECT::dbo.AuditOperations TO [AuditAppWriter];
GRANT SELECT ON OBJECT::dbo.AuditTrail TO [AuditHistoryReader];
GRANT SELECT, DELETE ON OBJECT::dbo.AuditTrail TO [AuditRetentionAdmin];
GRANT SELECT, DELETE ON OBJECT::dbo.AuditOperations TO [AuditRetentionAdmin];
-- Example (replace with existing database users):
-- ALTER ROLE AuditAppWriter ADD MEMBER ApplicationUser;
-- ALTER ROLE AuditHistoryReader ADD MEMBER HistoryReaderUser;
-- The combined demo API needs both writer and reader roles, but endpoint authorization remains required.
-- Do not combine retention and ordinary writer identities; DENY takes precedence over GRANT.
