SET @sql := IF(
  NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmConversation' AND COLUMN_NAME = 'VisitorEmail'
  ),
  'ALTER TABLE bee_CrmConversation ADD COLUMN VisitorEmail VARCHAR(180) NULL AFTER VisitorLabel',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmConversation' AND COLUMN_NAME = 'EmailVerifiedAtUtc'
  ),
  'ALTER TABLE bee_CrmConversation ADD COLUMN EmailVerifiedAtUtc DATETIME(6) NULL AFTER VisitorEmail',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmConversation' AND INDEX_NAME = 'IX_bee_CrmConversation_Merchant_Email'
  ),
  'ALTER TABLE bee_CrmConversation ADD INDEX IX_bee_CrmConversation_Merchant_Email (MerchantId, VisitorEmail, LastMessageAtUtc)',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
