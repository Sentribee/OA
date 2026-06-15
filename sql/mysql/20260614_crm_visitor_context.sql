SET @sql := IF(
  NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmConversation' AND COLUMN_NAME = 'VisitorKey'
  ),
  'ALTER TABLE bee_CrmConversation ADD COLUMN VisitorKey VARCHAR(80) NULL AFTER VisitorLabel',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmConversation' AND COLUMN_NAME = 'VisitorIp'
  ),
  'ALTER TABLE bee_CrmConversation ADD COLUMN VisitorIp VARCHAR(80) NULL AFTER VisitorKey',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmConversation' AND COLUMN_NAME = 'UserAgent'
  ),
  'ALTER TABLE bee_CrmConversation ADD COLUMN UserAgent VARCHAR(500) NULL AFTER VisitorIp',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmConversation' AND COLUMN_NAME = 'Referrer'
  ),
  'ALTER TABLE bee_CrmConversation ADD COLUMN Referrer VARCHAR(1000) NULL AFTER UserAgent',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmConversation' AND COLUMN_NAME = 'LastSeenAtUtc'
  ),
  'ALTER TABLE bee_CrmConversation ADD COLUMN LastSeenAtUtc DATETIME(6) NULL AFTER LastMessageAtUtc',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmConversation' AND INDEX_NAME = 'IX_bee_CrmConversation_Merchant_Visitor'
  ),
  'ALTER TABLE bee_CrmConversation ADD INDEX IX_bee_CrmConversation_Merchant_Visitor (MerchantId, VisitorKey, LastMessageAtUtc)',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmConversation' AND INDEX_NAME = 'IX_bee_CrmConversation_Merchant_IpUa'
  ),
  'ALTER TABLE bee_CrmConversation ADD INDEX IX_bee_CrmConversation_Merchant_IpUa (MerchantId, VisitorIp, UserAgent, LastMessageAtUtc)',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmCustomerProfile' AND COLUMN_NAME = 'VisitorKey'
  ),
  'ALTER TABLE bee_CrmCustomerProfile ADD COLUMN VisitorKey VARCHAR(80) NULL AFTER VisitorLabel',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmCustomerProfile' AND COLUMN_NAME = 'VisitorIp'
  ),
  'ALTER TABLE bee_CrmCustomerProfile ADD COLUMN VisitorIp VARCHAR(80) NULL AFTER VisitorKey',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmCustomerProfile' AND COLUMN_NAME = 'UserAgent'
  ),
  'ALTER TABLE bee_CrmCustomerProfile ADD COLUMN UserAgent VARCHAR(500) NULL AFTER VisitorIp',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmCustomerProfile' AND INDEX_NAME = 'UX_bee_CrmCustomerProfile_Merchant_Visitor'
  ),
  'ALTER TABLE bee_CrmCustomerProfile ADD UNIQUE KEY UX_bee_CrmCustomerProfile_Merchant_Visitor (MerchantId, VisitorKey)',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmCustomerProfile' AND INDEX_NAME = 'IX_bee_CrmCustomerProfile_Merchant_IpUa'
  ),
  'ALTER TABLE bee_CrmCustomerProfile ADD INDEX IX_bee_CrmCustomerProfile_Merchant_IpUa (MerchantId, VisitorIp, UserAgent)',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
