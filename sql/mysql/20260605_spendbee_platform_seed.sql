INSERT INTO bee_SpendBeePlatform
  (ProjectId, Name, DisplayName, NormalizedName, PlatformType, LogoUrl, WebsiteUrl, CountryOrRegion, KnownAliasesJson, SourceJson)
SELECT project.id,
  'Uber Eats',
  'Uber Eats',
  'ubereats',
  'FoodDelivery',
  'https://upload.wikimedia.org/wikipedia/commons/b/b3/Uber_Eats_2020_logo.svg',
  'https://www.ubereats.com/nz',
  'Auckland, New Zealand',
  JSON_ARRAY('Uber', 'Uber Eats', 'UberEats', 'ubereats', '优步外卖', '優步外賣'),
  JSON_OBJECT(
    'seed', '20260605_spendbee_platform_seed',
    'logoSource', 'https://commons.wikimedia.org/wiki/File:Uber_Eats_2020_logo.svg'
  )
FROM bee_Project AS project
WHERE project.ProjectName = 'SpendBee'
ON DUPLICATE KEY UPDATE
  Name = VALUES(Name),
  DisplayName = VALUES(DisplayName),
  PlatformType = VALUES(PlatformType),
  LogoUrl = VALUES(LogoUrl),
  WebsiteUrl = VALUES(WebsiteUrl),
  CountryOrRegion = VALUES(CountryOrRegion),
  KnownAliasesJson = VALUES(KnownAliasesJson),
  SourceJson = VALUES(SourceJson),
  UpdatedAtUtc = UTC_TIMESTAMP(6);

INSERT INTO bee_SpendBeePlatform
  (ProjectId, Name, DisplayName, NormalizedName, PlatformType, LogoUrl, WebsiteUrl, CountryOrRegion, KnownAliasesJson, SourceJson)
SELECT project.id,
  'foodpanda',
  '熊猫外卖',
  'foodpanda',
  'FoodDelivery',
  'https://upload.wikimedia.org/wikipedia/commons/7/74/Foodpanda_wordmark.svg',
  'https://www.foodpanda.com/',
  'Auckland, New Zealand',
  JSON_ARRAY('foodpanda', 'Foodpanda', '熊猫外卖', '熊貓外賣', '熊猫', '富胖达', '富胖達', 'pandamart'),
  JSON_OBJECT(
    'seed', '20260605_spendbee_platform_seed',
    'logoSource', 'https://commons.wikimedia.org/wiki/File:Foodpanda_wordmark.svg'
  )
FROM bee_Project AS project
WHERE project.ProjectName = 'SpendBee'
ON DUPLICATE KEY UPDATE
  Name = VALUES(Name),
  DisplayName = VALUES(DisplayName),
  PlatformType = VALUES(PlatformType),
  LogoUrl = VALUES(LogoUrl),
  WebsiteUrl = VALUES(WebsiteUrl),
  CountryOrRegion = VALUES(CountryOrRegion),
  KnownAliasesJson = VALUES(KnownAliasesJson),
  SourceJson = VALUES(SourceJson),
  UpdatedAtUtc = UTC_TIMESTAMP(6);
