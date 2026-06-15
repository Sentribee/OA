# SentriBee Console

SentriBee Console is an ASP.NET Core Razor Pages administration application
intended for deployment on Windows Server.

## Current Features

- `.NET 10` web application using the imported administration theme.
- Purple SentriBee login and dashboard branding.
- Cookie-authenticated dashboard access through `/login`.
- Administrator authentication backed by MySQL 8.x table `bee_Admin`.
- Automatic upgrade of a successfully verified legacy password to an ASP.NET
  Core PBKDF2 password hash.
- Administrator profile editing with Tencent COS avatar uploads.
- Per-administrator project settings, project logo uploads, and saved rules.
- Project-level Edge AI Git repository configuration, defaulting to
  `https://github.com/Sentribee/Sentribee-edge.git` on branch `main`.
- OpenAI-assisted generation of dimension-based project rules.

## Database Configuration

The data layer uses MySQL 8.x through `MySqlConnector`. Initialize a local
database with:

```powershell
mysql -u <user> -p < .\sql\mysql\schema.sql
```

Use a MySQL connection string, for example:

```text
Server=localhost;Port=3306;Database=sentribee;User=<user>;Password=<password>;SslMode=None;AllowPublicKeyRetrieval=True;TreatTinyAsBoolean=true;
```

The repository does not contain the database password. For local development,
set the connection string in .NET Secret Manager:

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<connection-string>" --project .\src\SentribeeConsole.Web\SentribeeConsole.Web.csproj
```

On Windows Server, configure the application environment variable:

```powershell
[Environment]::SetEnvironmentVariable("ConnectionStrings__DefaultConnection", "<connection-string>", "Machine")
```

Restart the application process after creating or changing the machine
environment variable.

Image uploads use Tencent Cloud COS. Keep credentials out of committed
configuration and provide them on Windows Server as environment variables:

```powershell
[Environment]::SetEnvironmentVariable("CosStorage__SecretId", "<secret-id>", "Machine")
[Environment]::SetEnvironmentVariable("CosStorage__SecretKey", "<secret-key>", "Machine")
[Environment]::SetEnvironmentVariable("CosStorage__AppId", "1320851884", "Machine")
[Environment]::SetEnvironmentVariable("CosStorage__Bucket", "res-1320851884", "Machine")
[Environment]::SetEnvironmentVariable("CosStorage__Region", "ap-guangzhou", "Machine")
[Environment]::SetEnvironmentVariable("CosStorage__PublicBaseUrl", "https://res.kiigou.com/", "Machine")
```

Project rule generation uses the OpenAI Responses API. Provide the API key
only through Secret Manager or server environment configuration:

```powershell
dotnet user-secrets set "OpenAI:ApiKey" "<openai-api-key>" --project .\src\SentribeeConsole.Web\SentribeeConsole.Web.csproj
dotnet user-secrets set "OpenAI:Model" "gpt-5.4-mini" --project .\src\SentribeeConsole.Web\SentribeeConsole.Web.csproj
dotnet user-secrets set "OpenAI:BaseUrl" "https://api.openai.com/v1/" --project .\src\SentribeeConsole.Web\SentribeeConsole.Web.csproj

[Environment]::SetEnvironmentVariable("OpenAI__ApiKey", "<openai-api-key>", "Machine")
[Environment]::SetEnvironmentVariable("OpenAI__Model", "gpt-5.4-mini", "Machine")
[Environment]::SetEnvironmentVariable("OpenAI__BaseUrl", "https://api.openai.com/v1/", "Machine")
```

If the Windows Server network cannot reach the official OpenAI endpoint, set
`OpenAI__BaseUrl` to an approved compatible endpoint available from that
server.

## Start Development

```powershell
dotnet restore .\SentribeeConsole.sln
dotnet run --project .\src\SentribeeConsole.Web\SentribeeConsole.Web.csproj
```

Open `/login` and sign in with an administrator record from `bee_Admin`.
The dashboard at `/dashboard` requires an authenticated session.

## Structure

```text
src/SentribeeConsole.Web/
  Domain/Entities/                   Admin domain records
  Application/Contracts/             Service and repository interfaces
  Application/Services/              Authentication workflow
  Infrastructure/Repositories/       MySQL data access
  Infrastructure/OpenAI/              OpenAI project rule generator
  Infrastructure/Storage/            Tencent COS file storage adapter
  Pages/Account/                      Login and logout endpoints
  Pages/Settings/Profile.cshtml       Administrator profile management
  Pages/Settings/Project.cshtml       Project details and rule management
  Pages/Dashboard/                    Protected administration pages
  Pages/Shared/                       Login and dashboard theme layouts
  wwwroot/theme/                      Imported theme runtime assets
```
