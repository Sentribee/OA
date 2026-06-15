SET @has_role_id := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND COLUMN_NAME = 'RoleId'
);
SET @sql := IF(@has_role_id = 0,
  'ALTER TABLE bee_CrmEmployee ADD COLUMN RoleId BIGINT NULL AFTER OfficeAddressId',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_employee_password := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND COLUMN_NAME = 'EmployeePasswordHash'
);
SET @sql := IF(@has_employee_password = 0,
  'ALTER TABLE bee_CrmEmployee ADD COLUMN EmployeePasswordHash VARCHAR(512) NULL AFTER PrivateEmail',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_must_change := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND COLUMN_NAME = 'MustChangePassword'
);
SET @sql := IF(@has_must_change = 0,
  'ALTER TABLE bee_CrmEmployee ADD COLUMN MustChangePassword TINYINT(1) NOT NULL DEFAULT 0 AFTER EmployeePasswordHash',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_login_enabled := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND COLUMN_NAME = 'LoginEnabled'
);
SET @sql := IF(@has_login_enabled = 0,
  'ALTER TABLE bee_CrmEmployee ADD COLUMN LoginEnabled TINYINT(1) NOT NULL DEFAULT 0 AFTER MustChangePassword',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_password_updated := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND COLUMN_NAME = 'PasswordUpdatedAtUtc'
);
SET @sql := IF(@has_password_updated = 0,
  'ALTER TABLE bee_CrmEmployee ADD COLUMN PasswordUpdatedAtUtc DATETIME(6) NULL AFTER LoginEnabled',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_employee_login_at := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND COLUMN_NAME = 'LastEmployeeLoginAtUtc'
);
SET @sql := IF(@has_employee_login_at = 0,
  'ALTER TABLE bee_CrmEmployee ADD COLUMN LastEmployeeLoginAtUtc DATETIME(6) NULL AFTER PasswordUpdatedAtUtc',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_work_email_index := (
  SELECT COUNT(*)
  FROM information_schema.STATISTICS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND INDEX_NAME = 'IX_bee_CrmEmployee_Merchant_WorkEmail'
);
SET @sql := IF(@has_work_email_index = 0,
  'ALTER TABLE bee_CrmEmployee ADD KEY IX_bee_CrmEmployee_Merchant_WorkEmail (MerchantId, WorkEmail)',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

CREATE TABLE IF NOT EXISTS bee_CrmRole (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  RoleName VARCHAR(120) NOT NULL,
  Description VARCHAR(500) NULL,
  CanApproveLeave TINYINT(1) NOT NULL DEFAULT 0,
  CanManageAttendance TINYINT(1) NOT NULL DEFAULT 0,
  CanManageEmployees TINYINT(1) NOT NULL DEFAULT 0,
  Status VARCHAR(40) NOT NULL DEFAULT 'Active',
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_CrmRole_Merchant_Status (MerchantId, Status, RoleName),
  KEY IX_bee_CrmRole_Project (ProjectId),
  CONSTRAINT FK_bee_CrmRole_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmRole_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_CrmWorkflowDefinition (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  WorkflowKey VARCHAR(80) NOT NULL,
  WorkflowName VARCHAR(160) NOT NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Active',
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_CrmWorkflowDefinition_Merchant_Key (MerchantId, WorkflowKey),
  KEY IX_bee_CrmWorkflowDefinition_Project (ProjectId),
  CONSTRAINT FK_bee_CrmWorkflowDefinition_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmWorkflowDefinition_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_CrmWorkflowStep (
  id BIGINT NOT NULL AUTO_INCREMENT,
  WorkflowDefinitionId BIGINT NOT NULL,
  MerchantId BIGINT NOT NULL,
  StepOrder INT NOT NULL,
  StepName VARCHAR(160) NOT NULL,
  ApproverRoleId BIGINT NULL,
  ApproverEmployeeId BIGINT NULL,
  IsFinalApproval TINYINT(1) NOT NULL DEFAULT 0,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_CrmWorkflowStep_Workflow_Order (WorkflowDefinitionId, StepOrder),
  KEY IX_bee_CrmWorkflowStep_Merchant (MerchantId),
  CONSTRAINT FK_bee_CrmWorkflowStep_Workflow FOREIGN KEY (WorkflowDefinitionId)
    REFERENCES bee_CrmWorkflowDefinition (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmWorkflowStep_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmWorkflowStep_Role FOREIGN KEY (ApproverRoleId)
    REFERENCES bee_CrmRole (id) ON DELETE SET NULL,
  CONSTRAINT FK_bee_CrmWorkflowStep_Employee FOREIGN KEY (ApproverEmployeeId)
    REFERENCES bee_CrmEmployee (id) ON DELETE SET NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_CrmWorkflowRequest (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  WorkflowDefinitionId BIGINT NOT NULL,
  EntityType VARCHAR(80) NOT NULL,
  EntityId BIGINT NOT NULL,
  RequestedByEmployeeId BIGINT NULL,
  CurrentStepId BIGINT NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Pending',
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  CompletedAtUtc DATETIME(6) NULL,
  PRIMARY KEY (id),
  KEY IX_bee_CrmWorkflowRequest_Merchant_Status (MerchantId, Status, CreatedAtUtc),
  KEY IX_bee_CrmWorkflowRequest_Entity (EntityType, EntityId),
  CONSTRAINT FK_bee_CrmWorkflowRequest_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmWorkflowRequest_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmWorkflowRequest_Workflow FOREIGN KEY (WorkflowDefinitionId)
    REFERENCES bee_CrmWorkflowDefinition (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmWorkflowRequest_Requester FOREIGN KEY (RequestedByEmployeeId)
    REFERENCES bee_CrmEmployee (id) ON DELETE SET NULL,
  CONSTRAINT FK_bee_CrmWorkflowRequest_CurrentStep FOREIGN KEY (CurrentStepId)
    REFERENCES bee_CrmWorkflowStep (id) ON DELETE SET NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_CrmWorkflowApproval (
  id BIGINT NOT NULL AUTO_INCREMENT,
  WorkflowRequestId BIGINT NOT NULL,
  MerchantId BIGINT NOT NULL,
  StepId BIGINT NOT NULL,
  ApproverRoleId BIGINT NULL,
  ApproverEmployeeId BIGINT NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Pending',
  DecisionByEmployeeId BIGINT NULL,
  DecisionAtUtc DATETIME(6) NULL,
  DecisionNote VARCHAR(1000) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_CrmWorkflowApproval_Merchant_Status (MerchantId, Status, CreatedAtUtc),
  KEY IX_bee_CrmWorkflowApproval_Request (WorkflowRequestId),
  CONSTRAINT FK_bee_CrmWorkflowApproval_Request FOREIGN KEY (WorkflowRequestId)
    REFERENCES bee_CrmWorkflowRequest (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmWorkflowApproval_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmWorkflowApproval_Step FOREIGN KEY (StepId)
    REFERENCES bee_CrmWorkflowStep (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmWorkflowApproval_Role FOREIGN KEY (ApproverRoleId)
    REFERENCES bee_CrmRole (id) ON DELETE SET NULL,
  CONSTRAINT FK_bee_CrmWorkflowApproval_Approver FOREIGN KEY (ApproverEmployeeId)
    REFERENCES bee_CrmEmployee (id) ON DELETE SET NULL,
  CONSTRAINT FK_bee_CrmWorkflowApproval_DecisionBy FOREIGN KEY (DecisionByEmployeeId)
    REFERENCES bee_CrmEmployee (id) ON DELETE SET NULL
) ENGINE=InnoDB;
