SET @crm_project_id := (
  SELECT id FROM bee_Project WHERE ProjectName = 'crm.sentribee.ai' LIMIT 1
);

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
  KEY IX_bee_AppUserVerification_Project_Email (ProjectId, Email, Purpose, ExpiresAtUtc),
  CONSTRAINT FK_bee_AppUserVerification_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;

SET @sql := IF(
  EXISTS (
    SELECT 1
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_AppUserVerificationCode' AND COLUMN_NAME = 'PhoneNumber'
  ),
  'ALTER TABLE bee_AppUserVerificationCode MODIFY PhoneNumber VARCHAR(40) NULL',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_AppUserVerificationCode' AND COLUMN_NAME = 'Email'
  ),
  'ALTER TABLE bee_AppUserVerificationCode ADD COLUMN Email VARCHAR(150) NULL AFTER PhoneNumber',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1
    FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_AppUserVerificationCode' AND INDEX_NAME = 'IX_bee_AppUserVerification_Project_Email'
  ),
  'ALTER TABLE bee_AppUserVerificationCode ADD KEY IX_bee_AppUserVerification_Project_Email (ProjectId, Email, Purpose, ExpiresAtUtc)',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

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

SET @sql := IF(
  NOT EXISTS (
    SELECT 1
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmIndustry' AND COLUMN_NAME = 'ChatGuidance'
  ),
  'ALTER TABLE bee_CrmIndustry ADD COLUMN ChatGuidance TEXT NULL AFTER Description',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmIndustry' AND COLUMN_NAME = 'ProfileDimensionTemplate'
  ),
  'ALTER TABLE bee_CrmIndustry ADD COLUMN ProfileDimensionTemplate TEXT NULL AFTER ChatGuidance',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

INSERT INTO bee_CrmIndustry
  (ProjectId, Name, Slug, Description, ChatGuidance, ProfileDimensionTemplate, SortOrder, IsActive)
