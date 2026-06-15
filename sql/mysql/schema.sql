CREATE TABLE IF NOT EXISTS bee_Admin (
  id INT NOT NULL AUTO_INCREMENT,
  LoginID VARCHAR(50) NOT NULL,
  Pwd VARCHAR(512) NOT NULL,
  Roles VARCHAR(200) NULL,
  LastLoginTime DATETIME(6) NULL,
  DisplayName VARCHAR(100) NULL,
  Email VARCHAR(150) NULL,
  AvatarUrl VARCHAR(500) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_Admin_LoginID (LoginID),
  UNIQUE KEY UX_bee_Admin_Email (Email)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_Project (
  id INT NOT NULL AUTO_INCREMENT,
  AdminId INT NOT NULL,
  ProjectName VARCHAR(150) NOT NULL,
  ProjectDescription TEXT NULL,
  LogoUrl VARCHAR(500) NULL,
  CompanyName VARCHAR(150) NULL,
  WebsiteUrl VARCHAR(500) NULL,
  ProjectKind VARCHAR(40) NOT NULL DEFAULT 'EdgeAi',
  Visibility VARCHAR(20) NOT NULL DEFAULT 'Private',
  TimeZoneId VARCHAR(80) NOT NULL DEFAULT 'Pacific/Auckland',
  EdgeAiGitRepositoryUrl VARCHAR(500) NOT NULL DEFAULT 'https://github.com/Sentribee/Sentribee-edge.git',
  EdgeAiGitBranch VARCHAR(100) NOT NULL DEFAULT 'main',
  EdgeAiGitWorkingDirectory VARCHAR(500) NULL,
  AiModelYamlPath VARCHAR(500) NOT NULL DEFAULT '/home/ubuntu/sentribee/hobson/data.yaml',
  PersonPpeModelYamlPath VARCHAR(500) NOT NULL DEFAULT '/home/ubuntu/sentribee/hobson/person_crops_ppe/data.yaml',
  ApiKeyHash VARCHAR(128) NULL,
  ApiKeyPrefix VARCHAR(32) NULL,
  ApiKeyCreatedAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_Project_AdminId (AdminId),
  UNIQUE KEY UX_bee_Project_ApiKeyHash (ApiKeyHash),
  CONSTRAINT FK_bee_Project_Admin FOREIGN KEY (AdminId)
    REFERENCES bee_Admin (id) ON DELETE CASCADE
) ENGINE=InnoDB;

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

CREATE TABLE IF NOT EXISTS bee_ProjectRule (
  id INT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  EdgeAiCodeVersionId INT NULL,
  ChangeType VARCHAR(20) NOT NULL DEFAULT 'Active',
  Dimension VARCHAR(100) NOT NULL,
  RuleText VARCHAR(1000) NOT NULL,
  SourcePrompt TEXT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_ProjectRule_ProjectId_CreatedAtUtc (ProjectId, CreatedAtUtc),
  KEY IX_bee_ProjectRule_CodeVersion (EdgeAiCodeVersionId),
  CONSTRAINT FK_bee_ProjectRule_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_DeviceCatalog (
  id INT NOT NULL AUTO_INCREMENT,
  CatalogName VARCHAR(150) NOT NULL,
  Description VARCHAR(500) NULL,
  SortOrder INT NOT NULL DEFAULT 0,
  IsActive TINYINT(1) NOT NULL DEFAULT 1,
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_DeviceCatalog_CatalogName (CatalogName)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_EdgeDevice (
  id INT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  AdminId INT NOT NULL,
  DeviceCode VARCHAR(40) NOT NULL,
  DeviceName VARCHAR(150) NOT NULL,
  Address VARCHAR(300) NOT NULL,
  Latitude DECIMAL(10,7) NULL,
  Longitude DECIMAL(10,7) NULL,
  GooglePlaceId VARCHAR(200) NULL,
  StreetViewThumbnailUrl VARCHAR(1000) NULL,
  IpAddress VARCHAR(45) NOT NULL,
  ServerResourceInstanceName VARCHAR(80) NULL,
  BindingCode VARCHAR(16) NULL,
  Description TEXT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_EdgeDevice_DeviceCode (DeviceCode),
  UNIQUE KEY UX_bee_EdgeDevice_BindingCode (BindingCode),
  KEY IX_bee_EdgeDevice_AdminId_CreatedAtUtc (AdminId, CreatedAtUtc),
  KEY IX_bee_EdgeDevice_ProjectId (ProjectId),
  KEY IX_bee_EdgeDevice_Location (Latitude, Longitude),
  CONSTRAINT FK_bee_EdgeDevice_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_EdgeDevice_Admin FOREIGN KEY (AdminId)
    REFERENCES bee_Admin (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_EdgeDeviceEndpoint (
  id INT NOT NULL AUTO_INCREMENT,
  EdgeDeviceId INT NOT NULL,
  CatalogDeviceId INT NULL,
  DeviceName VARCHAR(150) NOT NULL,
  AccessUrl VARCHAR(500) NOT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_EdgeDeviceEndpoint_EdgeDeviceId (EdgeDeviceId),
  CONSTRAINT FK_bee_EdgeDeviceEndpoint_EdgeDevice FOREIGN KEY (EdgeDeviceId)
    REFERENCES bee_EdgeDevice (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_EdgeDeviceEndpoint_Catalog FOREIGN KEY (CatalogDeviceId)
    REFERENCES bee_DeviceCatalog (id) ON DELETE SET NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_EdgeEvent (
  id INT NOT NULL AUTO_INCREMENT,
  EdgeDeviceId INT NOT NULL,
  Title VARCHAR(200) NOT NULL,
  EventDescription TEXT NULL,
  ImageUrl VARCHAR(500) NULL,
  AnnotationJson MEDIUMTEXT NULL,
  YoloLabelUrl VARCHAR(500) NULL,
  PpeReviewJson JSON NULL,
  RawPayloadJson JSON NULL,
  AnnotatedAtUtc DATETIME(6) NULL,
  EventTimeUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  Status VARCHAR(40) NOT NULL DEFAULT 'Real Risk',
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_EdgeEvent_Device_Status_Time (EdgeDeviceId, Status, EventTimeUtc),
  CONSTRAINT FK_bee_EdgeEvent_EdgeDevice FOREIGN KEY (EdgeDeviceId)
    REFERENCES bee_EdgeDevice (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_EdgeEventVideo (
  id INT NOT NULL AUTO_INCREMENT,
  EdgeEventId INT NOT NULL,
  S3Key VARCHAR(700) NOT NULL,
  VideoUrl VARCHAR(1000) NULL,
  UploadId VARCHAR(700) NOT NULL,
  FileName VARCHAR(255) NULL,
  ContentType VARCHAR(100) NOT NULL DEFAULT 'video/mp4',
  FileSizeBytes BIGINT NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Uploading',
  PartEtagsJson JSON NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  CompletedAtUtc DATETIME(6) NULL,
  PRIMARY KEY (id),
  KEY IX_bee_EdgeEventVideo_Event_Status (EdgeEventId, Status),
  CONSTRAINT FK_bee_EdgeEventVideo_EdgeEvent FOREIGN KEY (EdgeEventId)
    REFERENCES bee_EdgeEvent (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_ProjectApiClientSession (
  id INT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  TokenHash VARCHAR(128) NOT NULL,
  ClientName VARCHAR(150) NULL,
  ExpiresAtUtc DATETIME(6) NOT NULL,
  RevokedAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_ProjectApiClientSession_TokenHash (TokenHash),
  KEY IX_bee_ProjectApiClientSession_Project_Expiry (ProjectId, ExpiresAtUtc),
  CONSTRAINT FK_bee_ProjectApiClientSession_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_EdgeAiHeartbeat (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  EdgeDeviceId INT NOT NULL,
  RuntimeStatus VARCHAR(80) NOT NULL,
  DeviceStatus VARCHAR(80) NOT NULL,
  DetailJson JSON NULL,
  ReportedAtUtc DATETIME(6) NOT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_EdgeAiHeartbeat_Project_Device_Time (ProjectId, EdgeDeviceId, ReportedAtUtc),
  CONSTRAINT FK_bee_EdgeAiHeartbeat_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_EdgeAiHeartbeat_Device FOREIGN KEY (EdgeDeviceId)
    REFERENCES bee_EdgeDevice (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_EdgeDeviceDailyStat (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  EdgeDeviceId INT NOT NULL,
  StatDate DATE NOT NULL,
  PeopleCount INT NOT NULL DEFAULT 0,
  BraceletCount INT NOT NULL DEFAULT 0,
  MachineryVehicleCount INT NOT NULL DEFAULT 0,
  PpeComplianceRate DECIMAL(5,2) NULL,
  RiskEventCount INT NOT NULL DEFAULT 0,
  RiskPersonCount INT NOT NULL DEFAULT 0,
  TopRiskSubjectKey VARCHAR(120) NULL,
  TopRiskSubjectRiskCount INT NOT NULL DEFAULT 0,
  LastHeartbeatAtUtc DATETIME(6) NULL,
  LastEventAtUtc DATETIME(6) NULL,
  DetailJson JSON NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_EdgeDeviceDailyStat_Device_Date (EdgeDeviceId, StatDate),
  KEY IX_bee_EdgeDeviceDailyStat_Project_Date (ProjectId, StatDate),
  CONSTRAINT FK_bee_EdgeDeviceDailyStat_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_EdgeDeviceDailyStat_Device FOREIGN KEY (EdgeDeviceId)
    REFERENCES bee_EdgeDevice (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_EdgeEventAnalysis (
  EdgeEventId INT NOT NULL,
  PeopleCount INT NOT NULL DEFAULT 0,
  MachineryVehicleCount INT NOT NULL DEFAULT 0,
  ToolCount INT NOT NULL DEFAULT 0,
  PpeCompliantPeopleCount INT NOT NULL DEFAULT 0,
  RiskPersonCount INT NOT NULL DEFAULT 0,
  PpeComplianceRate DECIMAL(5,2) NULL,
  RiskCategory VARCHAR(120) NULL,
  RiskSeverity VARCHAR(40) NOT NULL DEFAULT 'Review',
  Summary VARCHAR(500) NULL,
  AnalysisJson JSON NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (EdgeEventId),
  KEY IX_bee_EdgeEventAnalysis_Risk (RiskSeverity, RiskCategory),
  CONSTRAINT FK_bee_EdgeEventAnalysis_Event FOREIGN KEY (EdgeEventId)
    REFERENCES bee_EdgeEvent (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_EdgeEventSubject (
  id BIGINT NOT NULL AUTO_INCREMENT,
  EdgeEventId INT NOT NULL,
  SubjectKey VARCHAR(120) NOT NULL,
  SubjectType VARCHAR(40) NOT NULL DEFAULT 'Person',
  TrackingLabel VARCHAR(150) NULL,
  CropImageUrl VARCHAR(1000) NULL,
  PreviewImageUrl VARCHAR(1000) NULL,
  BoundingBoxJson JSON NULL,
  PpeBoxJson JSON NULL,
  PpeStatusJson JSON NULL,
  LearningStatus VARCHAR(80) NOT NULL DEFAULT 'None',
  IsRisk TINYINT(1) NOT NULL DEFAULT 0,
  RiskCategory VARCHAR(120) NULL,
  RiskSeverity VARCHAR(40) NULL,
  RiskReason VARCHAR(500) NULL,
  AnalysisJson JSON NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_EdgeEventSubject_Event_Key (EdgeEventId, SubjectKey),
  KEY IX_bee_EdgeEventSubject_Risk (SubjectType, IsRisk, RiskSeverity),
  KEY IX_bee_EdgeEventSubject_Learning (SubjectType, LearningStatus, UpdatedAtUtc),
  CONSTRAINT FK_bee_EdgeEventSubject_Event FOREIGN KEY (EdgeEventId)
    REFERENCES bee_EdgeEvent (id) ON DELETE CASCADE
) ENGINE=InnoDB;

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

CREATE TABLE IF NOT EXISTS bee_EdgeDeviceDailyRiskPerson (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  EdgeDeviceId INT NOT NULL,
  StatDate DATE NOT NULL,
  PersonGroupKey VARCHAR(120) NOT NULL,
  DisplayLabel VARCHAR(150) NULL,
  RepresentativeSubjectId BIGINT NULL,
  RepresentativeCropImageUrl VARCHAR(1000) NULL,
  RepresentativePreviewImageUrl VARCHAR(1000) NULL,
  RiskEventCount INT NOT NULL DEFAULT 0,
  RiskSubjectCount INT NOT NULL DEFAULT 0,
  SimilarityHash VARCHAR(32) NULL,
  SubjectIdsJson JSON NULL,
  EventIdsJson JSON NULL,
  FirstEventAtUtc DATETIME(6) NULL,
  LastEventAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_EdgeDeviceDailyRiskPerson_Device_Date_Group (EdgeDeviceId, StatDate, PersonGroupKey),
  KEY IX_bee_EdgeDeviceDailyRiskPerson_Project_Date (ProjectId, StatDate),
  KEY IX_bee_EdgeDeviceDailyRiskPerson_Device_Date_Rank (EdgeDeviceId, StatDate, RiskEventCount, RiskSubjectCount),
  CONSTRAINT FK_bee_EdgeDeviceDailyRiskPerson_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_EdgeDeviceDailyRiskPerson_Device FOREIGN KEY (EdgeDeviceId)
    REFERENCES bee_EdgeDevice (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_EdgeDeviceDailyRiskPerson_Subject FOREIGN KEY (RepresentativeSubjectId)
    REFERENCES bee_EdgeEventSubject (id) ON DELETE SET NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_AppUser (
  id INT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  PhoneNumber VARCHAR(40) NULL,
  Email VARCHAR(150) NULL,
  DisplayName VARCHAR(100) NOT NULL,
  FirstName VARCHAR(80) NULL,
  LastName VARCHAR(80) NULL,
  Gender VARCHAR(40) NULL,
  AvatarUrl VARCHAR(500) NULL,
  Bio VARCHAR(280) NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Active',
  ActivatedAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_AppUser_Project_Phone (ProjectId, PhoneNumber),
  UNIQUE KEY UX_bee_AppUser_Project_Email (ProjectId, Email),
  KEY IX_bee_AppUser_Project_Status (ProjectId, Status),
  CONSTRAINT FK_bee_AppUser_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_AppUserVerificationCode (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  PhoneNumber VARCHAR(40) NULL,
  Email VARCHAR(150) NULL,
  Purpose VARCHAR(40) NOT NULL DEFAULT 'Register',
  CodeHash VARCHAR(128) NOT NULL,
  ExpiresAtUtc DATETIME(6) NOT NULL,
  ConsumedAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_AppUserVerification_Project_Phone (ProjectId, PhoneNumber, Purpose, ExpiresAtUtc),
  KEY IX_bee_AppUserVerification_Project_Email (ProjectId, Email, Purpose, ExpiresAtUtc),
  CONSTRAINT FK_bee_AppUserVerification_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_AppSmsDelivery (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  VerificationCodeId BIGINT NULL,
  PhoneNumber VARCHAR(40) NOT NULL,
  Purpose VARCHAR(40) NOT NULL,
  Provider VARCHAR(40) NOT NULL DEFAULT 'Vonage',
  ProviderMessageId VARCHAR(120) NULL,
  RequestStatus VARCHAR(40) NOT NULL,
  DeliveryStatus VARCHAR(80) NULL,
  ErrorCode VARCHAR(40) NULL,
  ErrorText VARCHAR(500) NULL,
  RawResponseJson JSON NULL,
  DeliveryReceiptJson JSON NULL,
  SentAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  DeliveredAtUtc DATETIME(6) NULL,
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_AppSmsDelivery_Project_Time (ProjectId, SentAtUtc),
  KEY IX_bee_AppSmsDelivery_MessageId (ProviderMessageId),
  CONSTRAINT FK_bee_AppSmsDelivery_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AppSmsDelivery_Verification FOREIGN KEY (VerificationCodeId)
    REFERENCES bee_AppUserVerificationCode (id) ON DELETE SET NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_AppEmailDelivery (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  VerificationCodeId BIGINT NULL,
  Email VARCHAR(150) NOT NULL,
  Purpose VARCHAR(40) NOT NULL,
  Provider VARCHAR(40) NOT NULL DEFAULT 'AmazonSes',
  ProviderMessageId VARCHAR(150) NULL,
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
  DeviceKeyHash VARCHAR(128) NULL,
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

CREATE TABLE IF NOT EXISTS bee_AppUserRiskNotificationPreference (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  AppUserId INT NOT NULL,
  EdgeDeviceId INT NOT NULL,
  RiskSeverity VARCHAR(40) NOT NULL,
  PushEnabled TINYINT(1) NOT NULL DEFAULT 0,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_AppRiskPref_User_Device_Severity (AppUserId, EdgeDeviceId, RiskSeverity),
  KEY IX_bee_AppRiskPref_Project_Device (ProjectId, EdgeDeviceId),
  CONSTRAINT FK_bee_AppRiskPref_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AppRiskPref_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AppRiskPref_Device FOREIGN KEY (EdgeDeviceId)
    REFERENCES bee_EdgeDevice (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_AppRiskNotification (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  AppUserId INT NOT NULL,
  EdgeDeviceId INT NOT NULL,
  EdgeEventId INT NOT NULL,
  RiskSeverity VARCHAR(40) NOT NULL,
  Title VARCHAR(200) NOT NULL,
  Message VARCHAR(500) NULL,
  IsRead TINYINT(1) NOT NULL DEFAULT 0,
  PushStatus VARCHAR(40) NOT NULL DEFAULT 'Suppressed',
  PushProviderMessageId VARCHAR(100) NULL,
  PushAttemptedAtUtc DATETIME(6) NULL,
  PushErrorText VARCHAR(500) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  ReadAtUtc DATETIME(6) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_AppRiskNotification_User_Event (AppUserId, EdgeEventId),
  KEY IX_bee_AppRiskNotification_User_Read_Time (AppUserId, IsRead, CreatedAtUtc),
  KEY IX_bee_AppRiskNotification_Project_Device_Time (ProjectId, EdgeDeviceId, CreatedAtUtc),
  CONSTRAINT FK_bee_AppRiskNotification_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AppRiskNotification_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AppRiskNotification_Device FOREIGN KEY (EdgeDeviceId)
    REFERENCES bee_EdgeDevice (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AppRiskNotification_Event FOREIGN KEY (EdgeEventId)
    REFERENCES bee_EdgeEvent (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeMerchant (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  GooglePlaceId VARCHAR(160) NULL,
  GooglePlaceResourceName VARCHAR(240) NULL,
  Name VARCHAR(220) NOT NULL,
  NormalizedName VARCHAR(220) NOT NULL,
  Address VARCHAR(600) NULL,
  PhoneNumber VARCHAR(80) NULL,
  WebsiteUrl VARCHAR(700) NULL,
  GoogleMapsUri VARCHAR(700) NULL,
  PrimaryType VARCHAR(120) NULL,
  BusinessStatus VARCHAR(80) NULL,
  Latitude DECIMAL(10,7) NULL,
  Longitude DECIMAL(10,7) NULL,
  Rating DECIMAL(4,2) NULL,
  UserRatingCount INT NULL,
  PriceLevel VARCHAR(80) NULL,
  DineIn TINYINT(1) NULL,
  Takeout TINYINT(1) NULL,
  GooglePhotoName VARCHAR(500) NULL,
  GooglePhotoUri VARCHAR(1000) NULL,
  GooglePhotoAttributionsJson JSON NULL,
  AiCoverImageUrl VARCHAR(1000) NULL,
  AiCoverPrompt VARCHAR(1600) NULL,
  CoverSource VARCHAR(40) NULL,
  CoverCategory VARCHAR(80) NULL,
  StreetViewImageUrl VARCHAR(1000) NULL,
  SourceJson JSON NULL,
  SyncStatus VARCHAR(40) NOT NULL DEFAULT 'LocalOnly',
  LastGoogleSyncAtUtc DATETIME(6) NULL,
  LastAiCoverGeneratedAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_SpendBeeMerchant_Project_GooglePlace (ProjectId, GooglePlaceId),
  KEY IX_bee_SpendBeeMerchant_Project_Name (ProjectId, NormalizedName),
  KEY IX_bee_SpendBeeMerchant_Project_Updated (ProjectId, UpdatedAtUtc),
  CONSTRAINT FK_bee_SpendBeeMerchant_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeePlatform (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  Name VARCHAR(160) NOT NULL,
  DisplayName VARCHAR(160) NULL,
  NormalizedName VARCHAR(180) NOT NULL,
  PlatformType VARCHAR(80) NOT NULL DEFAULT 'FoodDelivery',
  LogoUrl VARCHAR(1000) NULL,
  WebsiteUrl VARCHAR(700) NULL,
  CountryOrRegion VARCHAR(120) NULL,
  KnownAliasesJson JSON NULL,
  SourceJson JSON NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_SpendBeePlatform_Project_Name (ProjectId, NormalizedName),
  KEY IX_bee_SpendBeePlatform_Project_Type (ProjectId, PlatformType),
  CONSTRAINT FK_bee_SpendBeePlatform_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeReceipt (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  AppUserId INT NOT NULL,
  MerchantId BIGINT NULL,
  PlatformId BIGINT NULL,
  ReceiptImageSetHash VARCHAR(128) NULL,
  ReceiptCanonicalHash VARCHAR(128) NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Processing',
  ReceiptType VARCHAR(60) NULL,
  FulfillmentType VARCHAR(60) NULL,
  MerchantName VARCHAR(200) NULL,
  MerchantAddress VARCHAR(500) NULL,
  PlatformOrderNumber VARCHAR(120) NULL,
  PurchasedAtUtc DATETIME(6) NULL,
  OrderedAtUtc DATETIME(6) NULL,
  PickupAtUtc DATETIME(6) NULL,
  DeliveredAtUtc DATETIME(6) NULL,
  Currency VARCHAR(12) NULL,
  Subtotal DECIMAL(12,2) NULL,
  Tax DECIMAL(12,2) NULL,
  DeliveryFee DECIMAL(12,2) NULL,
  ServiceFee DECIMAL(12,2) NULL,
  PlatformDiscount DECIMAL(12,2) NULL,
  Total DECIMAL(12,2) NULL,
  OverallConfidence DECIMAL(8,5) NULL,
  EstimatedErrorRate DECIMAL(8,5) NULL,
  FailedChecksJson JSON NULL,
  RawOcrJson JSON NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_SpendBeeReceipt_Project_ImageSetHash (ProjectId, ReceiptImageSetHash),
  UNIQUE KEY UX_bee_SpendBeeReceipt_Project_CanonicalHash (ProjectId, ReceiptCanonicalHash),
  KEY IX_bee_SpendBeeReceipt_Project_Time (ProjectId, CreatedAtUtc),
  KEY IX_bee_SpendBeeReceipt_User_Time (AppUserId, CreatedAtUtc),
  KEY IX_bee_SpendBeeReceipt_Status (ProjectId, Status),
  KEY IX_bee_SpendBeeReceipt_Merchant_Time (MerchantId, CreatedAtUtc),
  KEY IX_bee_SpendBeeReceipt_Platform_Time (PlatformId, CreatedAtUtc),
  CONSTRAINT FK_bee_SpendBeeReceipt_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeReceipt_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeReceipt_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_SpendBeeMerchant (id) ON DELETE SET NULL,
  CONSTRAINT FK_bee_SpendBeeReceipt_Platform FOREIGN KEY (PlatformId)
    REFERENCES bee_SpendBeePlatform (id) ON DELETE SET NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeReceiptUpload (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  AppUserId INT NOT NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Uploading',
  Timezone VARCHAR(80) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  CompletedAtUtc DATETIME(6) NULL,
  CancelledAtUtc DATETIME(6) NULL,
  PRIMARY KEY (id),
  KEY IX_bee_SpendBeeReceiptUpload_User_Time (AppUserId, CreatedAtUtc),
  KEY IX_bee_SpendBeeReceiptUpload_Project_Status (ProjectId, Status),
  CONSTRAINT FK_bee_SpendBeeReceiptUpload_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeReceiptUpload_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeReceiptUploadImage (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ReceiptUploadId BIGINT NOT NULL,
  S3Key VARCHAR(700) NOT NULL,
  UploadId VARCHAR(700) NOT NULL,
  FileName VARCHAR(255) NULL,
  ContentType VARCHAR(80) NOT NULL,
  FileSizeBytes BIGINT NULL,
  SortOrder INT NOT NULL DEFAULT 0,
  Status VARCHAR(40) NOT NULL DEFAULT 'Uploading',
  ImageUrl VARCHAR(800) NULL,
  PartEtagsJson JSON NOT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  CompletedAtUtc DATETIME(6) NULL,
  PRIMARY KEY (id),
  KEY IX_bee_SpendBeeReceiptUploadImage_Upload (ReceiptUploadId, SortOrder),
  CONSTRAINT FK_bee_SpendBeeReceiptUploadImage_Upload FOREIGN KEY (ReceiptUploadId)
    REFERENCES bee_SpendBeeReceiptUpload (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeReceiptImage (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ReceiptId BIGINT NOT NULL,
  ImageUrl VARCHAR(800) NOT NULL,
  ContentType VARCHAR(80) NOT NULL,
  SortOrder INT NOT NULL DEFAULT 0,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_SpendBeeReceiptImage_Receipt (ReceiptId, SortOrder),
  CONSTRAINT FK_bee_SpendBeeReceiptImage_Receipt FOREIGN KEY (ReceiptId)
    REFERENCES bee_SpendBeeReceipt (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeReceiptLineItem (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ReceiptId BIGINT NOT NULL,
  ItemName VARCHAR(240) NOT NULL,
  Quantity DECIMAL(12,3) NULL,
  UnitPrice DECIMAL(12,2) NULL,
  Amount DECIMAL(12,2) NULL,
  Category VARCHAR(80) NULL,
  Confidence DECIMAL(8,5) NULL,
  SortOrder INT NOT NULL DEFAULT 0,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_SpendBeeReceiptLineItem_Receipt (ReceiptId, SortOrder),
  CONSTRAINT FK_bee_SpendBeeReceiptLineItem_Receipt FOREIGN KEY (ReceiptId)
    REFERENCES bee_SpendBeeReceipt (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeReceiptGroup (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  AppUserId INT NOT NULL,
  Title VARCHAR(160) NOT NULL,
  Description VARCHAR(500) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_SpendBeeReceiptGroup_User_Time (AppUserId, CreatedAtUtc),
  CONSTRAINT FK_bee_SpendBeeReceiptGroup_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeReceiptGroup_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeReceiptGroupReceipt (
  ReceiptGroupId BIGINT NOT NULL,
  ReceiptId BIGINT NOT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (ReceiptGroupId, ReceiptId),
  KEY IX_bee_SpendBeeReceiptGroupReceipt_Receipt (ReceiptId),
  CONSTRAINT FK_bee_SpendBeeReceiptGroupReceipt_Group FOREIGN KEY (ReceiptGroupId)
    REFERENCES bee_SpendBeeReceiptGroup (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeReceiptGroupReceipt_Receipt FOREIGN KEY (ReceiptId)
    REFERENCES bee_SpendBeeReceipt (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_YoloModelVersion (
  id INT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  VersionName VARCHAR(80) NOT NULL,
  Status VARCHAR(80) NOT NULL,
  Notes VARCHAR(500) NULL,
  ModelFileUrl VARCHAR(500) NULL,
  YamlDescription TEXT NULL,
  IsCurrent TINYINT(1) NOT NULL DEFAULT 0,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  TrainedAtUtc DATETIME(6) NULL,
  PRIMARY KEY (id),
  KEY IX_bee_YoloModelVersion_Project_Current (ProjectId, IsCurrent, CreatedAtUtc),
  CONSTRAINT FK_bee_YoloModelVersion_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_YoloTrainingSchedule (
  ProjectId INT NOT NULL,
  NextTrainingAtUtc DATETIME(6) NULL,
  AutoSchedule TINYINT(1) NOT NULL DEFAULT 0,
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (ProjectId),
  CONSTRAINT FK_bee_YoloTrainingSchedule_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;

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

CREATE TABLE IF NOT EXISTS bee_EdgeAiLogic (
  id INT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  LogicName VARCHAR(150) NOT NULL,
  Description VARCHAR(500) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_EdgeAiLogic_ProjectId (ProjectId),
  CONSTRAINT FK_bee_EdgeAiLogic_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_EdgeAiCodeVersion (
  id INT NOT NULL AUTO_INCREMENT,
  LogicId INT NOT NULL,
  VersionName VARCHAR(80) NOT NULL,
  Description VARCHAR(500) NULL,
  IsCurrent TINYINT(1) NOT NULL DEFAULT 0,
  PackageSizeBytes BIGINT NULL,
  FileCount INT NULL,
  DirectoryStructure TEXT NOT NULL,
  FeatureList TEXT NOT NULL,
  Notes VARCHAR(500) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_EdgeAiCodeVersion_Logic_Current (LogicId, IsCurrent, CreatedAtUtc),
  CONSTRAINT FK_bee_EdgeAiCodeVersion_Logic FOREIGN KEY (LogicId)
    REFERENCES bee_EdgeAiLogic (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_EdgeAiInstance (
  id INT NOT NULL AUTO_INCREMENT,
  LogicId INT NOT NULL,
  EdgeDeviceId INT NOT NULL,
  CodeVersionId INT NULL,
  InstanceName VARCHAR(150) NOT NULL,
  Status VARCHAR(80) NOT NULL,
  RuntimeStatus VARCHAR(80) NOT NULL DEFAULT 'Pending',
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_EdgeAiInstance_Logic_Device (LogicId, EdgeDeviceId),
  KEY IX_bee_EdgeAiInstance_LogicId (LogicId),
  KEY IX_bee_EdgeAiInstance_EdgeDeviceId (EdgeDeviceId),
  CONSTRAINT FK_bee_EdgeAiInstance_Logic FOREIGN KEY (LogicId)
    REFERENCES bee_EdgeAiLogic (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_EdgeAiInstance_EdgeDevice FOREIGN KEY (EdgeDeviceId)
    REFERENCES bee_EdgeDevice (id) ON DELETE CASCADE
  ,
  CONSTRAINT FK_bee_EdgeAiInstance_CodeVersion FOREIGN KEY (CodeVersionId)
    REFERENCES bee_EdgeAiCodeVersion (id) ON DELETE SET NULL
) ENGINE=InnoDB;

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

INSERT INTO bee_Admin (LoginID, Pwd, Roles, DisplayName, Email)
VALUES ('admin', 'password', 'Administrator', 'SentriBee Admin', 'admin@sentribee.com')
ON DUPLICATE KEY UPDATE LoginID = VALUES(LoginID);

INSERT INTO bee_Project (AdminId, ProjectName, ProjectDescription, LogoUrl, CompanyName, WebsiteUrl, Visibility)
SELECT admin.id,
  'PREVENX Edge AI Construction Safety Recognition',
  'PREVENX Edge AI Construction Safety Recognition System V1.0 MVP for PPE compliance, danger-zone monitoring, BLE wristband association, risk classification, alerts, human review, and continuous AI learning.',
  '/images/prevenx-logo.jpg',
  'PREVENX',
  'https://prevenx.ai',
  'Private'
FROM bee_Admin AS admin
WHERE admin.LoginID = 'admin'
ON DUPLICATE KEY UPDATE
  ProjectName = VALUES(ProjectName),
  ProjectDescription = VALUES(ProjectDescription),
  LogoUrl = VALUES(LogoUrl),
  CompanyName = VALUES(CompanyName),
  WebsiteUrl = VALUES(WebsiteUrl),
  Visibility = VALUES(Visibility);

INSERT INTO bee_ProjectMember (ProjectId, AdminId, Role)
SELECT project.id, project.AdminId, 'Administrator'
FROM bee_Project AS project
ON DUPLICATE KEY UPDATE Role = VALUES(Role);

DELETE projectRule
FROM bee_ProjectRule AS projectRule
INNER JOIN bee_Project AS project ON project.id = projectRule.ProjectId
INNER JOIN bee_Admin AS admin ON admin.id = project.AdminId
WHERE admin.LoginID = 'admin';

INSERT INTO bee_ProjectRule (ProjectId, Dimension, RuleText, SourcePrompt)
SELECT project.id, seed.Dimension, seed.RuleText, seed.SourcePrompt
FROM bee_Project AS project
INNER JOIN bee_Admin AS admin ON admin.id = project.AdminId
INNER JOIN (
  SELECT 'Environment Recognition' AS Dimension,
    'Ingest construction-site data from RTSP IP cameras, BLE E01/E03 gateways, MQTT bracelet events, and site internet connectivity.' AS RuleText,
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.' AS SourcePrompt
  UNION ALL SELECT 'Environment Recognition',
    'Decode real-time RTSP video with reconnect handling and scale toward multi-camera construction-site deployments.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
  UNION ALL SELECT 'Environment Recognition',
    'Validate construction-scene quality and background context before applying PPE, person, high-work, and zone-risk analysis.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
  UNION ALL SELECT 'Recognition Content',
    'Detect construction workers and required PPE including helmet, vest, goggles, gloves, boots, and mask.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
  UNION ALL SELECT 'Recognition Content',
    'Recognize danger-zone entry, scaffold/high-work context, unauthorized visitors, repeated unsafe behavior, and critical abnormal events.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
  UNION ALL SELECT 'Recognition Content',
    'Associate detected workers with BLE wristbands through MAC/RSSI events and persistent worker RID identity.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
  UNION ALL SELECT 'Recognition Logic',
    'Use YOLOv8 for person and PPE detection, BoT-SORT or ByteTrack for tracking, and OSNet-AIN ReID for persistent worker identity.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
  UNION ALL SELECT 'Recognition Logic',
    'Apply polygon-based danger-zone and scaffold risk analysis to determine whether tracked workers enter restricted or high-risk areas.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
  UNION ALL SELECT 'Recognition Logic',
    'Use safety rules to evaluate missing PPE, full PPE compliance, person-to-bracelet pairing, and alarm cooldown eligibility.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
  UNION ALL SELECT 'Recognition Logic',
    'Use OpenAI-assisted semantic verification for PPE missing checks, real-person validation, high-work background checks, and complex scene understanding.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
  UNION ALL SELECT 'Event Recognition',
    'Generate Level 1 risk events for worker-level safety issues such as missing PPE or worker entry into a danger zone.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
  UNION ALL SELECT 'Event Recognition',
    'Generate Level 2 risk events for site-management issues such as unauthorized visitor entry, long-term PPE non-compliance, or repeated unsafe behavior.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
  UNION ALL SELECT 'Event Recognition',
    'Generate Level 3 risk events for critical incidents such as fire, structural collapse, severe injury, or major safety accidents.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
  UNION ALL SELECT 'Response Method',
    'For Level 1 events, trigger BLE wristband vibration or on-site reminders to immediately notify the affected worker.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
  UNION ALL SELECT 'Response Method',
    'For Level 2 events, notify administrators by email and future app push notifications for site-management intervention.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
  UNION ALL SELECT 'Response Method',
    'For Level 3 events, escalate to responsible executives and emergency notification workflows.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
  UNION ALL SELECT 'Response Method',
    'Archive snapshots and structured JSON event data, support HTTP upload, human verification, false-positive correction, and knowledge-base feedback.',
    'Derived from PREVENX Edge AI Construction Safety Recognition System V1.0 architecture document.'
) AS seed
WHERE admin.LoginID = 'admin';

DELETE logic
FROM bee_EdgeAiLogic AS logic
INNER JOIN bee_Project AS project ON project.id = logic.ProjectId
INNER JOIN bee_Admin AS admin ON admin.id = project.AdminId
WHERE admin.LoginID = 'admin'
  AND logic.LogicName IN ('RTSP Region Overlay Monitor', 'PREVENX Edge AI Safety System');

INSERT INTO bee_EdgeAiLogic (ProjectId, LogicName, Description)
SELECT project.id,
  'PREVENX Edge AI Safety System',
  'PREVENX V1.0 MVP construction safety Edge AI logic for video ingestion, PPE recognition, worker tracking, ReID, BLE association, zone risk analysis, risk classification, alerts, review, and learning feedback.'
FROM bee_Project AS project
INNER JOIN bee_Admin AS admin ON admin.id = project.AdminId
WHERE admin.LoginID = 'admin'
  AND NOT EXISTS (
    SELECT 1
    FROM bee_EdgeAiLogic AS existing
    WHERE existing.ProjectId = project.id
      AND existing.LogicName = 'PREVENX Edge AI Safety System'
  );

INSERT INTO bee_EdgeAiCodeVersion
  (LogicId, VersionName, Description, IsCurrent, PackageSizeBytes, FileCount, DirectoryStructure, FeatureList, Notes)
SELECT logic.id,
  '1.0',
  'PREVENX Edge AI V1.0 MVP package for construction-site PPE detection, BLE wristband binding, danger-zone recognition, alert routing, review, and learning feedback.',
  1,
  248000000,
  14,
  'PREVENX Edge AI Construction Safety Recognition System V1.0/
  main_test.py - runtime orchestration, RTSP connection, frame processing, tracking, risk evaluation
  logic/yolo_ppe_detector.py - YOLOv8 person and PPE detection
  logic/reid_engine.py - OSNet-AIN feature extraction and similarity matching
  logic/identity_manager.py - RID generation, candidate confirmation, visual profile persistence
  logic/safety_rules.py - missing PPE rules, full PPE rules, polygon danger zones, BLE-person pairing
  logic/bracelet_gateway.py - MQTT gateway integration, BLE MAC/RSSI ingestion, wristband vibration/beep commands
  logic/openai_checker.py - OpenAI semantic verification for PPE, real-person, and high-work scene checks
  logic/alarm_manager.py - alarm cooldown, snapshot archive, JSON event export, email and HTTP upload integration
  bytetrack_persistent.yaml - persistent tracking configuration
  README_REID.md / test_reid_engine.py - ReID validation and testing utilities
  requirements.txt - YOLO, TorchReID, OpenCV, MQTT, FastAPI, and runtime dependencies

Runtime flow:
  Site data collection -> AI ingestion/preprocessing -> YOLO PPE detection -> tracking/ReID/RID -> BLE association -> zone and PPE rules -> OpenAI semantic verification -> risk classification -> alarm/snapshot/export -> human review -> knowledge base and model feedback',
  'Logic call mapping:
  1. main_test.main / open_rtsp / connect_rtsp_with_retry: acquire real-time RTSP frames with reconnect handling.
  2. main_test.process_frame / track_frame: run frame preprocessing, person detection, tracking, and the safety-analysis pipeline.
  3. YoloPpeDetector.detect_persons / detect_ppe / detect_all: detect workers and PPE classes.
  4. ReIDEngine.extract / similarity plus identity_manager.match_or_create / commit_candidate: create and maintain persistent RID worker identities.
  5. BraceletGateway.start / _on_message / _on_connect: ingest BLE bracelet MAC/RSSI events over MQTT.
  6. safety_rules.pair_persons_with_bracelets / apply_bracelet_pairs: map bracelets to tracked workers.
  7. safety_rules.get_missing_ppe / has_full_ppe / check_high_work_background / draw_scaffolding_overlay: evaluate PPE compliance and danger-zone entry.
  8. openai_checker.check_ppe_missing / check_real_person / check_high_work: provide semantic verification for ambiguous cases.
  9. main_test.check_and_report_missing_ppe / apply_ppe_missing_confirmation: classify risk and confirm missing-PPE events.
  10. alarm_manager.should_alarm / save_person_alarm / save_scene_check / _write_json / send_email_alarm: apply cooldown, generate snapshots, export JSON, and notify.
  11. BraceletGateway.send_ring / send_vibrate_and_beep: trigger worker-level on-site notification.

Rule implementation mapping:
  Environment recognition: RTSP cameras, BLE gateways, MQTT, and scene-quality checks provide site context.
  Recognition content: worker, PPE, danger zone, scaffold/high-work, unauthorized visitor, abnormal event, BLE bracelet, and RID identity signals.
  Recognition logic: YOLOv8, BoT-SORT/ByteTrack, OSNet-AIN ReID, polygon zone rules, bracelet association, OpenAI semantic verification, and alarm cooldown.
  Event recognition: Level 1 missing PPE or danger-zone entry, Level 2 unauthorized visitor or repeated unsafe behavior, Level 3 fire/collapse/severe injury/major incident.
  Event response: BLE vibration/on-site warning for Level 1, administrator email/app notification for Level 2, executive emergency escalation for Level 3, plus snapshot, JSON, HTTP upload, human review, knowledge-base update, and training feedback.',
  'Seeded from PREVENX Edge AI Construction Safety Recognition System V1.0 MVP architecture document.'
FROM bee_EdgeAiLogic AS logic
INNER JOIN bee_Project AS project ON project.id = logic.ProjectId
INNER JOIN bee_Admin AS admin ON admin.id = project.AdminId
WHERE admin.LoginID = 'admin'
  AND logic.LogicName = 'PREVENX Edge AI Safety System';

INSERT INTO bee_DeviceCatalog (CatalogName, Description, SortOrder, IsActive)
VALUES
  ('RTSP Camera', 'Network camera reachable through an RTSP stream.', 10, 1),
  ('Bluetooth Gateway', 'Bluetooth gateway device for nearby sensor collection.', 20, 1)
ON DUPLICATE KEY UPDATE
  Description = VALUES(Description),
  SortOrder = VALUES(SortOrder),
  IsActive = VALUES(IsActive);
