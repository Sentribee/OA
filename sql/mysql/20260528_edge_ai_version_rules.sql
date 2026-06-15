SET @missing := (
  SELECT COUNT(*) = 0
  FROM INFORMATION_SCHEMA.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_ProjectRule'
    AND COLUMN_NAME = 'EdgeAiCodeVersionId'
);
SET @stmt := IF(@missing,
  'ALTER TABLE bee_ProjectRule ADD COLUMN EdgeAiCodeVersionId INT NULL AFTER ProjectId',
  'SELECT 1');
PREPARE edge_ai_rule_version_stmt FROM @stmt;
EXECUTE edge_ai_rule_version_stmt;
DEALLOCATE PREPARE edge_ai_rule_version_stmt;

SET @missing := (
  SELECT COUNT(*) = 0
  FROM INFORMATION_SCHEMA.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_ProjectRule'
    AND COLUMN_NAME = 'ChangeType'
);
SET @stmt := IF(@missing,
  'ALTER TABLE bee_ProjectRule ADD COLUMN ChangeType VARCHAR(20) NOT NULL DEFAULT ''Active'' AFTER EdgeAiCodeVersionId',
  'SELECT 1');
PREPARE edge_ai_rule_change_stmt FROM @stmt;
EXECUTE edge_ai_rule_change_stmt;
DEALLOCATE PREPARE edge_ai_rule_change_stmt;

SET @missing := (
  SELECT COUNT(*) = 0
  FROM INFORMATION_SCHEMA.STATISTICS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_ProjectRule'
    AND INDEX_NAME = 'IX_bee_ProjectRule_CodeVersion'
);
SET @stmt := IF(@missing,
  'ALTER TABLE bee_ProjectRule ADD INDEX IX_bee_ProjectRule_CodeVersion (EdgeAiCodeVersionId)',
  'SELECT 1');
PREPARE edge_ai_rule_index_stmt FROM @stmt;
EXECUTE edge_ai_rule_index_stmt;
DEALLOCATE PREPARE edge_ai_rule_index_stmt;

UPDATE bee_ProjectRule
SET ChangeType = 'Active'
WHERE ChangeType IS NULL OR ChangeType = '';

CREATE TABLE IF NOT EXISTS bee_EdgeAiGitHandoff (
  id INT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  EdgeAiCodeVersionId INT NOT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_EdgeAiGitHandoff_Project_Date (ProjectId, CreatedAtUtc),
  KEY IX_bee_EdgeAiGitHandoff_CodeVersion (EdgeAiCodeVersionId),
  CONSTRAINT FK_bee_EdgeAiGitHandoff_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_EdgeAiGitHandoff_CodeVersion FOREIGN KEY (EdgeAiCodeVersionId)
    REFERENCES bee_EdgeAiCodeVersion (id) ON DELETE CASCADE
) ENGINE=InnoDB;
