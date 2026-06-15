CREATE TABLE IF NOT EXISTS bee_AppUser (
  id INT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  PhoneNumber VARCHAR(40) NOT NULL,
  DisplayName VARCHAR(100) NOT NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Active',
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_AppUser_Project_Phone (ProjectId, PhoneNumber),
  KEY IX_bee_AppUser_Project_Status (ProjectId, Status),
  CONSTRAINT FK_bee_AppUser_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_AppUserVerificationCode (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  PhoneNumber VARCHAR(40) NOT NULL,
  Purpose VARCHAR(40) NOT NULL DEFAULT 'Register',
  CodeHash VARCHAR(128) NOT NULL,
  ExpiresAtUtc DATETIME(6) NOT NULL,
  ConsumedAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_AppUserVerification_Project_Phone (ProjectId, PhoneNumber, Purpose, ExpiresAtUtc),
  CONSTRAINT FK_bee_AppUserVerification_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_AppUserSession (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  AppUserId INT NOT NULL,
  TokenHash VARCHAR(128) NOT NULL,
  ExpiresAtUtc DATETIME(6) NOT NULL,
  RevokedAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_AppUserSession_TokenHash (TokenHash),
  KEY IX_bee_AppUserSession_User_Expiry (AppUserId, ExpiresAtUtc),
  CONSTRAINT FK_bee_AppUserSession_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AppUserSession_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_AppUserDevice (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  AppUserId INT NOT NULL,
  DeviceIdentifier VARCHAR(160) NOT NULL,
  DeviceType VARCHAR(80) NULL,
  Platform VARCHAR(80) NULL,
  OsVersion VARCHAR(80) NULL,
  AppVersion VARCHAR(80) NULL,
  PushProvider VARCHAR(40) NULL,
  PushToken VARCHAR(500) NULL,
  LastLoginAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_AppUserDevice_User_Device (AppUserId, DeviceIdentifier),
  KEY IX_bee_AppUserDevice_Project_LastLogin (ProjectId, LastLoginAtUtc),
  CONSTRAINT FK_bee_AppUserDevice_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AppUserDevice_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_EdgeDeviceBindingToken (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  EdgeDeviceId INT NOT NULL,
  TokenHash VARCHAR(128) NOT NULL,
  ExpiresAtUtc DATETIME(6) NOT NULL,
  UsedAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_EdgeDeviceBindingToken_TokenHash (TokenHash),
  KEY IX_bee_EdgeDeviceBindingToken_Device_Expiry (EdgeDeviceId, ExpiresAtUtc),
  CONSTRAINT FK_bee_EdgeDeviceBindingToken_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_EdgeDeviceBindingToken_Device FOREIGN KEY (EdgeDeviceId)
    REFERENCES bee_EdgeDevice (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_EdgeDeviceUserBinding (
  EdgeDeviceId INT NOT NULL,
  AppUserId INT NOT NULL,
  BoundAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (EdgeDeviceId, AppUserId),
  KEY IX_bee_EdgeDeviceUserBinding_User (AppUserId),
  CONSTRAINT FK_bee_EdgeDeviceUserBinding_Device FOREIGN KEY (EdgeDeviceId)
    REFERENCES bee_EdgeDevice (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_EdgeDeviceUserBinding_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE
) ENGINE=InnoDB;
