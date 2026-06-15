SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_Project' AND COLUMN_NAME = 'ProjectKind'
  ),
  'SELECT 1',
  'ALTER TABLE bee_Project ADD COLUMN ProjectKind VARCHAR(40) NOT NULL DEFAULT ''EdgeAi'' AFTER WebsiteUrl'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

UPDATE bee_Project
SET ProjectKind = 'EdgeAi'
WHERE ProjectKind IS NULL OR ProjectKind = '';

UPDATE bee_Project
SET ProjectKind = 'SpendBee'
WHERE ProjectName = 'SpendBee';

UPDATE bee_Project
SET ProjectKind = 'SentribeeCrm'
WHERE ProjectName IN ('Sentribee CRM', 'SentriBee CRM', 'crm.sentribee.ai')
   OR WebsiteUrl IN ('https://crm.sentribee.ai', 'http://crm.sentribee.ai', 'crm.sentribee.ai');