SELECT @crm_project_id, seed.Name, seed.Slug, seed.Description, seed.ChatGuidance, seed.ProfileDimensionTemplate, seed.SortOrder, 1
FROM (
  SELECT 'Chinese Restaurant & Takeaway' AS Name, 'chinese-restaurant-takeaway' AS Slug,
    'NZ Chinese restaurants, takeaway shops, bubble tea, bakery, hotpot, BBQ, yum cha, catering and private dining.' AS Description,
    'Prioritize menu, allergens, opening hours, booking, group size, delivery/pickup, catering, dietary needs, parking and current promotions. If the customer drifts to unrelated topics, acknowledge briefly and guide back to ordering, booking, menu choice or visit planning.' AS ChatGuidance,
    'Capture dine-in/takeaway/delivery, preferred cuisine, date/time, party size, suburb, budget per person, dietary restrictions, spice level, occasion, contact phone, language preference and next action.' AS ProfileDimensionTemplate,
    10 AS SortOrder
  UNION ALL SELECT 'Asian Grocery & Supermarket', 'asian-grocery-supermarket',
    'Asian supermarkets, Chinese grocery stores, fresh produce, frozen food, snacks, herbs, hotpot supplies and imported goods.',
    'Focus on product availability, brand, stock, price range, store location, delivery, preorder, substitutions and membership offers. Pull unrelated questions back to product search, shopping list or store visit.',
    'Capture product category, exact item/brand, quantity, suburb, pickup/delivery preference, budget, urgency, substitution tolerance, contact method and recurring shopping needs.',
    20
  UNION ALL SELECT 'Real Estate Sales', 'real-estate-sales',
    'NZ residential and commercial real estate agencies serving Chinese-speaking buyers, sellers and investors.',
    'Focus on buyer/seller intent, suburb, budget, property type, bedrooms, school zones, financing readiness, viewing time and appraisal needs. Steer unrelated topics back to property goals and next viewing/appraisal step.',
    'Capture buyer/seller/investor, target suburbs, budget, deposit/finance status, bedrooms, school zone, move timeline, must-haves, deal breakers, preferred language, contact details and decision makers.',
    30
  UNION ALL SELECT 'Rental & Property Management', 'rental-property-management',
    'Property managers, rental agencies, boarding houses and landlord services.',
    'Focus on rental availability, tenancy requirements, viewing schedule, documents, move-in date, maintenance requests and landlord management enquiries. Redirect unrelated discussion to tenancy or property management outcome.',
    'Capture tenant/landlord role, suburb, property type, rent budget, move date, household size, pets, visa/work status when relevant, maintenance issue, urgency, viewing availability and contact details.',
    40
  UNION ALL SELECT 'Immigration & Legal Advisory', 'immigration-legal-advisory',
    'Immigration advisers, legal clinics and document support businesses. The assistant should not provide legal advice beyond merchant-approved knowledge.',
    'Collect matter type, visa/legal category, deadlines, current status and consultation need. Avoid giving legal conclusions; guide toward booking a consultation and required documents.',
    'Capture visa/legal category, applicant status, nationality, deadline, family members, employer/school context, documents held, preferred consultation time, language, urgency and contact details.',
    50
  UNION ALL SELECT 'Education, Tutoring & Study Agency', 'education-tutoring-study-agency',
    'Tutoring centres, study abroad agencies, after-school programmes, IELTS/PTE, school enrolment and university pathway services.',
    'Focus on learner age, subject, level, target exam/school, timetable, location/online, trial lesson and parent concerns. Bring unrelated topics back to study goal and assessment booking.',
    'Capture student age/year level, subject, current level, target score/school, timeframe, location, online/offline preference, parent contact, budget, pain points and trial lesson availability.',
    60
  UNION ALL SELECT 'Travel, Tours & Ticketing', 'travel-tours-ticketing',
    'Inbound/outbound travel agencies, local NZ tours, airport transfers, visa travel support and ticketing.',
    'Focus on destination, dates, group size, budget, itinerary, language guide, transport, accommodation and booking constraints. Redirect to trip planning or quote collection.',
    'Capture destination, travel dates, group size, ages, budget, preferred style, must-see places, transport, accommodation level, visa/passport constraints, language and contact details.',
    70
  UNION ALL SELECT 'Beauty, Spa & Cosmetic Clinic', 'beauty-spa-cosmetic-clinic',
    'Beauty salons, hair, nails, lashes, massage, spa, skin management and cosmetic treatment clinics.',
    'Focus on service type, concerns, appointment time, contraindications, price range, practitioner preference and aftercare. Avoid medical claims and guide back to consultation/booking.',
    'Capture service interest, skin/hair/body concern, preferred date/time, budget, prior treatments, allergies/contraindications, event deadline, preferred staff/language and booking contact.',
    80
  UNION ALL SELECT 'Health, Dental & Chinese Medicine', 'health-dental-chinese-medicine',
    'GP clinics, dental clinics, physiotherapy, acupuncture, TCM and allied health providers. The assistant should triage and book, not diagnose.',
    'Focus on appointment type, symptoms at a high level, urgency, location, insurance/ACC, language and booking. For urgent or emergency symptoms, advise contacting emergency services.',
    'Capture service type, broad symptom/need, urgency, preferred clinic/location, appointment time, patient age, ACC/insurance, language, contact details and whether emergency escalation is needed.',
    90
  UNION ALL SELECT 'Accounting, Tax, Mortgage & Insurance', 'accounting-tax-mortgage-insurance',
    'Accountants, tax agents, mortgage brokers, insurance brokers and financial service providers.',
    'Focus on service category, income/business type, deadlines, loan/insurance goal, documents, consultation booking and compliance reminders. Avoid financial advice beyond approved knowledge.',
    'Capture individual/business, service need, deadline, income/business type, loan amount or cover need when volunteered, documents ready, urgency, preferred appointment and contact details.',
    100
  UNION ALL SELECT 'Automotive Sales, Repair & WOF', 'automotive-sales-repair-wof',
    'Car dealers, mechanics, WOF/COF, detailing, tyres, panel beaters and car rental.',
    'Focus on vehicle make/model/year, service need, symptoms, booking time, location, budget and parts availability. Pull unrelated topics back to vehicle issue, quote or appointment.',
    'Capture vehicle make/model/year, rego if provided, service/repair need, symptoms, urgency, budget, location, preferred date/time, contact phone and photo availability.',
    110
  UNION ALL SELECT 'Construction, Renovation & Trades', 'construction-renovation-trades',
    'Builders, renovations, electricians, plumbers, painters, flooring, roofing, landscaping and maintenance.',
    'Focus on job type, property location, scope, measurements/photos, urgency, budget, site visit and quote. Redirect broad discussion to project details and inspection booking.',
    'Capture property type, suburb, job category, scope, measurements/photos, budget, deadline, access constraints, owner/tenant role, consent concerns and site visit availability.',
    120
  UNION ALL SELECT 'Logistics, Moving & Courier', 'logistics-moving-courier',
    'Moving companies, courier, freight forwarding, China-NZ shipping, storage and delivery services.',
    'Focus on pickup/drop-off, dates, item volume, fragile/special goods, stairs/lift, customs, storage and quote. Keep the conversation on shipment or move planning.',
    'Capture service type, pickup/drop-off suburbs, date/time, item list, volume, stairs/lift/access, fragile goods, storage need, budget, urgency and contact details.',
    130
  UNION ALL SELECT 'E-commerce, Import & Retail Brand', 'ecommerce-import-retail-brand',
    'Online stores, importers, WeChat stores, local retail brands and wholesale suppliers.',
    'Focus on product fit, availability, shipping, returns, warranty, wholesale MOQ, payment and order conversion. Redirect unrelated topics back to product selection or order support.',
    'Capture product interest, quantity, personal/wholesale use, delivery suburb, budget, decision timeline, objections, preferred channel, contact details and repeat purchase potential.',
    140
) AS seed
WHERE @crm_project_id IS NOT NULL
ON DUPLICATE KEY UPDATE
  Name = VALUES(Name),
  Description = VALUES(Description),
  ChatGuidance = VALUES(ChatGuidance),
  ProfileDimensionTemplate = VALUES(ProfileDimensionTemplate),
  SortOrder = VALUES(SortOrder),
  IsActive = 1;

