CREATE TABLE IF NOT EXISTS bee_SpendBeeUserMessage (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  AppUserId INT NOT NULL,
  MessageType VARCHAR(80) NOT NULL,
  Severity VARCHAR(30) NOT NULL DEFAULT 'Info',
  Title VARCHAR(200) NOT NULL,
  Body VARCHAR(1000) NULL,
  TargetType VARCHAR(80) NULL,
  TargetId BIGINT NULL,
  TargetUrl VARCHAR(500) NULL,
  PayloadJson JSON NULL,
  ReadAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  ExpiresAtUtc DATETIME(6) NULL,
  PRIMARY KEY (id),
  KEY IX_bee_SpendBeeUserMessage_User_Time (AppUserId, CreatedAtUtc),
  KEY IX_bee_SpendBeeUserMessage_User_Read_Time (AppUserId, ReadAtUtc, CreatedAtUtc),
  KEY IX_bee_SpendBeeUserMessage_Project_Type_Time (ProjectId, MessageType, CreatedAtUtc),
  CONSTRAINT FK_bee_SpendBeeUserMessage_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeUserMessage_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE
) ENGINE=InnoDB;
