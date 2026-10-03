// Azure hosting for stockroom (validated with `bicep build`; never deployed by this repo).
//
//   App Service (Linux, .NET 8) --user-assigned identity--> Key Vault (DB connection string, API key)
//          |                                              ^
//          +--> Azure Database for PostgreSQL Flexible ---+ (connection string stored as a secret)
//          +--> Application Insights (workspace-based, via OpenTelemetry)
//
// Deploy (example):
//   az group create -n rg-stockroom-dev -l canadacentral
//   az deployment group create -g rg-stockroom-dev -f infra/main.bicep -p infra/main.bicepparam

targetScope = 'resourceGroup'

@description('Short name used to derive resource names, e.g. "stockroom".')
@minLength(3)
@maxLength(12)
param appName string = 'stockroom'

@description('Deployment environment; part of resource names and tags.')
@allowed(['dev', 'test', 'prod'])
param environmentName string = 'dev'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('App Service plan SKU. B1 is the cheapest tier that supports always-on and health checks.')
param appServiceSku string = 'B1'

@description('PostgreSQL administrator login name.')
param postgresAdminLogin string = 'inventoryadmin'

@description('PostgreSQL administrator password. Supply at deploy time; never commit it.')
@secure()
@minLength(12)
param postgresAdminPassword string

@description('API key for write endpoints. Supply at deploy time; stored only in Key Vault.')
@secure()
@minLength(24)
param writeApiKey string

@description('Apply EF Core migrations when the app starts. Prefer a migration bundle in the release pipeline for prod.')
param migrateOnStartup bool = environmentName != 'prod'

var suffix = uniqueString(resourceGroup().id, appName, environmentName)
var tags = {
  application: appName
  environment: environmentName
}
var databaseName = 'inventory'
// Built-in role: Key Vault Secrets User (read secret values).
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

// ---------- Observability ----------
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${appName}-${environmentName}-${suffix}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${appName}-${environmentName}-${suffix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ---------- Database ----------
resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2022-12-01' = {
  name: 'psql-${appName}-${environmentName}-${suffix}'
  location: location
  tags: tags
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: '16'
    administratorLogin: postgresAdminLogin
    administratorLoginPassword: postgresAdminPassword
    storage: { storageSizeGB: 32 }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: { mode: 'Disabled' }
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2022-12-01' = {
  parent: postgres
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

// Allows connections from Azure services (including App Service outbound IPs).
// A production setup would use VNet integration + private access instead.
resource allowAzureServices 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2022-12-01' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// ---------- Secrets ----------
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: take('kv-${appName}-${suffix}', 24)
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: environmentName == 'prod' ? true : null
  }
}

resource connectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'inventory-db-connection'
  properties: {
    value: 'Host=${postgres.properties.fullyQualifiedDomainName};Database=${databaseName};Username=${postgresAdminLogin};Password=${postgresAdminPassword};Ssl Mode=Require'
  }
}

resource apiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'inventory-write-api-key'
  properties: {
    value: writeApiKey
  }
}

// ---------- Identity ----------
// A user-assigned identity exists before the web app, so its Key Vault role assignment can be created
// (and start propagating) before the site first starts and resolves its Key Vault references. With a
// system-assigned identity the grant can only happen after the site exists, so the first boot can see the
// literal "@Microsoft.KeyVault(...)" strings and fail until a restart.
resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${appName}-${environmentName}-${suffix}'
  location: location
  tags: tags
}

// The app identity may read secret values (needed to resolve the Key Vault references below).
resource appReadsSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, appIdentity.id, keyVaultSecretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------- Compute ----------
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'asp-${appName}-${environmentName}-${suffix}'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: appServiceSku }
  properties: {
    reserved: true // Linux
  }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'app-${appName}-${environmentName}-${suffix}'
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${appIdentity.id}': {} }
  }
  // Grant first, then start the site (RBAC propagation can still lag by minutes; App Service retries
  // unresolved Key Vault references, and a restart forces a refresh).
  dependsOn: [appReadsSecrets]
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: appIdentity.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/health/ready'
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: environmentName == 'prod' ? 'Production' : 'Staging' }
        { name: 'Database__Provider', value: 'Postgres' }
        { name: 'Database__MigrateOnStartup', value: string(migrateOnStartup) }
        { name: 'ConnectionStrings__Inventory', value: '@Microsoft.KeyVault(SecretUri=${connectionStringSecret.properties.secretUri})' }
        { name: 'Auth__ApiKeys__0', value: '@Microsoft.KeyVault(SecretUri=${apiKeySecret.properties.secretUri})' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
      ]
    }
  }
}

output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output keyVaultName string = vault.name
output postgresServerFqdn string = postgres.properties.fullyQualifiedDomainName
