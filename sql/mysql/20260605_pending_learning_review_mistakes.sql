CREATE TABLE IF NOT EXISTS bee_AnnotationReviewMistake (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  TargetType VARCHAR(40) NOT NULL,
  TargetId BIGINT NOT NULL,
  EdgeEventId INT NULL,
  EdgeEventSubjectId BIGINT NULL,
  EditorAdminId INT NULL,
  EditorName VARCHAR(100) NULL,
  EditorEmail VARCHAR(150) NULL,
  ReviewerAdminId INT NOT NULL,
  ReviewedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_AnnotationReviewMistake_Project_Time (ProjectId, ReviewedAtUtc),
  KEY IX_bee_AnnotationReviewMistake_Editor_Time (EditorAdminId, ReviewedAtUtc),
  KEY IX_bee_AnnotationReviewMistake_Target (TargetType, TargetId),
  CONSTRAINT FK_bee_AnnotationReviewMistake_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AnnotationReviewMistake_Editor FOREIGN KEY (EditorAdminId)
    REFERENCES bee_Admin (id) ON DELETE SET NULL,
  CONSTRAINT FK_bee_AnnotationReviewMistake_Reviewer FOREIGN KEY (ReviewerAdminId)
    REFERENCES bee_Admin (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AnnotationReviewMistake_Event FOREIGN KEY (EdgeEventId)
    REFERENCES bee_EdgeEvent (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AnnotationReviewMistake_Subject FOREIGN KEY (EdgeEventSubjectId)
    REFERENCES bee_EdgeEventSubject (id) ON DELETE CASCADE
) ENGINE=InnoDB;
