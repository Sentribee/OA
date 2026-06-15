ALTER TABLE bee_AppUser
  MODIFY PhoneNumber VARCHAR(40) NULL,
  ADD COLUMN Email VARCHAR(150) NULL AFTER PhoneNumber,
  ADD UNIQUE KEY UX_bee_AppUser_Project_Email (ProjectId, Email);

ALTER TABLE bee_AppUserVerificationCode
  MODIFY PhoneNumber VARCHAR(40) NULL,
  ADD COLUMN Email VARCHAR(150) NULL AFTER PhoneNumber,
  ADD KEY IX_bee_AppUserVerification_Project_Email (ProjectId, Email, Purpose, ExpiresAtUtc);

CREATE TABLE IF NOT EXISTS bee_AppEmailDelivery (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  VerificationCodeId BIGINT NULL,
  Email VARCHAR(150) NOT NULL,
  Purpose VARCHAR(40) NOT NULL,
  Provider VARCHAR(40) NOT NULL DEFAULT 'AmazonSes',
  RequestStatus VARCHAR(40) NOT NULL,
  ErrorText VARCHAR(500) NULL,
  SentAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_AppEmailDelivery_Project_Time (ProjectId, SentAtUtc),
  KEY IX_bee_AppEmailDelivery_Email_Time (Email, SentAtUtc),
  CONSTRAINT FK_bee_AppEmailDelivery_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AppEmailDelivery_Verification FOREIGN KEY (VerificationCodeId)
    REFERENCES bee_AppUserVerificationCode (id) ON DELETE SET NULL
) ENGINE=InnoDB;
