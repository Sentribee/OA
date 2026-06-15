UPDATE bee_Project
SET ProjectName = 'Sentribee OA',
    ProjectDescription = 'Enterprise operations platform for registered businesses, covering CRM, staff, attendance, leave, payroll hours, workflow approvals, knowledge and customer service automation.',
    WebsiteUrl = 'https://oa.sentribee.ai',
    CompanyName = 'SentriBee',
    ProjectKind = 'SentribeeCrm',
    UpdatedAtUtc = UTC_TIMESTAMP(6)
WHERE ProjectName IN ('Sentribee CRM', 'SentriBee CRM', 'crm.sentribee.ai')
   OR WebsiteUrl IN ('https://crm.sentribee.ai', 'http://crm.sentribee.ai', 'crm.sentribee.ai');
