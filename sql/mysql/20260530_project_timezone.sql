SET @has_timezone := (
  SELECT COUNT(*)
  FROM INFORMATION_SCHEMA.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_Project'
    AND COLUMN_NAME = 'TimeZoneId'
);

SET @timezone_sql := IF(
  @has_timezone = 0,
  'ALTER TABLE bee_Project ADD COLUMN TimeZoneId VARCHAR(80) NOT NULL DEFAULT ''Pacific/Auckland'' AFTER Visibility',
  'SELECT 1'
);

PREPARE timezone_stmt FROM @timezone_sql;
EXECUTE timezone_stmt;
DEALLOCATE PREPARE timezone_stmt;

UPDATE bee_Project
SET TimeZoneId = 'Pacific/Auckland'
WHERE TimeZoneId IS NULL OR TimeZoneId = '';
