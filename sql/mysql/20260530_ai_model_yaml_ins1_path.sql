UPDATE bee_Project
SET AiModelYamlPath = '/home/ubuntu/sentribee/hobson/data.yaml'
WHERE AiModelYamlPath IS NULL
   OR AiModelYamlPath = ''
   OR AiModelYamlPath = '/sentribee/hobson/data.yaml';
