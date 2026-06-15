CREATE TABLE IF NOT EXISTS bee_SpendBeeMerchant (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  GooglePlaceId VARCHAR(160) NULL,
  GooglePlaceResourceName VARCHAR(240) NULL,
  Name VARCHAR(220) NOT NULL,
  NormalizedName VARCHAR(220) NOT NULL,
  Address VARCHAR(600) NULL,
  PhoneNumber VARCHAR(80) NULL,
  WebsiteUrl VARCHAR(700) NULL,
  GoogleMapsUri VARCHAR(700) NULL,
  PrimaryType VARCHAR(120) NULL,
  BusinessStatus VARCHAR(80) NULL,
  Latitude DECIMAL(10,7) NULL,
  Longitude DECIMAL(10,7) NULL,
  Rating DECIMAL(4,2) NULL,
  UserRatingCount INT NULL,
  PriceLevel VARCHAR(80) NULL,
  DineIn TINYINT(1) NULL,
  Takeout TINYINT(1) NULL,
  GooglePhotoName VARCHAR(500) NULL,
  GooglePhotoUri VARCHAR(1000) NULL,
  GooglePhotoAttributionsJson JSON NULL,
  AiCoverImageUrl VARCHAR(1000) NULL,
  AiCoverPrompt VARCHAR(1600) NULL,
  SourceJson JSON NULL,
  SyncStatus VARCHAR(40) NOT NULL DEFAULT 'LocalOnly',
  LastGoogleSyncAtUtc DATETIME(6) NULL,
  LastAiCoverGeneratedAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_SpendBeeMerchant_Project_GooglePlace (ProjectId, GooglePlaceId),
  KEY IX_bee_SpendBeeMerchant_Project_Name (ProjectId, NormalizedName),
  KEY IX_bee_SpendBeeMerchant_Project_Updated (ProjectId, UpdatedAtUtc),
  CONSTRAINT FK_bee_SpendBeeMerchant_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_SpendBeeReceipt' AND COLUMN_NAME = 'MerchantId'
  ),
  'SELECT 1',
  'ALTER TABLE bee_SpendBeeReceipt ADD COLUMN MerchantId BIGINT NULL AFTER AppUserId'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_SpendBeeReceipt' AND INDEX_NAME = 'IX_bee_SpendBeeReceipt_Merchant_Time'
  ),
  'SELECT 1',
  'ALTER TABLE bee_SpendBeeReceipt ADD KEY IX_bee_SpendBeeReceipt_Merchant_Time (MerchantId, CreatedAtUtc)'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_SpendBeeReceipt' AND CONSTRAINT_NAME = 'FK_bee_SpendBeeReceipt_Merchant'
  ),
  'SELECT 1',
  'ALTER TABLE bee_SpendBeeReceipt ADD CONSTRAINT FK_bee_SpendBeeReceipt_Merchant FOREIGN KEY (MerchantId) REFERENCES bee_SpendBeeMerchant (id) ON DELETE SET NULL'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
