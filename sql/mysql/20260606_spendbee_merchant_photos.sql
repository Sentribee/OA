CREATE TABLE IF NOT EXISTS bee_SpendBeeMerchantPhotoUpload (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  AppUserId INT NOT NULL,
  S3Key VARCHAR(700) NOT NULL,
  UploadId VARCHAR(700) NOT NULL,
  FileName VARCHAR(255) NULL,
  ContentType VARCHAR(80) NOT NULL,
  FileSizeBytes BIGINT NULL,
  Category VARCHAR(80) NULL,
  Caption VARCHAR(500) NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Uploading',
  PartEtagsJson JSON NOT NULL,
  OriginalImageUrl VARCHAR(1000) NULL,
  PhotoId BIGINT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  CompletedAtUtc DATETIME(6) NULL,
  CancelledAtUtc DATETIME(6) NULL,
  PRIMARY KEY (id),
  KEY IX_bee_SpendBeeMerchantPhotoUpload_User_Status (AppUserId, Status, CreatedAtUtc),
  KEY IX_bee_SpendBeeMerchantPhotoUpload_Merchant_Time (MerchantId, CreatedAtUtc),
  CONSTRAINT FK_bee_SpendBeeMerchantPhotoUpload_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeMerchantPhotoUpload_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_SpendBeeMerchant (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeMerchantPhotoUpload_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeMerchantPhoto (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  AppUserId INT NOT NULL,
  UploadId BIGINT NULL,
  Category VARCHAR(80) NULL,
  Caption VARCHAR(500) NULL,
  OriginalImageUrl VARCHAR(1000) NOT NULL,
  OriginalContentType VARCHAR(80) NOT NULL,
  DisplayImageUrl VARCHAR(1000) NULL,
  DisplayContentType VARCHAR(80) NULL,
  OpenAIPrompt VARCHAR(1600) NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Processing',
  ProcessingError VARCHAR(700) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_SpendBeeMerchantPhoto_Merchant_Category_Time (MerchantId, Category, CreatedAtUtc),
  KEY IX_bee_SpendBeeMerchantPhoto_User_Time (AppUserId, CreatedAtUtc),
  CONSTRAINT FK_bee_SpendBeeMerchantPhoto_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeMerchantPhoto_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_SpendBeeMerchant (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeMerchantPhoto_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeMerchantPhoto_Upload FOREIGN KEY (UploadId)
    REFERENCES bee_SpendBeeMerchantPhotoUpload (id) ON DELETE SET NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeMerchantPhotoLike (
  PhotoId BIGINT NOT NULL,
  AppUserId INT NOT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (PhotoId, AppUserId),
  KEY IX_bee_SpendBeeMerchantPhotoLike_User (AppUserId, CreatedAtUtc),
  CONSTRAINT FK_bee_SpendBeeMerchantPhotoLike_Photo FOREIGN KEY (PhotoId)
    REFERENCES bee_SpendBeeMerchantPhoto (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeMerchantPhotoLike_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeMerchantPhotoComment (
  id BIGINT NOT NULL AUTO_INCREMENT,
  PhotoId BIGINT NOT NULL,
  AppUserId INT NOT NULL,
  ParentCommentId BIGINT NULL,
  Body VARCHAR(1000) NOT NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Visible',
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_SpendBeeMerchantPhotoComment_Photo_Time (PhotoId, CreatedAtUtc),
  KEY IX_bee_SpendBeeMerchantPhotoComment_Parent_Time (ParentCommentId, CreatedAtUtc),
  KEY IX_bee_SpendBeeMerchantPhotoComment_User_Time (AppUserId, CreatedAtUtc),
  CONSTRAINT FK_bee_SpendBeeMerchantPhotoComment_Photo FOREIGN KEY (PhotoId)
    REFERENCES bee_SpendBeeMerchantPhoto (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeMerchantPhotoComment_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeMerchantPhotoComment_Parent FOREIGN KEY (ParentCommentId)
    REFERENCES bee_SpendBeeMerchantPhotoComment (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeMerchantPhotoCommentLike (
  CommentId BIGINT NOT NULL,
  AppUserId INT NOT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (CommentId, AppUserId),
  KEY IX_bee_SpendBeeMerchantPhotoCommentLike_User (AppUserId, CreatedAtUtc),
  CONSTRAINT FK_bee_SpendBeeMerchantPhotoCommentLike_Comment FOREIGN KEY (CommentId)
    REFERENCES bee_SpendBeeMerchantPhotoComment (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeMerchantPhotoCommentLike_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE
) ENGINE=InnoDB;
