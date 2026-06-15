CREATE TABLE IF NOT EXISTS bee_CrmEmployeeProfileChangeRequest (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  EmployeeId BIGINT NOT NULL,
  RequestedByEmployeeId BIGINT NULL,
  RequestedByMerchantId BIGINT NULL,
  CurrentProfileJson LONGTEXT NULL,
  RequestedProfileJson LONGTEXT NOT NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Pending',
  DecisionByMerchantId BIGINT NULL,
  DecisionByEmployeeId BIGINT NULL,
  DecisionAtUtc DATETIME(6) NULL,
  DecisionNote VARCHAR(1000) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_CrmEmployeeProfileRequest_Merchant_Status (MerchantId, Status, CreatedAtUtc),
  KEY IX_bee_CrmEmployeeProfileRequest_Employee (EmployeeId, CreatedAtUtc),
  KEY IX_bee_CrmEmployeeProfileRequest_Project (ProjectId),
  CONSTRAINT FK_bee_CrmEmployeeProfileRequest_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmEmployeeProfileRequest_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmEmployeeProfileRequest_Employee FOREIGN KEY (EmployeeId)
    REFERENCES bee_CrmEmployee (id) ON DELETE CASCADE
) ENGINE=InnoDB;
