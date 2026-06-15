ALTER TABLE bee_Project
  ADD COLUMN EdgeAiGitRepositoryUrl VARCHAR(500) NOT NULL DEFAULT 'https://github.com/Sentribee/Sentribee-edge.git' AFTER WebsiteUrl,
  ADD COLUMN EdgeAiGitBranch VARCHAR(100) NOT NULL DEFAULT 'main' AFTER EdgeAiGitRepositoryUrl,
  ADD COLUMN EdgeAiGitWorkingDirectory VARCHAR(500) NULL AFTER EdgeAiGitBranch;

UPDATE bee_Project
SET EdgeAiGitRepositoryUrl = 'https://github.com/Sentribee/Sentribee-edge.git',
    EdgeAiGitBranch = 'main'
WHERE EdgeAiGitRepositoryUrl = '' OR EdgeAiGitBranch = '';
