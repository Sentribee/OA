CREATE TABLE IF NOT EXISTS bee_SpendBeeReceiptUpload (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  AppUserId INT NOT NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Uploading',
  Timezone VARCHAR(80) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  CompletedAtUtc DATETIME(6) NULL,
  CancelledAtUtc DATETIME(6) NULL,
  PRIMARY KEY (id),
  KEY IX_bee_SpendBeeReceiptUpload_User_Time (AppUserId, CreatedAtUtc),
  KEY IX_bee_SpendBeeReceiptUpload_Project_Status (ProjectId, Status),
  CONSTRAINT FK_bee_SpendBeeReceiptUpload_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeReceiptUpload_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeReceiptUploadImage (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ReceiptUploadId BIGINT NOT NULL,
  S3Key VARCHAR(700) NOT NULL,
  UploadId VARCHAR(700) NOT NULL,
  FileName VARCHAR(255) NULL,
  ContentType VARCHAR(80) NOT NULL,
  FileSizeBytes BIGINT NULL,
  SortOrder INT NOT NULL DEFAULT 0,
  Status VARCHAR(40) NOT NULL DEFAULT 'Uploading',
  ImageUrl VARCHAR(800) NULL,
  PartEtagsJson JSON NOT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  CompletedAtUtc DATETIME(6) NULL,
  PRIMARY KEY (id),
  KEY IX_bee_SpendBeeReceiptUploadImage_Upload (ReceiptUploadId, SortOrder),
  CONSTRAINT FK_bee_SpendBeeReceiptUploadImage_Upload FOREIGN KEY (ReceiptUploadId)
    REFERENCES bee_SpendBeeReceiptUpload (id) ON DELETE CASCADE
) ENGINE=InnoDB;
