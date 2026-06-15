SET @has_email_index := (
  SELECT COUNT(*)
  FROM INFORMATION_SCHEMA.STATISTICS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_Admin'
    AND INDEX_NAME = 'UX_bee_Admin_Email'
);
SET @sql := IF(@has_email_index = 0,
  'ALTER TABLE bee_Admin ADD UNIQUE KEY UX_bee_Admin_Email (Email)',
  'SELECT 1');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_project_admin_index := (
  SELECT COUNT(*)
  FROM INFORMATION_SCHEMA.STATISTICS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_Project'
    AND INDEX_NAME = 'UX_bee_Project_AdminId'
);
SET @has_project_admin_plain_index := (
  SELECT COUNT(*)
  FROM INFORMATION_SCHEMA.STATISTICS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_Project'
    AND INDEX_NAME = 'IX_bee_Project_AdminId'
);
SET @sql := IF(@has_project_admin_plain_index = 0,
  'ALTER TABLE bee_Project ADD INDEX IX_bee_Project_AdminId (AdminId)',
  'SELECT 1');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(@has_project_admin_index > 0,
  'ALTER TABLE bee_Project DROP INDEX UX_bee_Project_AdminId',
  'SELECT 1');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

CREATE TABLE IF NOT EXISTS bee_ProjectMember (
  ProjectId INT NOT NULL,
  AdminId INT NOT NULL,
  Role VARCHAR(40) NOT NULL DEFAULT 'Read Only',
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (ProjectId, AdminId),
  KEY IX_bee_ProjectMember_AdminId (AdminId),
  CONSTRAINT FK_bee_ProjectMember_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_ProjectMember_Admin FOREIGN KEY (AdminId)
    REFERENCES bee_Admin (id) ON DELETE CASCADE
) ENGINE=InnoDB;

INSERT INTO bee_ProjectMember (ProjectId, AdminId, Role)
SELECT id, AdminId, 'Administrator'
FROM bee_Project
ON DUPLICATE KEY UPDATE Role = VALUES(Role);
