CREATE TABLE IF NOT EXISTS bee_AppUserRiskNotificationPreference (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  AppUserId INT NOT NULL,
  EdgeDeviceId INT NOT NULL,
  RiskSeverity VARCHAR(40) NOT NULL,
  PushEnabled TINYINT(1) NOT NULL DEFAULT 0,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_AppRiskPref_User_Device_Severity (AppUserId, EdgeDeviceId, RiskSeverity),
  KEY IX_bee_AppRiskPref_Project_Device (ProjectId, EdgeDeviceId),
  CONSTRAINT FK_bee_AppRiskPref_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AppRiskPref_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AppRiskPref_Device FOREIGN KEY (EdgeDeviceId)
    REFERENCES bee_EdgeDevice (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_AppRiskNotification (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  AppUserId INT NOT NULL,
  EdgeDeviceId INT NOT NULL,
  EdgeEventId INT NOT NULL,
  RiskSeverity VARCHAR(40) NOT NULL,
  Title VARCHAR(200) NOT NULL,
  Message VARCHAR(500) NULL,
  IsRead TINYINT(1) NOT NULL DEFAULT 0,
  PushStatus VARCHAR(40) NOT NULL DEFAULT 'Suppressed',
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  ReadAtUtc DATETIME(6) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_AppRiskNotification_User_Event (AppUserId, EdgeEventId),
  KEY IX_bee_AppRiskNotification_User_Read_Time (AppUserId, IsRead, CreatedAtUtc),
  KEY IX_bee_AppRiskNotification_Project_Device_Time (ProjectId, EdgeDeviceId, CreatedAtUtc),
  CONSTRAINT FK_bee_AppRiskNotification_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AppRiskNotification_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AppRiskNotification_Device FOREIGN KEY (EdgeDeviceId)
    REFERENCES bee_EdgeDevice (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AppRiskNotification_Event FOREIGN KEY (EdgeEventId)
    REFERENCES bee_EdgeEvent (id) ON DELETE CASCADE
) ENGINE=InnoDB;
