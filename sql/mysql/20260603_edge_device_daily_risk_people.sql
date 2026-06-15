CREATE TABLE IF NOT EXISTS bee_EdgeDeviceDailyRiskPerson (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  EdgeDeviceId INT NOT NULL,
  StatDate DATE NOT NULL,
  PersonGroupKey VARCHAR(120) NOT NULL,
  DisplayLabel VARCHAR(150) NULL,
  RepresentativeSubjectId BIGINT NULL,
  RepresentativeCropImageUrl VARCHAR(1000) NULL,
  RepresentativePreviewImageUrl VARCHAR(1000) NULL,
  RiskEventCount INT NOT NULL DEFAULT 0,
  RiskSubjectCount INT NOT NULL DEFAULT 0,
  SimilarityHash VARCHAR(32) NULL,
  SubjectIdsJson JSON NULL,
  EventIdsJson JSON NULL,
  FirstEventAtUtc DATETIME(6) NULL,
  LastEventAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_EdgeDeviceDailyRiskPerson_Device_Date_Group (EdgeDeviceId, StatDate, PersonGroupKey),
  KEY IX_bee_EdgeDeviceDailyRiskPerson_Project_Date (ProjectId, StatDate),
  KEY IX_bee_EdgeDeviceDailyRiskPerson_Device_Date_Rank (EdgeDeviceId, StatDate, RiskEventCount, RiskSubjectCount),
  CONSTRAINT FK_bee_EdgeDeviceDailyRiskPerson_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_EdgeDeviceDailyRiskPerson_Device FOREIGN KEY (EdgeDeviceId)
    REFERENCES bee_EdgeDevice (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_EdgeDeviceDailyRiskPerson_Subject FOREIGN KEY (RepresentativeSubjectId)
    REFERENCES bee_EdgeEventSubject (id) ON DELETE SET NULL
) ENGINE=InnoDB;
