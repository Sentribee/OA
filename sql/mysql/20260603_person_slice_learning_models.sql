SET @project_person_ppe_yaml_exists := (
  SELECT COUNT(*)
  FROM INFORMATION_SCHEMA.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_Project'
    AND COLUMN_NAME = 'PersonPpeModelYamlPath'
);

SET @project_person_ppe_yaml_sql := IF(
  @project_person_ppe_yaml_exists = 0,
  'ALTER TABLE bee_Project ADD COLUMN PersonPpeModelYamlPath VARCHAR(500) NOT NULL DEFAULT ''/home/ubuntu/sentribee/hobson/person_crops_ppe/data.yaml'' AFTER AiModelYamlPath',
  'SELECT 1'
);
PREPARE project_person_ppe_yaml_stmt FROM @project_person_ppe_yaml_sql;
EXECUTE project_person_ppe_yaml_stmt;
DEALLOCATE PREPARE project_person_ppe_yaml_stmt;

UPDATE bee_Project
SET PersonPpeModelYamlPath = '/home/ubuntu/sentribee/hobson/person_crops_ppe/data.yaml'
WHERE PersonPpeModelYamlPath IS NULL OR PersonPpeModelYamlPath = '';

SET @subject_learning_status_exists := (
  SELECT COUNT(*)
  FROM INFORMATION_SCHEMA.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_EdgeEventSubject'
    AND COLUMN_NAME = 'LearningStatus'
);

SET @subject_learning_status_sql := IF(
  @subject_learning_status_exists = 0,
  'ALTER TABLE bee_EdgeEventSubject ADD COLUMN LearningStatus VARCHAR(80) NOT NULL DEFAULT ''None'' AFTER PpeStatusJson',
  'SELECT 1'
);
PREPARE subject_learning_status_stmt FROM @subject_learning_status_sql;
EXECUTE subject_learning_status_stmt;
DEALLOCATE PREPARE subject_learning_status_stmt;

SET @subject_learning_index_exists := (
  SELECT COUNT(*)
  FROM INFORMATION_SCHEMA.STATISTICS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_EdgeEventSubject'
    AND INDEX_NAME = 'IX_bee_EdgeEventSubject_Learning'
);

SET @subject_learning_index_sql := IF(
  @subject_learning_index_exists = 0,
  'ALTER TABLE bee_EdgeEventSubject ADD INDEX IX_bee_EdgeEventSubject_Learning (SubjectType, LearningStatus, UpdatedAtUtc)',
  'SELECT 1'
);
PREPARE subject_learning_index_stmt FROM @subject_learning_index_sql;
EXECUTE subject_learning_index_stmt;
DEALLOCATE PREPARE subject_learning_index_stmt;
