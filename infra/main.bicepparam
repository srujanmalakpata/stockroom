using 'main.bicep'

// Secrets come from the deploying shell's environment, never from source control:
//   export POSTGRES_ADMIN_PASSWORD='...' WRITE_API_KEY="$(openssl rand -base64 32)"
param appName = 'stockroom'
param environmentName = 'dev'
param postgresAdminPassword = readEnvironmentVariable('POSTGRES_ADMIN_PASSWORD')
param writeApiKey = readEnvironmentVariable('WRITE_API_KEY')
