INSERT INTO bee_Project
  (AdminId, ProjectName, ProjectDescription, LogoUrl, CompanyName, WebsiteUrl, Visibility, TimeZoneId)
SELECT admin.id,
  'SpendBee',
  'SpendBee consumer app for receipt recognition, restaurant photo sharing, same-table collaboration, split bills, and user spending behavior analytics.',
  NULL,
  'SpendBee',
  NULL,
  'Private',
  'Pacific/Auckland'
FROM bee_Admin AS admin
WHERE admin.LoginID = 'admin'
  AND NOT EXISTS (
    SELECT 1
    FROM bee_Project AS existing
    WHERE existing.AdminId = admin.id
      AND existing.ProjectName = 'SpendBee'
  );

INSERT INTO bee_ProjectMember (ProjectId, AdminId, Role)
SELECT project.id, project.AdminId, 'Administrator'
FROM bee_Project AS project
INNER JOIN bee_Admin AS admin ON admin.id = project.AdminId
WHERE admin.LoginID = 'admin'
  AND project.ProjectName = 'SpendBee'
ON DUPLICATE KEY UPDATE Role = VALUES(Role);

INSERT INTO bee_ProjectRule (ProjectId, Dimension, RuleText, SourcePrompt)
SELECT project.id, seed.Dimension, seed.RuleText, seed.SourcePrompt
FROM bee_Project AS project
INNER JOIN bee_Admin AS admin ON admin.id = project.AdminId
INNER JOIN (
  SELECT 'Receipt Recognition' AS Dimension,
    'Recognize merchant, purchase date, currency, tax, total, and line items from consumer receipts and invoices.' AS RuleText,
    'Derived from SpendBee product requirements.' AS SourcePrompt
  UNION ALL SELECT 'Receipt Recognition',
    'Run multi-pass recognition and field-level validation so the estimated receipt error rate stays below 1%.',
    'Derived from SpendBee product requirements.'
  UNION ALL SELECT 'Photo Sharing',
    'Support restaurant-based public photo feeds with dish names, tips, warnings, and visibility controls.',
    'Derived from SpendBee product requirements.'
  UNION ALL SELECT 'Photo Sharing',
    'Allow same-table users who are currently using SpendBee to opt in to shared photo sessions and image collage generation.',
    'Derived from SpendBee product requirements.'
  UNION ALL SELECT 'Split Bills',
    'Record AA split bills where one user pays first and other participants settle later; payment integration can be added after MVP.',
    'Derived from SpendBee product requirements.'
  UNION ALL SELECT 'Behavior Analytics',
    'Analyze each user spending behavior by merchant, category, dish, frequency, and time period using confirmed receipts and interaction events.',
    'Derived from SpendBee product requirements.'
) AS seed
WHERE admin.LoginID = 'admin'
  AND project.ProjectName = 'SpendBee'
  AND NOT EXISTS (
    SELECT 1
    FROM bee_ProjectRule AS existingRule
    WHERE existingRule.ProjectId = project.id
      AND existingRule.RuleText = seed.RuleText
  );
