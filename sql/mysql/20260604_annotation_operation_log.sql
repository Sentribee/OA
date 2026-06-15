CREATE TABLE IF NOT EXISTS bee_AnnotationOperationLog (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  TargetType VARCHAR(40) NOT NULL,
  TargetId BIGINT NOT NULL,
  EdgeEventId INT NULL,
  EdgeEventSubjectId BIGINT NULL,
  AdminId INT NOT NULL,
  AdminName VARCHAR(100) NULL,
  AdminEmail VARCHAR(150) NULL,
  Action VARCHAR(80) NOT NULL,
  BoxCount INT NOT NULL DEFAULT 0,
  SaveAsPendingLearning TINYINT(1) NOT NULL DEFAULT 0,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_AnnotationOperationLog_Target_Time (TargetType, TargetId, CreatedAtUtc),
  KEY IX_bee_AnnotationOperationLog_Project_Time (ProjectId, CreatedAtUtc),
  KEY IX_bee_AnnotationOperationLog_Admin_Time (AdminId, CreatedAtUtc),
  CONSTRAINT FK_bee_AnnotationOperationLog_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AnnotationOperationLog_Admin FOREIGN KEY (AdminId)
    REFERENCES bee_Admin (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AnnotationOperationLog_Event FOREIGN KEY (EdgeEventId)
    REFERENCES bee_EdgeEvent (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AnnotationOperationLog_Subject FOREIGN KEY (EdgeEventSubjectId)
    REFERENCES bee_EdgeEventSubject (id) ON DELETE CASCADE
) ENGINE=InnoDB;
