targetScope = 'resourceGroup'

@description('Globally unique Azure Communication Services resource name.')
@minLength(1)
@maxLength(63)
param communicationServiceName string

@description('Azure Communication Services data-at-rest geography.')
param communicationDataLocation string = 'United States'

resource communicationService 'Microsoft.Communication/communicationServices@2025-05-01' = {
  name: communicationServiceName
  location: 'global'
  properties: {
    dataLocation: communicationDataLocation
    disableLocalAuth: false
    publicNetworkAccess: 'Enabled'
  }
}

output communicationServiceId string = communicationService.id
