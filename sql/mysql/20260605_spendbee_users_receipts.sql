SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_AppUser' AND COLUMN_NAME = 'AvatarUrl'
  ),
  'SELECT 1',
  'ALTER TABLE bee_AppUser ADD COLUMN AvatarUrl VARCHAR(500) NULL AFTER Gender'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_AppUser' AND COLUMN_NAME = 'Bio'
  ),
  'SELECT 1',
  'ALTER TABLE bee_AppUser ADD COLUMN Bio VARCHAR(280) NULL AFTER AvatarUrl'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_AppUser' AND COLUMN_NAME = 'ActivatedAtUtc'
  ),
  'SELECT 1',
  'ALTER TABLE bee_AppUser ADD COLUMN ActivatedAtUtc DATETIME(6) NULL AFTER Status'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_AppUserDevice' AND COLUMN_NAME = 'DeviceKeyHash'
  ),
  'SELECT 1',
  'ALTER TABLE bee_AppUserDevice ADD COLUMN DeviceKeyHash VARCHAR(128) NULL AFTER DeviceIdentifier'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

UPDATE bee_AppUserDevice
SET DeviceKeyHash = SHA2(CONCAT(AppUserId, ':', DeviceIdentifier, ':', COALESCE(PushToken, '')), 256)
WHERE DeviceKeyHash IS NULL;

CREATE TABLE IF NOT EXISTS bee_SpendBeeReceipt (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  AppUserId INT NOT NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Processing',
  MerchantName VARCHAR(200) NULL,
  MerchantAddress VARCHAR(500) NULL,
  PurchasedAtUtc DATETIME(6) NULL,
  Currency VARCHAR(12) NULL,
  Subtotal DECIMAL(12,2) NULL,
  Tax DECIMAL(12,2) NULL,
  Total DECIMAL(12,2) NULL,
  OverallConfidence DECIMAL(8,5) NULL,
  EstimatedErrorRate DECIMAL(8,5) NULL,
  FailedChecksJson JSON NULL,
  RawOcrJson JSON NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_SpendBeeReceipt_Project_Time (ProjectId, CreatedAtUtc),
  KEY IX_bee_SpendBeeReceipt_User_Time (AppUserId, CreatedAtUtc),
  KEY IX_bee_SpendBeeReceipt_Status (ProjectId, Status),
  CONSTRAINT FK_bee_SpendBeeReceipt_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeReceipt_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeReceiptImage (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ReceiptId BIGINT NOT NULL,
  ImageUrl VARCHAR(800) NOT NULL,
  ContentType VARCHAR(80) NOT NULL,
  SortOrder INT NOT NULL DEFAULT 0,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_SpendBeeReceiptImage_Receipt (ReceiptId, SortOrder),
  CONSTRAINT FK_bee_SpendBeeReceiptImage_Receipt FOREIGN KEY (ReceiptId)
    REFERENCES bee_SpendBeeReceipt (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeReceiptLineItem (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ReceiptId BIGINT NOT NULL,
  ItemName VARCHAR(240) NOT NULL,
  Quantity DECIMAL(12,3) NULL,
  UnitPrice DECIMAL(12,2) NULL,
  Amount DECIMAL(12,2) NULL,
  Category VARCHAR(80) NULL,
  Confidence DECIMAL(8,5) NULL,
  SortOrder INT NOT NULL DEFAULT 0,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_SpendBeeReceiptLineItem_Receipt (ReceiptId, SortOrder),
  CONSTRAINT FK_bee_SpendBeeReceiptLineItem_Receipt FOREIGN KEY (ReceiptId)
    REFERENCES bee_SpendBeeReceipt (id) ON DELETE CASCADE
) ENGINE=InnoDB;

INSERT INTO bee_Admin (LoginID, Pwd, Roles, DisplayName, Email)
SELECT 'admin@sentribee.ai',
  admin.Pwd,
  'Administrator',
  COALESCE(admin.DisplayName, 'SentriBee Admin'),
  'admin@sentribee.ai'
FROM bee_Admin AS admin
WHERE admin.LoginID = 'admin'
ON DUPLICATE KEY UPDATE
  Pwd = VALUES(Pwd),
  Roles = VALUES(Roles),
  DisplayName = VALUES(DisplayName),
  Email = VALUES(Email),
  UpdatedAtUtc = UTC_TIMESTAMP(6);

INSERT INTO bee_Project
  (AdminId, ProjectName, ProjectDescription, LogoUrl, CompanyName, WebsiteUrl, Visibility, TimeZoneId)
SELECT admin.id,
  'SpendBee',
  'SpendBee consumer app for receipt recognition, restaurant photo sharing, same-table collaboration, split bills, and user spending behavior analytics.',
  NULL,
  'SpendBee',
  NULL,
  'Private',
  'Pacific/Auckland'
FROM bee_Admin AS admin
WHERE admin.Email = 'admin@sentribee.ai'
  AND NOT EXISTS (
    SELECT 1
    FROM bee_Project AS existing
    WHERE existing.ProjectName = 'SpendBee'
  );

UPDATE bee_Project AS project
INNER JOIN bee_Admin AS admin ON admin.Email = 'admin@sentribee.ai'
SET project.AdminId = admin.id
WHERE project.ProjectName = 'SpendBee';

INSERT INTO bee_ProjectMember (ProjectId, AdminId, Role)
SELECT project.id, admin.id, 'Administrator'
FROM bee_Project AS project
INNER JOIN bee_Admin AS admin ON admin.Email = 'admin@sentribee.ai'
ON DUPLICATE KEY UPDATE Role = VALUES(Role);
