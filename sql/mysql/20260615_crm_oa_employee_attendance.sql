CREATE TABLE IF NOT EXISTS bee_CrmOfficeAddress (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  LocationName VARCHAR(160) NOT NULL,
  AddressLine1 VARCHAR(260) NOT NULL,
  AddressLine2 VARCHAR(260) NULL,
  Suburb VARCHAR(120) NULL,
  City VARCHAR(120) NULL,
  Region VARCHAR(120) NULL,
  Postcode VARCHAR(40) NULL,
  Country VARCHAR(120) NOT NULL DEFAULT 'New Zealand',
  Phone VARCHAR(80) NULL,
  IsPrimary TINYINT(1) NOT NULL DEFAULT 0,
  Status VARCHAR(40) NOT NULL DEFAULT 'Active',
  Notes TEXT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_CrmOfficeAddress_Merchant_Status (MerchantId, Status),
  KEY IX_bee_CrmOfficeAddress_Project (ProjectId),
  CONSTRAINT FK_bee_CrmOfficeAddress_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmOfficeAddress_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_CrmEmployee (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  OfficeAddressId BIGINT NULL,
  RealName VARCHAR(160) NOT NULL,
  PreferredName VARCHAR(160) NULL,
  ResidentialAddress VARCHAR(700) NULL,
  Phone VARCHAR(80) NULL,
  WorkEmail VARCHAR(180) NULL,
  PrivateEmail VARCHAR(180) NULL,
  GstNumber VARCHAR(80) NULL,
  BankAccountNumber VARCHAR(120) NULL,
  StartDate DATE NULL,
  EndDate DATE NULL,
  JobTitle VARCHAR(160) NULL,
  EmploymentType VARCHAR(80) NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Active',
  Notes TEXT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_CrmEmployee_Merchant_Status (MerchantId, Status, RealName),
  KEY IX_bee_CrmEmployee_Project (ProjectId),
  KEY IX_bee_CrmEmployee_Office (OfficeAddressId),
  CONSTRAINT FK_bee_CrmEmployee_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmEmployee_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmEmployee_Office FOREIGN KEY (OfficeAddressId)
    REFERENCES bee_CrmOfficeAddress (id) ON DELETE SET NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_CrmEmployeeAttendance (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  EmployeeId BIGINT NOT NULL,
  OfficeAddressId BIGINT NULL,
  AttendanceDate DATE NOT NULL,
  ClockInAtUtc DATETIME(6) NOT NULL,
  ClockOutAtUtc DATETIME(6) NULL,
  ClockInIp VARCHAR(80) NULL,
  ClockOutIp VARCHAR(80) NULL,
  ClockInNote TEXT NULL,
  ClockOutNote TEXT NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Open',
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_CrmAttendance_Merchant_Date (MerchantId, AttendanceDate, ClockInAtUtc),
  KEY IX_bee_CrmAttendance_Employee_Date (EmployeeId, AttendanceDate, ClockInAtUtc),
  KEY IX_bee_CrmAttendance_Project (ProjectId),
  CONSTRAINT FK_bee_CrmAttendance_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmAttendance_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmAttendance_Employee FOREIGN KEY (EmployeeId)
    REFERENCES bee_CrmEmployee (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmAttendance_Office FOREIGN KEY (OfficeAddressId)
    REFERENCES bee_CrmOfficeAddress (id) ON DELETE SET NULL
) ENGINE=InnoDB;
