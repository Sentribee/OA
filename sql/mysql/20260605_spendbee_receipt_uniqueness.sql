SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_SpendBeeReceipt' AND COLUMN_NAME = 'ReceiptImageSetHash'
  ),
  'SELECT 1',
  'ALTER TABLE bee_SpendBeeReceipt ADD COLUMN ReceiptImageSetHash VARCHAR(128) NULL AFTER MerchantId'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_SpendBeeReceipt' AND COLUMN_NAME = 'ReceiptCanonicalHash'
  ),
  'SELECT 1',
  'ALTER TABLE bee_SpendBeeReceipt ADD COLUMN ReceiptCanonicalHash VARCHAR(128) NULL AFTER ReceiptImageSetHash'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_SpendBeeReceipt' AND INDEX_NAME = 'UX_bee_SpendBeeReceipt_Project_ImageSetHash'
  ),
  'SELECT 1',
  'ALTER TABLE bee_SpendBeeReceipt ADD UNIQUE KEY UX_bee_SpendBeeReceipt_Project_ImageSetHash (ProjectId, ReceiptImageSetHash)'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_SpendBeeReceipt' AND INDEX_NAME = 'UX_bee_SpendBeeReceipt_Project_CanonicalHash'
  ),
  'SELECT 1',
  'ALTER TABLE bee_SpendBeeReceipt ADD UNIQUE KEY UX_bee_SpendBeeReceipt_Project_CanonicalHash (ProjectId, ReceiptCanonicalHash)'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
