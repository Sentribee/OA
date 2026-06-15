ALTER TABLE bee_Project
  ADD COLUMN ApiKeyHash VARCHAR(128) NULL AFTER EdgeAiGitWorkingDirectory,
  ADD COLUMN ApiKeyPrefix VARCHAR(32) NULL AFTER ApiKeyHash,
  ADD COLUMN ApiKeyCreatedAtUtc DATETIME(6) NULL AFTER ApiKeyPrefix,
  ADD UNIQUE KEY UX_bee_Project_ApiKeyHash (ApiKeyHash);

ALTER TABLE bee_EdgeEvent
  ADD COLUMN RawPayloadJson JSON NULL AFTER YoloLabelUrl;

CREATE TABLE IF NOT EXISTS bee_ProjectApiClientSession (
  id INT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  TokenHash VARCHAR(128) NOT NULL,
  ClientName VARCHAR(150) NULL,
  ExpiresAtUtc DATETIME(6) NOT NULL,
  RevokedAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_ProjectApiClientSession_TokenHash (TokenHash),
  KEY IX_bee_ProjectApiClientSession_Project_Expiry (ProjectId, ExpiresAtUtc),
  CONSTRAINT FK_bee_ProjectApiClientSession_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_EdgeAiHeartbeat (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  EdgeDeviceId INT NOT NULL,
  RuntimeStatus VARCHAR(80) NOT NULL,
  DeviceStatus VARCHAR(80) NOT NULL,
  DetailJson JSON NULL,
  ReportedAtUtc DATETIME(6) NOT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_EdgeAiHeartbeat_Project_Device_Time (ProjectId, EdgeDeviceId, ReportedAtUtc),
  CONSTRAINT FK_bee_EdgeAiHeartbeat_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_EdgeAiHeartbeat_Device FOREIGN KEY (EdgeDeviceId)
    REFERENCES bee_EdgeDevice (id) ON DELETE CASCADE
) ENGINE=InnoDB;
