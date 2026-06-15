SET @has_employee_avatar := (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_CrmEmployee'
    AND COLUMN_NAME = 'AvatarUrl'
);
SET @sql := IF(@has_employee_avatar = 0,
  'ALTER TABLE bee_CrmEmployee ADD COLUMN AvatarUrl VARCHAR(800) NULL AFTER PreferredName',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
