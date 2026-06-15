SET @profile_completed_col := (
  SELECT COUNT(*)
  FROM INFORMATION_SCHEMA.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND COLUMN_NAME = 'ProfileCompletedAtUtc'
);
SET @profile_completed_sql := IF(
  @profile_completed_col = 0,
  'ALTER TABLE bee_CrmEmployee ADD COLUMN ProfileCompletedAtUtc DATETIME(6) NULL AFTER LastEmployeeLoginAtUtc',
  'SELECT 1'
);
PREPARE stmt FROM @profile_completed_sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @invite_sent_col := (
  SELECT COUNT(*)
  FROM INFORMATION_SCHEMA.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND COLUMN_NAME = 'InviteSentAtUtc'
);
SET @invite_sent_sql := IF(
  @invite_sent_col = 0,
  'ALTER TABLE bee_CrmEmployee ADD COLUMN InviteSentAtUtc DATETIME(6) NULL AFTER ProfileCompletedAtUtc',
  'SELECT 1'
);
PREPARE stmt FROM @invite_sent_sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

UPDATE bee_CrmEmployee
SET ProfileCompletedAtUtc = COALESCE(ProfileCompletedAtUtc, UpdatedAtUtc, CreatedAtUtc, UTC_TIMESTAMP(6))
WHERE ProfileCompletedAtUtc IS NULL
  AND (
    COALESCE(NULLIF(TRIM(RealName), ''), '') <> ''
    OR COALESCE(NULLIF(TRIM(Phone), ''), '') <> ''
    OR COALESCE(NULLIF(TRIM(PrivateEmail), ''), '') <> ''
    OR COALESCE(NULLIF(TRIM(ResidentialAddress), ''), '') <> ''
    OR COALESCE(NULLIF(TRIM(GstNumber), ''), '') <> ''
    OR COALESCE(NULLIF(TRIM(BankAccountNumber), ''), '') <> ''
  );
