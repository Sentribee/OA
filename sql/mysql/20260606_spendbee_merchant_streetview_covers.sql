SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_SpendBeeMerchant' AND COLUMN_NAME = 'CoverSource'
  ),
  'SELECT 1',
  'ALTER TABLE bee_SpendBeeMerchant ADD COLUMN CoverSource VARCHAR(40) NULL AFTER AiCoverPrompt'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_SpendBeeMerchant' AND COLUMN_NAME = 'CoverCategory'
  ),
  'SELECT 1',
  'ALTER TABLE bee_SpendBeeMerchant ADD COLUMN CoverCategory VARCHAR(80) NULL AFTER CoverSource'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_SpendBeeMerchant' AND COLUMN_NAME = 'StreetViewImageUrl'
  ),
  'SELECT 1',
  'ALTER TABLE bee_SpendBeeMerchant ADD COLUMN StreetViewImageUrl VARCHAR(1000) NULL AFTER CoverCategory'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

UPDATE bee_SpendBeeMerchant
SET CoverSource = CASE
    WHEN AiCoverImageUrl IS NOT NULL AND AiCoverImageUrl <> '' THEN 'LegacyAiConcept'
    WHEN GooglePhotoUri IS NOT NULL AND GooglePhotoUri <> '' THEN 'GooglePhoto'
    ELSE CoverSource
  END,
  CoverCategory = CASE
    WHEN CoverCategory IS NULL THEN COALESCE(NULLIF(PrimaryType, ''), 'restaurant')
    ELSE CoverCategory
  END
WHERE CoverSource IS NULL OR CoverCategory IS NULL;
