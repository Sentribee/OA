SET @has_pay_type := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND COLUMN_NAME = 'PayType'
);
SET @sql := IF(@has_pay_type = 0,
  'ALTER TABLE bee_CrmEmployee ADD COLUMN PayType VARCHAR(40) NOT NULL DEFAULT ''Hourly'' AFTER EmploymentType',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_hourly_rate := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND COLUMN_NAME = 'HourlyRate'
);
SET @sql := IF(@has_hourly_rate = 0,
  'ALTER TABLE bee_CrmEmployee ADD COLUMN HourlyRate DECIMAL(12,2) NULL AFTER PayType',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_annual_salary := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND COLUMN_NAME = 'AnnualSalary'
);
SET @sql := IF(@has_annual_salary = 0,
  'ALTER TABLE bee_CrmEmployee ADD COLUMN AnnualSalary DECIMAL(12,2) NULL AFTER HourlyRate',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_standard_weekly_hours := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND COLUMN_NAME = 'StandardWeeklyHours'
);
SET @sql := IF(@has_standard_weekly_hours = 0,
  'ALTER TABLE bee_CrmEmployee ADD COLUMN StandardWeeklyHours DECIMAL(6,2) NOT NULL DEFAULT 40.00 AFTER AnnualSalary',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

CREATE TABLE IF NOT EXISTS bee_CrmEmployeeLeave (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  EmployeeId BIGINT NOT NULL,
  LeaveType VARCHAR(60) NOT NULL,
  StartDate DATE NOT NULL,
  EndDate DATE NOT NULL,
  Hours DECIMAL(8,2) NOT NULL,
  IsPaid TINYINT(1) NOT NULL DEFAULT 1,
  Status VARCHAR(40) NOT NULL DEFAULT 'Approved',
  Reason VARCHAR(500) NULL,
  Notes TEXT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_CrmEmployeeLeave_Merchant_Date (MerchantId, StartDate, EndDate, Status),
  KEY IX_bee_CrmEmployeeLeave_Employee_Date (EmployeeId, StartDate, EndDate),
  KEY IX_bee_CrmEmployeeLeave_Project (ProjectId),
  CONSTRAINT FK_bee_CrmEmployeeLeave_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmEmployeeLeave_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmEmployeeLeave_Employee FOREIGN KEY (EmployeeId)
    REFERENCES bee_CrmEmployee (id) ON DELETE CASCADE
) ENGINE=InnoDB;
