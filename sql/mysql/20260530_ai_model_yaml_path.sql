SET @column_exists := (
  SELECT COUNT(*)
  FROM INFORMATION_SCHEMA.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_Project'
    AND COLUMN_NAME = 'AiModelYamlPath'
);

SET @ddl := IF(
  @column_exists = 0,
  'ALTER TABLE bee_Project ADD COLUMN AiModelYamlPath VARCHAR(500) NOT NULL DEFAULT ''/sentribee/hobson/data.yaml'' AFTER EdgeAiGitWorkingDirectory',
  'SELECT 1'
);

PREPARE stmt FROM @ddl;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

UPDATE bee_Project
SET AiModelYamlPath = '/sentribee/hobson/data.yaml'
WHERE AiModelYamlPath IS NULL OR AiModelYamlPath = '';
