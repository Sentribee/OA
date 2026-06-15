CREATE TABLE IF NOT EXISTS bee_EdgeDeviceDailyStat (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  EdgeDeviceId INT NOT NULL,
  StatDate DATE NOT NULL,
  PeopleCount INT NOT NULL DEFAULT 0,
  BraceletCount INT NOT NULL DEFAULT 0,
  MachineryVehicleCount INT NOT NULL DEFAULT 0,
  PpeComplianceRate DECIMAL(5,2) NULL,
  RiskEventCount INT NOT NULL DEFAULT 0,
  RiskPersonCount INT NOT NULL DEFAULT 0,
  TopRiskSubjectKey VARCHAR(120) NULL,
  TopRiskSubjectRiskCount INT NOT NULL DEFAULT 0,
  LastHeartbeatAtUtc DATETIME(6) NULL,
  LastEventAtUtc DATETIME(6) NULL,
  DetailJson JSON NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_EdgeDeviceDailyStat_Device_Date (EdgeDeviceId, StatDate),
  KEY IX_bee_EdgeDeviceDailyStat_Project_Date (ProjectId, StatDate),
  CONSTRAINT FK_bee_EdgeDeviceDailyStat_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_EdgeDeviceDailyStat_Device FOREIGN KEY (EdgeDeviceId)
    REFERENCES bee_EdgeDevice (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_EdgeEventAnalysis (
  EdgeEventId INT NOT NULL,
  PeopleCount INT NOT NULL DEFAULT 0,
  MachineryVehicleCount INT NOT NULL DEFAULT 0,
  ToolCount INT NOT NULL DEFAULT 0,
  PpeCompliantPeopleCount INT NOT NULL DEFAULT 0,
  RiskPersonCount INT NOT NULL DEFAULT 0,
  PpeComplianceRate DECIMAL(5,2) NULL,
  RiskCategory VARCHAR(120) NULL,
  RiskSeverity VARCHAR(40) NOT NULL DEFAULT 'Review',
  Summary VARCHAR(500) NULL,
  AnalysisJson JSON NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (EdgeEventId),
  KEY IX_bee_EdgeEventAnalysis_Risk (RiskSeverity, RiskCategory),
  CONSTRAINT FK_bee_EdgeEventAnalysis_Event FOREIGN KEY (EdgeEventId)
    REFERENCES bee_EdgeEvent (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_EdgeEventSubject (
  id BIGINT NOT NULL AUTO_INCREMENT,
  EdgeEventId INT NOT NULL,
  SubjectKey VARCHAR(120) NOT NULL,
  SubjectType VARCHAR(40) NOT NULL DEFAULT 'Person',
  TrackingLabel VARCHAR(150) NULL,
  CropImageUrl VARCHAR(1000) NULL,
  PreviewImageUrl VARCHAR(1000) NULL,
  BoundingBoxJson JSON NULL,
  PpeBoxJson JSON NULL,
  PpeStatusJson JSON NULL,
  IsRisk TINYINT(1) NOT NULL DEFAULT 0,
  RiskCategory VARCHAR(120) NULL,
  RiskSeverity VARCHAR(40) NULL,
  RiskReason VARCHAR(500) NULL,
  AnalysisJson JSON NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_EdgeEventSubject_Event_Key (EdgeEventId, SubjectKey),
  KEY IX_bee_EdgeEventSubject_Risk (SubjectType, IsRisk, RiskSeverity),
  CONSTRAINT FK_bee_EdgeEventSubject_Event FOREIGN KEY (EdgeEventId)
    REFERENCES bee_EdgeEvent (id) ON DELETE CASCADE
) ENGINE=InnoDB;
