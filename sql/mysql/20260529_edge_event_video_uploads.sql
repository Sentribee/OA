CREATE TABLE IF NOT EXISTS bee_EdgeEventVideo (
  id INT NOT NULL AUTO_INCREMENT,
  EdgeEventId INT NOT NULL,
  S3Key VARCHAR(700) NOT NULL,
  VideoUrl VARCHAR(1000) NULL,
  UploadId VARCHAR(700) NOT NULL,
  FileName VARCHAR(255) NULL,
  ContentType VARCHAR(100) NOT NULL DEFAULT 'video/mp4',
  FileSizeBytes BIGINT NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Uploading',
  PartEtagsJson JSON NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  CompletedAtUtc DATETIME(6) NULL,
  PRIMARY KEY (id),
  KEY IX_bee_EdgeEventVideo_Event_Status (EdgeEventId, Status),
  CONSTRAINT FK_bee_EdgeEventVideo_EdgeEvent FOREIGN KEY (EdgeEventId)
    REFERENCES bee_EdgeEvent (id) ON DELETE CASCADE
) ENGINE=InnoDB;
