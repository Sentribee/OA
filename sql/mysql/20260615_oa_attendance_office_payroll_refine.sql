SET @has_google_place_id := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmOfficeAddress'
    AND COLUMN_NAME = 'GooglePlaceId'
);
SET @sql := IF(@has_google_place_id = 0,
  'ALTER TABLE bee_CrmOfficeAddress ADD COLUMN GooglePlaceId VARCHAR(160) NULL AFTER LocationName',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_formatted_address := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmOfficeAddress'
    AND COLUMN_NAME = 'FormattedAddress'
);
SET @sql := IF(@has_formatted_address = 0,
  'ALTER TABLE bee_CrmOfficeAddress ADD COLUMN FormattedAddress VARCHAR(700) NULL AFTER AddressLine1',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_latitude := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmOfficeAddress'
    AND COLUMN_NAME = 'Latitude'
);
SET @sql := IF(@has_latitude = 0,
  'ALTER TABLE bee_CrmOfficeAddress ADD COLUMN Latitude DECIMAL(10,7) NULL AFTER Country',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_longitude := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmOfficeAddress'
    AND COLUMN_NAME = 'Longitude'
);
SET @sql := IF(@has_longitude = 0,
  'ALTER TABLE bee_CrmOfficeAddress ADD COLUMN Longitude DECIMAL(10,7) NULL AFTER Latitude',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_clock_in_local_time := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployeeAttendance'
    AND COLUMN_NAME = 'ClockInLocalTime'
);
SET @sql := IF(@has_clock_in_local_time = 0,
  'ALTER TABLE bee_CrmEmployeeAttendance ADD COLUMN ClockInLocalTime TIME NULL AFTER ClockInAtUtc',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_clock_out_local_time := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployeeAttendance'
    AND COLUMN_NAME = 'ClockOutLocalTime'
);
SET @sql := IF(@has_clock_out_local_time = 0,
  'ALTER TABLE bee_CrmEmployeeAttendance ADD COLUMN ClockOutLocalTime TIME NULL AFTER ClockOutAtUtc',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_grace_minutes := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployeeAttendance'
    AND COLUMN_NAME = 'GraceMinutes'
);
SET @sql := IF(@has_grace_minutes = 0,
  'ALTER TABLE bee_CrmEmployeeAttendance ADD COLUMN GraceMinutes INT NOT NULL DEFAULT 10 AFTER ClockOutIp',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_is_complete_day := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployeeAttendance'
    AND COLUMN_NAME = 'IsCompleteDay'
);
SET @sql := IF(@has_is_complete_day = 0,
  'ALTER TABLE bee_CrmEmployeeAttendance ADD COLUMN IsCompleteDay TINYINT(1) NOT NULL DEFAULT 0 AFTER GraceMinutes',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_is_late_beyond_grace := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployeeAttendance'
    AND COLUMN_NAME = 'IsLateBeyondGrace'
);
SET @sql := IF(@has_is_late_beyond_grace = 0,
  'ALTER TABLE bee_CrmEmployeeAttendance ADD COLUMN IsLateBeyondGrace TINYINT(1) NOT NULL DEFAULT 0 AFTER IsCompleteDay',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_work_log_summary := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployeeAttendance'
    AND COLUMN_NAME = 'WorkLogSummary'
);
SET @sql := IF(@has_work_log_summary = 0,
  'ALTER TABLE bee_CrmEmployeeAttendance ADD COLUMN WorkLogSummary TEXT NULL AFTER ClockOutNote',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_workload_level := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployeeAttendance'
    AND COLUMN_NAME = 'WorkloadLevel'
);
SET @sql := IF(@has_workload_level = 0,
  'ALTER TABLE bee_CrmEmployeeAttendance ADD COLUMN WorkloadLevel VARCHAR(40) NULL AFTER WorkLogSummary',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_workload_reason := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployeeAttendance'
    AND COLUMN_NAME = 'WorkloadReason'
);
SET @sql := IF(@has_workload_reason = 0,
  'ALTER TABLE bee_CrmEmployeeAttendance ADD COLUMN WorkloadReason VARCHAR(700) NULL AFTER WorkloadLevel',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_scheduled_start_time := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND COLUMN_NAME = 'ScheduledStartTime'
);
SET @sql := IF(@has_scheduled_start_time = 0,
  'ALTER TABLE bee_CrmEmployee ADD COLUMN ScheduledStartTime TIME NULL AFTER StandardWeeklyHours',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_scheduled_end_time := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND COLUMN_NAME = 'ScheduledEndTime'
);
SET @sql := IF(@has_scheduled_end_time = 0,
  'ALTER TABLE bee_CrmEmployee ADD COLUMN ScheduledEndTime TIME NULL AFTER ScheduledStartTime',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

CREATE TABLE IF NOT EXISTS bee_CrmPayrollAdjustment (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  EmployeeId BIGINT NOT NULL,
  PeriodYear INT NOT NULL,
  PeriodMonth INT NOT NULL,
  DeductionHours DECIMAL(8,2) NOT NULL DEFAULT 0.00,
  Notes VARCHAR(1000) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_CrmPayrollAdjustment_Employee_Period (EmployeeId, PeriodYear, PeriodMonth),
  KEY IX_bee_CrmPayrollAdjustment_Merchant_Period (MerchantId, PeriodYear, PeriodMonth),
  CONSTRAINT FK_bee_CrmPayrollAdjustment_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmPayrollAdjustment_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmPayrollAdjustment_Employee FOREIGN KEY (EmployeeId)
    REFERENCES bee_CrmEmployee (id) ON DELETE CASCADE
) ENGINE=InnoDB;
