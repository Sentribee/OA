CREATE TABLE IF NOT EXISTS bee_EdgeAiCodeGeneration (
  id INT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  LogicId INT NOT NULL,
  BranchName VARCHAR(100) NOT NULL,
  VersionName VARCHAR(80) NOT NULL,
  Status VARCHAR(40) NOT NULL,
  ProgressPercent INT NOT NULL DEFAULT 0,
  HandoffCommitSha VARCHAR(80) NULL,
  GeneratedCommitSha VARCHAR(80) NULL,
  StatusMessage VARCHAR(500) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_EdgeAiCodeGeneration_Project_Status (ProjectId, Status, CreatedAtUtc),
  KEY IX_bee_EdgeAiCodeGeneration_Logic (LogicId),
  CONSTRAINT FK_bee_EdgeAiCodeGeneration_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_EdgeAiCodeGeneration_Logic FOREIGN KEY (LogicId)
    REFERENCES bee_EdgeAiLogic (id) ON DELETE CASCADE
) ENGINE=InnoDB;