UPDATE bee_CrmIndustry
SET IsActive = 0
WHERE ProjectId = @crm_project_id
  AND Slug IN ('retail-ecommerce', 'professional-services', 'health-wellness', 'education-training', 'real-estate', 'hospitality-travel', 'automotive-services', 'home-trades');

UPDATE bee_CrmMerchant AS merchant
INNER JOIN bee_CrmIndustry AS industry ON industry.id = merchant.IndustryId
SET
  merchant.ProfileGuidanceInstructions = COALESCE(
    merchant.ProfileGuidanceInstructions,
    CONCAT(
      'Naturally learn the customer profile during chat for a New Zealand Chinese business in ', industry.Name, '. ',
      'Ask only one useful follow-up question at a time and guide the customer toward the next action. ',
      COALESCE(industry.ChatGuidance, industry.Description, '')
    )
  ),
  merchant.ProfileDimensionFocus = COALESCE(
    merchant.ProfileDimensionFocus,
    CONCAT(
      'Core dimensions: name, contact method, language preference, location/suburb, customer type, intent, urgency, budget, timeline, decision role, objections, preferences, sentiment and next step. ',
      'Industry-specific dimensions for ', industry.Name, ': ',
      COALESCE(industry.ProfileDimensionTemplate, industry.Description, '')
    )
  )
WHERE merchant.ProjectId = @crm_project_id;
