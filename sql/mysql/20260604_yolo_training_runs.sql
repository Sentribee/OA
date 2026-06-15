CREATE TABLE IF NOT EXISTS bee_YoloTrainingRun (
  id INT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  ModelKind VARCHAR(40) NOT NULL,
  Status VARCHAR(40) NOT NULL,
  NextTrainingAtUtc DATETIME(6) NULL,
  StartedAtUtc DATETIME(6) NULL,
  CompletedAtUtc DATETIME(6) NULL,
  Notes VARCHAR(500) NULL,
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_YoloTrainingRun_Project_Kind (ProjectId, ModelKind),
  KEY IX_bee_YoloTrainingRun_Status_Time (Status, NextTrainingAtUtc),
  CONSTRAINT FK_bee_YoloTrainingRun_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;
